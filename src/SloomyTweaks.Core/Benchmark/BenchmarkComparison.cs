namespace SloomyTweaks.Core.Benchmark;

public sealed record BenchmarkRun
{
    public long Id { get; init; }
    /// <summary>Configuration label, e.g. "Baseline" or "After HAGS". Runs with the same label are repeats.</summary>
    public required string ConfigurationLabel { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Application { get; init; }
    /// <summary>Where the data came from (e.g. "PresentMon CSV: capture.csv"). Never "simulated".</summary>
    public required string Source { get; init; }
    public required FrameTimeStats Stats { get; init; }
    public string? SystemSummary { get; init; }
    public string? Notes { get; init; }
}

public interface IBenchmarkRepository
{
    BenchmarkRun Save(BenchmarkRun run);
    IReadOnlyList<BenchmarkRun> List();
    void Delete(long id);
}

public sealed class InMemoryBenchmarkRepository : IBenchmarkRepository
{
    private readonly List<BenchmarkRun> _runs = new();
    private long _next = 1;

    public BenchmarkRun Save(BenchmarkRun run)
    {
        var saved = run with { Id = _next++ };
        _runs.Add(saved);
        return saved;
    }

    public IReadOnlyList<BenchmarkRun> List() => _runs.OrderByDescending(r => r.CreatedAt).ToList();
    public void Delete(long id) => _runs.RemoveAll(r => r.Id == id);
}

public enum ComparisonVerdict
{
    /// <summary>Fewer than the minimum number of runs on either side.</summary>
    InsufficientData,
    NoMeaningfulDifference,
    Improved,
    Regressed,
}

public sealed record MetricComparison(
    string Metric,
    bool HigherIsBetter,
    int BaselineRuns,
    int CandidateRuns,
    double BaselineMean,
    double CandidateMean,
    double BaselineStdDev,
    double CandidateStdDev,
    double Difference,
    double PercentChange,
    double? ConfidenceLow,
    double? ConfidenceHigh,
    double? PValue,
    ComparisonVerdict Verdict,
    string Explanation);

/// <summary>
/// Compares repeated runs of two configurations with Welch's t-test (unequal variances).
/// An improvement is never declared from a single run: at least <see cref="MinimumRunsPerSide"/>
/// runs per configuration are required, and the difference must be significant at α = 0.05.
/// </summary>
public static class BenchmarkComparer
{
    public const int MinimumRunsPerSide = 3;
    public const double Alpha = 0.05;

    public static IReadOnlyList<MetricComparison> Compare(IReadOnlyList<BenchmarkRun> baseline, IReadOnlyList<BenchmarkRun> candidate) => new[]
    {
        CompareMetric("Average FPS", true, baseline.Select(r => r.Stats.AverageFps), candidate.Select(r => r.Stats.AverageFps)),
        CompareMetric("1% low FPS", true, baseline.Select(r => r.Stats.OnePercentLowFps), candidate.Select(r => r.Stats.OnePercentLowFps)),
        CompareMetric("P95 frame time (ms)", false, baseline.Select(r => r.Stats.P95FrameTimeMs), candidate.Select(r => r.Stats.P95FrameTimeMs)),
        CompareMetric("P99 frame time (ms)", false, baseline.Select(r => r.Stats.P99FrameTimeMs), candidate.Select(r => r.Stats.P99FrameTimeMs)),
    };

    public static MetricComparison CompareMetric(string metric, bool higherIsBetter, IEnumerable<double> baselineValues, IEnumerable<double> candidateValues)
    {
        double[] a = baselineValues.ToArray();
        double[] b = candidateValues.ToArray();
        double ma = a.Length > 0 ? a.Average() : double.NaN;
        double mb = b.Length > 0 ? b.Average() : double.NaN;
        double sa = StdDev(a);
        double sb = StdDev(b);
        double diff = mb - ma;
        double pct = ma != 0 && double.IsFinite(ma) ? diff / ma * 100.0 : double.NaN;

        if (a.Length < MinimumRunsPerSide || b.Length < MinimumRunsPerSide)
        {
            return new MetricComparison(metric, higherIsBetter, a.Length, b.Length, ma, mb, sa, sb, diff, pct, null, null, null,
                ComparisonVerdict.InsufficientData,
                $"Record at least {MinimumRunsPerSide} runs of each configuration. A single run cannot separate a real change from run-to-run variation.");
        }

        double se = Math.Sqrt(sa * sa / a.Length + sb * sb / b.Length);
        if (se == 0)
        {
            var exact = diff == 0 ? ComparisonVerdict.NoMeaningfulDifference : (diff > 0 == higherIsBetter ? ComparisonVerdict.Improved : ComparisonVerdict.Regressed);
            return new MetricComparison(metric, higherIsBetter, a.Length, b.Length, ma, mb, sa, sb, diff, pct, diff, diff, diff == 0 ? 1 : 0, exact,
                "All runs produced identical values; this is unusual for real captures — check that different files were imported.");
        }

        double t = diff / se;
        double df = WelchDf(sa, a.Length, sb, b.Length);
        double p = StudentT.TwoSidedPValue(t, df);
        double tCrit = StudentT.InverseTwoSided(Alpha, df);
        double lo = diff - tCrit * se;
        double hi = diff + tCrit * se;

        ComparisonVerdict verdict = p >= Alpha
            ? ComparisonVerdict.NoMeaningfulDifference
            : (diff > 0 == higherIsBetter ? ComparisonVerdict.Improved : ComparisonVerdict.Regressed);

        string explanation = verdict switch
        {
            ComparisonVerdict.NoMeaningfulDifference => $"The difference ({diff:+0.##;-0.##}) is within run-to-run variation (p = {p:0.###}). No change can be claimed.",
            ComparisonVerdict.Improved => $"Improved by {Math.Abs(diff):0.##} ({Math.Abs(pct):0.#}%), 95% CI [{lo:0.##}, {hi:0.##}], p = {p:0.###}.",
            _ => $"Worse by {Math.Abs(diff):0.##} ({Math.Abs(pct):0.#}%), 95% CI [{lo:0.##}, {hi:0.##}], p = {p:0.###}.",
        };
        return new MetricComparison(metric, higherIsBetter, a.Length, b.Length, ma, mb, sa, sb, diff, pct, lo, hi, p, verdict, explanation);
    }

    internal static double StdDev(double[] values)
    {
        if (values.Length < 2) return 0;
        double m = values.Average();
        return Math.Sqrt(values.Sum(v => (v - m) * (v - m)) / (values.Length - 1));
    }

    private static double WelchDf(double sa, int na, double sb, int nb)
    {
        double va = sa * sa / na;
        double vb = sb * sb / nb;
        double num = (va + vb) * (va + vb);
        double den = (va * va / (na - 1)) + (vb * vb / (nb - 1));
        return den == 0 ? na + nb - 2 : num / den;
    }
}

/// <summary>Student's t distribution via the regularized incomplete beta function.</summary>
public static class StudentT
{
    public static double TwoSidedPValue(double t, double df)
    {
        if (double.IsNaN(t)) return 1;
        double x = df / (df + t * t);
        return Math.Clamp(RegularizedIncompleteBeta(x, df / 2.0, 0.5), 0, 1);
    }

    /// <summary>t such that the two-sided tail probability equals <paramref name="alpha"/>.</summary>
    public static double InverseTwoSided(double alpha, double df)
    {
        double lo = 0, hi = 1000;
        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2;
            if (TwoSidedPValue(mid, df) > alpha) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    public static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lnBeta = LogGamma(a + b) - LogGamma(a) - LogGamma(b);
        double front = Math.Exp(Math.Log(x) * a + Math.Log(1 - x) * b + lnBeta);
        return x < (a + 1) / (a + b + 2)
            ? front * ContinuedFraction(x, a, b) / a
            : 1 - front * ContinuedFraction(1 - x, b, a) / b;
    }

    private static double ContinuedFraction(double x, double a, double b)
    {
        const double Tiny = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= 300; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d; h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-12) break;
        }
        return h;
    }

    private static double LogGamma(double x)
    {
        // Lanczos approximation (g = 7, n = 9).
        double[] coef =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
        };
        if (x < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        double sum = coef[0];
        for (int i = 1; i < 9; i++) sum += coef[i] / (x + i);
        double t = x + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }
}
