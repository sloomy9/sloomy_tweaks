using SloomyTweaks.Core.Hardware;

namespace SloomyTweaks.Core.Optimizations;

/// <summary>An evidence-registry entry plus (for actionable items) the primitives that implement it.</summary>
public sealed class Optimization
{
    private readonly Func<SystemContext, CompatibilityResult> _compatibility;

    public Optimization(
        OptimizationDefinition definition,
        IReadOnlyList<ISettingPrimitive> primitives,
        Func<SystemContext, CompatibilityResult>? compatibility = null,
        SelectorBehavior? selector = null)
    {
        Definition = definition;
        Primitives = primitives;
        Selector = selector;
        _compatibility = compatibility ?? (_ => CompatibilityResult.Compatible());

        if (definition.ChangesSystem && primitives.Count == 0)
            throw new ArgumentException($"Optimization '{definition.Id}' changes the system but has no primitives.");
        if (!definition.ChangesSystem && primitives.Count != 0)
            throw new ArgumentException($"Non-actionable optimization '{definition.Id}' must not have primitives.");
        if ((definition.Kind == OptimizationKind.Selector) != (selector is not null))
            throw new ArgumentException($"Optimization '{definition.Id}': selector behavior must be provided exactly for Selector items.");
    }

    /// <summary>For <see cref="OptimizationKind.Selector"/> items: options, current value and per-value primitives.</summary>
    public SelectorBehavior? Selector { get; }

    public OptimizationDefinition Definition { get; }
    public IReadOnlyList<ISettingPrimitive> Primitives { get; }
    public string Id => Definition.Id;

    public CompatibilityResult CheckCompatibility(SystemContext context) =>
        Definition.ChangesSystem
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

/// <summary>
/// Behavior of a multi-value setting. <see cref="Optimization.Primitives"/> holds one primitive used for
/// reading and restoring the original; <see cref="PrimitivesFor"/> builds the primitives that set a value.
/// </summary>
public sealed record SelectorBehavior(
    Func<IReadOnlyList<SelectorOption>> Options,
    Func<IReadOnlyDictionary<string, SettingValue>, string?> CurrentValue,
    Func<string, IReadOnlyList<ISettingPrimitive>> PrimitivesFor,
    Func<SystemContext, string, CompatibilityResult>? CompatibilityFor = null);
