using SloomyTweaks.Core.Hardware;

namespace SloomyTweaks.Core.Optimizations;

public enum EvidenceLevel
{
    /// <summary>Supported by reproducible measurements relevant to the optimization.</summary>
    Verified,
    /// <summary>Supported by authoritative documentation, without demonstrated gaming gains.</summary>
    Documented,
    /// <summary>Technically plausible but lacking sufficient independent performance evidence.</summary>
    Experimental,
    /// <summary>Unsupported, obsolete, dangerous or consistently ineffective.</summary>
    NotRecommended,
}

public enum SourceKind
{
    OfficialDocumentation,
    VendorDocumentation,
    IndependentMeasurement,
    TechnicalReporting,
    CommunityDocumentation,
}

public sealed record EvidenceSource(string Title, string Url, SourceKind Kind, string? Note = null);

public enum OptimizationCategory
{
    Cpu,
    Gpu,
    Memory,
    Windows,
    ProcessesServices,
    Gaming,
    InputUsb,
    Network,
    Storage,
    Power,
}

public enum OptimizationKind
{
    /// <summary>Sloomy Tweaks can detect, apply, verify and restore it.</summary>
    Actionable,
    /// <summary>Cannot be safely managed through a supported API; the card gives verified instructions.</summary>
    Guidance,
    /// <summary>Listed only to explain why Sloomy Tweaks does not offer it.</summary>
    NotOffered,
    /// <summary>A setting with more than two meaningful values; shown as a selector, backed up and restorable.</summary>
    Selector,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

public sealed record SelectorOption(string Value, string Label);

public enum OptimizationState
{
    Enabled,
    Disabled,
    Unsupported,
    Unknown,
    RestartPending,
}

public sealed record OptimizationDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required OptimizationCategory Category { get; init; }
    public required OptimizationKind Kind { get; init; }
    public required string Description { get; init; }
    public required string TechnicalMechanism { get; init; }
    /// <summary>Qualitative only. Never contains invented numbers.</summary>
    public required string ExpectedBenefit { get; init; }
    public required EvidenceLevel Evidence { get; init; }
    /// <summary>What is not known or could not be verified.</summary>
    public required string EvidenceNotes { get; init; }
    public required IReadOnlyList<EvidenceSource> Sources { get; init; }
    public required IReadOnlyList<string> Risks { get; init; }
    public required string CompatibilitySummary { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public bool RequiresAdministrator { get; init; }
    public bool RequiresRestart { get; init; }
    /// <summary>Ids of optimizations that must not be enabled at the same time.</summary>
    public IReadOnlyList<string> ConflictsWith { get; init; } = Array.Empty<string>();
    /// <summary>Step-by-step instructions for Guidance items (and manual alternatives).</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = Array.Empty<string>();

    /// <summary>Only Verified or Documented actionable items may appear in the recommended flow.</summary>
    public bool EligibleForRecommendedFlow =>
        Kind == OptimizationKind.Actionable && Evidence is EvidenceLevel.Verified or EvidenceLevel.Documented;

    /// <summary>Actionable and selector items change the system and support verified rollback.</summary>
    public bool ChangesSystem => Kind is OptimizationKind.Actionable or OptimizationKind.Selector;
}

public enum CompatibilityStatus
{
    Compatible,
    /// <summary>Compatible but the user must acknowledge warnings first.</summary>
    CompatibleWithWarnings,
    /// <summary>Support cannot be confirmed by Sloomy Tweaks; requires explicit acknowledgement.</summary>
    Unverified,
    Unsupported,
}

public sealed record CompatibilityResult(CompatibilityStatus Status, IReadOnlyList<string> Reasons)
{
    public static CompatibilityResult Compatible() => new(CompatibilityStatus.Compatible, Array.Empty<string>());
    public static CompatibilityResult Unsupported(params string[] reasons) => new(CompatibilityStatus.Unsupported, reasons);
    public bool RequiresAcknowledgement => Status is CompatibilityStatus.CompatibleWithWarnings or CompatibilityStatus.Unverified;
}

/// <summary>Accumulates compatibility findings; the most severe status wins.</summary>
public sealed class CompatibilityBuilder
{
    private readonly List<string> _reasons = new();
    private CompatibilityStatus _status = CompatibilityStatus.Compatible;

    public CompatibilityBuilder RequireBuild(SystemContext ctx, int minBuild, string featureName)
    {
        if (ctx.WindowsBuild < minBuild)
            Fail($"{featureName} requires Windows build {minBuild} or newer (detected {ctx.WindowsBuild}).");
        return this;
    }

    public CompatibilityBuilder Warn(string reason) => Raise(CompatibilityStatus.CompatibleWithWarnings, reason);
    public CompatibilityBuilder Unverified(string reason) => Raise(CompatibilityStatus.Unverified, reason);
    public CompatibilityBuilder Fail(string reason) => Raise(CompatibilityStatus.Unsupported, reason);

    private CompatibilityBuilder Raise(CompatibilityStatus status, string reason)
    {
        _reasons.Add(reason);
        if (status > _status) _status = status;
        return this;
    }

    public CompatibilityResult Build() => new(_status, _reasons.ToArray());
}
