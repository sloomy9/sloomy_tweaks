using System.Globalization;
using System.Text;
using SloomyTweaks.Core.Benchmark;
using SloomyTweaks.Core.Network;
using SloomyTweaks.Tests.TestFramework;

namespace SloomyTweaks.Tests;

public class FrameTimeAnalyzerTests
{
    [Fact]
    public void Constant_frame_times_give_exact_values()
    {
        var stats = FrameTimeAnalyzer.Analyze(Enumerable.Repeat(4.0, 1000).ToList());
        Assert.Equal(250.0, stats.AverageFps, 1e-9);
        Assert.Equal(250.0, stats.OnePercentLowFps, 1e-9);
        Assert.Equal(4.0, stats.P99FrameTimeMs, 1e-9);
        Assert.Equal(4.0, stats.DurationSeconds, 1e-9);
        Assert.Equal(1000, stats.Distribution[4]);
    }

    [Fact]
    public void One_percent_low_is_mean_of_slowest_one_percent()
    {
        // 990 frames at 5 ms and 10 frames at 20 ms → the slowest 1% are exactly the 20 ms frames.
        var frames = Enumerable.Repeat(5.0, 990).Concat(Enumerable.Repeat(20.0, 10)).ToList();
        var stats = FrameTimeAnalyzer.Analyze(frames);
        Assert.Equal(50.0, stats.OnePercentLowFps, 1e-9);
        Assert.Equal(1000 / ((990 * 5.0 + 10 * 20.0) / 1000), stats.AverageFps, 1e-9);
        Assert.Equal(20.0, stats.MaxFrameTimeMs, 1e-9);
    }

    [Fact]
    public void Percentile_interpolates_linearly()
    {
        double[] sorted = { 1, 2, 3, 4, 5 };
        Assert.Equal(3.0, FrameTimeAnalyzer.Percentile(sorted, 50), 1e-12);
        Assert.Equal(4.6, FrameTimeAnalyzer.Percentile(sorted, 90), 1e-12);
    }

    [Fact]
    public void Too_few_frames_are_rejected_instead_of_reported()
    {
        Assert.Throws<ArgumentException>(() => FrameTimeAnalyzer.Analyze(Enumerable.Repeat(5.0, 50).ToList()));
    }
}

public class PresentMonParserTests
{
    [Fact]
    public void Parses_presentmon_1x_columns()
    {
        var csv = new StringBuilder("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange\n");
        for (int i = 0; i < 5; i++) csv.AppendLine(FormattableString.Invariant($"FortniteClient-Win64-Shipping.exe,1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,{i * 0.004},4.{i},4.0"));
        FrameCapture c = PresentMonCsvParser.Parse(new StringReader(csv.ToString()));
        Assert.Equal("FortniteClient-Win64-Shipping.exe", c.Application);
        Assert.Equal("MsBetweenPresents", c.SourceColumn);
        Assert.Equal(5, c.FrameTimesMs.Count);
        Assert.Equal(4.3, c.FrameTimesMs[3], 1e-12);
    }

    [Fact]
    public void Parses_presentmon_2x_frametime_and_keeps_dominant_process()
    {
        var csv = new StringBuilder("Application,ProcessID,SwapChainAddress,PresentRuntime,FrameTime,CPUBusy\n");
        for (int i = 0; i < 10; i++) csv.AppendLine("game.exe,1,0x1,DXGI,6.25,2.0");
        csv.AppendLine("dwm.exe,2,0x2,DXGI,16.6,1.0");
        csv.AppendLine("game.exe,1,0x1,DXGI,NA,2.0");
        FrameCapture c = PresentMonCsvParser.Parse(new StringReader(csv.ToString()));
        Assert.Equal("game.exe", c.Application);
        Assert.Equal("FrameTime", c.SourceColumn);
        Assert.Equal(10, c.FrameTimesMs.Count);
        Assert.Equal(1, c.SkippedRows);
    }

    [Fact]
    public void Handles_quoted_fields_and_rejects_files_without_frame_column()
    {
        Assert.True(new[] { "a,b", "c\"d", "" }.SequenceEqual(PresentMonCsvParser.SplitCsv("\"a,b\",\"c\"\"d\",")));
        Assert.Throws<FormatException>(() => PresentMonCsvParser.Parse(new StringReader("Application,Foo\nx,1\n")));
    }
}

public class BenchmarkComparerTests
{
    [Fact]
    public void Student_t_matches_reference_values()
    {
        // Reference: two-sided p for t = 2.0 with 10 df ≈ 0.07339; t critical (α = 0.05, df = 10) ≈ 2.2281.
        Assert.Equal(0.07339, StudentT.TwoSidedPValue(2.0, 10), 1e-4);
        Assert.Equal(2.2281, StudentT.InverseTwoSided(0.05, 10), 1e-3);
        Assert.Equal(1.0, StudentT.TwoSidedPValue(0, 5), 1e-12);
    }

    [Fact]
    public void Fewer_than_three_runs_never_produce_a_verdict()
    {
        var r = BenchmarkComparer.CompareMetric("Average FPS", true, new[] { 200.0, 201 }, new[] { 260.0, 262 });
        Assert.Equal(ComparisonVerdict.InsufficientData, r.Verdict);
        Assert.Null(r.PValue);
    }

    [Fact]
    public void Overlapping_noisy_runs_are_not_called_an_improvement()
    {
        var r = BenchmarkComparer.CompareMetric("Average FPS", true, new[] { 240.0, 252, 246, 238 }, new[] { 245.0, 250, 241, 249 });
        Assert.Equal(ComparisonVerdict.NoMeaningfulDifference, r.Verdict);
    }

    [Fact]
    public void Clear_separation_is_detected_in_the_right_direction()
    {
        var fps = BenchmarkComparer.CompareMetric("Average FPS", true, new[] { 240.0, 241, 239, 240.5 }, new[] { 255.0, 256, 254.5, 255.5 });
        Assert.Equal(ComparisonVerdict.Improved, fps.Verdict);
        Assert.True(fps.ConfidenceLow > 0);

        var p99 = BenchmarkComparer.CompareMetric("P99 frame time (ms)", false, new[] { 8.0, 8.1, 7.9, 8.05 }, new[] { 9.0, 9.1, 8.95, 9.05 });
        Assert.Equal(ComparisonVerdict.Regressed, p99.Verdict);
    }
}

public class NetworkTests
{
    [Fact]
    public void Latency_stats_compute_loss_and_jitter()
    {
        var s = LatencyCalculator.Compute("1.1.1.1", new double?[] { 10, 12, null, 11, 15 });
        Assert.Equal(20.0, s.LossPercent, 1e-12);
        Assert.Equal(12.0, s.AverageMs!.Value, 1e-12);
        Assert.Equal((2 + 1 + 4) / 3.0, s.JitterMs!.Value, 1e-12);
        Assert.Equal(10.0, s.MinMs!.Value, 1e-12);
    }

    [Fact]
    public void Interpretation_separates_local_from_upstream_problems()
    {
        var goodGateway = LatencyCalculator.Compute("192.168.1.1", Enumerable.Repeat((double?)1, 30).ToList());
        var lossyGateway = LatencyCalculator.Compute("192.168.1.1", Enumerable.Repeat((double?)1, 25).Concat(Enumerable.Repeat((double?)null, 5)).ToList());
        var jitteryInternet = LatencyCalculator.Compute("1.1.1.1", Enumerable.Range(0, 30).Select(i => (double?)(i % 2 == 0 ? 10 : 40)).ToList());

        Assert.Contains("local network", NetworkDiagnostics.Interpret(lossyGateway, jitteryInternet));
        Assert.Contains("ISP routing", NetworkDiagnostics.Interpret(goodGateway, jitteryInternet));
        Assert.Contains("not a measurement of Fortnite", LatencyStats.Disclaimer);
    }
}
