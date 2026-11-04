using SloomyTweaks.Core.Hardware;

namespace SloomyTweaks.Core.Optimizations;

/// <summary>An evidence-registry entry plus (for actionable items) the primitives that implement it.</summary>
public sealed class Optimization
{
    private readonly Func<SystemContext, CompatibilityResult> _compatibility;

    public Optimization(
        OptimizationDefinition definition,
        IReadOnlyList<ISettingPrimitive> primitives,
        Func<SystemContext, CompatibilityResult>? compatibility = null)
    {
        Definition = definition;
        Primitives = primitives;
        _compatibility = compatibility ?? (_ => CompatibilityResult.Compatible());

        if (definition.Kind == OptimizationKind.Actionable && primitives.Count == 0)
            throw new ArgumentException($"Actionable optimization '{definition.Id}' has no primitives.");
        if (definition.Kind != OptimizationKind.Actionable && primitives.Count != 0)
            throw new ArgumentException($"Non-actionable optimization '{definition.Id}' must not have primitives.");
    }

    public OptimizationDefinition Definition { get; }
    public IReadOnlyList<ISettingPrimitive> Primitives { get; }
    public string Id => Definition.Id;

    public CompatibilityResult CheckCompatibility(SystemContext context) =>
        Definition.Kind == OptimizationKind.Actionable
            ? _compatibility(context)
            : CompatibilityResult.Compatible();

    public IReadOnlyDictionary<string, SettingValue> ReadCurrent() =>
        Primitives.ToDictionary(p => p.Id, p => p.Read());

    /// <summary>Inspects the real configuration. Never assumes anything was applied.</summary>
    public OptimizationState DetectState(IReadOnlyDictionary<string, SettingValue> current)
    {
        bool anyUnknown = false;
        bool allDesired = true;
        foreach (ISettingPrimitive p in Primitives)
        {
            bool? desired = p.IsDesired(current[p.Id]);
            if (desired is null) anyUnknown = true;
            else if (desired == false) allDesired = false;
        }
        if (!allDesired) return OptimizationState.Disabled;
        return anyUnknown ? OptimizationState.Unknown : OptimizationState.Enabled;
    }
}
