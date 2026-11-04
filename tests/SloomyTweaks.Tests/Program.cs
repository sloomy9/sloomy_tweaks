using System.Text;
using SloomyTweaks.Core.Optimizations;
using SloomyTweaks.Tests;
using SloomyTweaks.Tests.TestFramework;

// `dotnet run -- --export-evidence docs/EVIDENCE.md` regenerates the evidence registry document
// from the catalog in code, so the documentation can never drift from what the app ships.
if (args.Length == 2 && args[0] == "--export-evidence")
{
    File.WriteAllText(args[1], EvidenceExporter.ToMarkdown());
    Console.WriteLine($"Wrote {args[1]}");
    return 0;
}

return await TestRunner.RunAsync(args);

internal static class EvidenceExporter
{
    public static string ToMarkdown()
    {
        var catalog = TestCatalog.Create(new FakeRegistry(), new FakePower(), new FakeMouse());
        var sb = new StringBuilder();
        sb.AppendLine("# Sloomy Tweaks — Evidence Registry");
        sb.AppendLine();
        sb.AppendLine("Generated from `OptimizationCatalog` (`tests/SloomyTweaks.Tests -- --export-evidence`). Do not edit by hand.");
        sb.AppendLine();
        sb.AppendLine("| Evidence level | Meaning |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Verified | Supported by reproducible measurements relevant to the optimization. |");
        sb.AppendLine("| Documented | Supported by authoritative documentation, without demonstrated gaming performance gains. |");
        sb.AppendLine("| Experimental | Technically plausible but lacking sufficient independent evidence. Never enabled by default. |");
        sb.AppendLine("| Not Recommended | Unsupported, obsolete, dangerous or ineffective. Listed only to explain why it is not offered. |");
        sb.AppendLine();
        sb.AppendLine("| Item | Kind | Evidence | Recommended flow | Admin | Restart |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var o in catalog.OrderBy(o => o.Definition.Kind).ThenBy(o => o.Definition.Category))
        {
            var d = o.Definition;
            bool act = d.Kind == OptimizationKind.Actionable;
            sb.AppendLine($"| [{d.Name}](#{Anchor(d.Name)}) | {d.Kind} | {d.Evidence} | {(d.EligibleForRecommendedFlow ? "Yes" : "No")} | {(act ? (d.RequiresAdministrator ? "Yes" : "No") : "—")} | {(act ? (d.RequiresRestart ? "Yes" : "No") : "—")} |");
        }
        foreach (var o in catalog.OrderBy(o => o.Definition.Kind).ThenBy(o => o.Definition.Category))
        {
            var d = o.Definition;
            sb.AppendLine();
            sb.AppendLine($"## {d.Name}");
            sb.AppendLine();
            sb.AppendLine($"- **Id:** `{d.Id}` · **Category:** {d.Category} · **Kind:** {d.Kind} · **Evidence:** {d.Evidence}");
            if (o.Primitives.Count > 0)
                sb.AppendLine("- **Changes:** " + string.Join("; ", o.Primitives.Select(p => $"`{p.DesiredDescription}`")));
            sb.AppendLine();
            sb.AppendLine($"**What it does.** {d.Description}");
            sb.AppendLine();
            sb.AppendLine($"**Mechanism.** {d.TechnicalMechanism}");
            sb.AppendLine();
            sb.AppendLine($"**Expected benefit.** {d.ExpectedBenefit}");
            sb.AppendLine();
            sb.AppendLine($"**What is not known.** {d.EvidenceNotes}");
            sb.AppendLine();
            sb.AppendLine($"**Compatibility.** {d.CompatibilitySummary}");
            if (d.Risks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Risks.**");
                foreach (string r in d.Risks) sb.AppendLine($"- {r}");
            }
            if (d.ManualSteps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Steps.**");
                int i = 1;
                foreach (string s in d.ManualSteps) sb.AppendLine($"{i++}. {s}");
            }
            sb.AppendLine();
            sb.AppendLine("**Sources.**");
            if (d.Sources.Count == 0) sb.AppendLine("- None found that supports a gaming benefit.");
            foreach (var s in d.Sources)
                sb.AppendLine($"- [{s.Title}]({s.Url}) — {s.Kind}{(s.Note is null ? "" : ". " + s.Note)}");
        }
        return sb.ToString();
    }

    private static string Anchor(string title) =>
        new string(title.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Replace(' ', '-');
}
