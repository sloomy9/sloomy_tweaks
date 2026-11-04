using System.Globalization;
using SloomyTweaks.Core.Platform;

namespace SloomyTweaks.Core.Optimizations;

/// <summary>
/// Serializable snapshot of one system setting. <see cref="Exists"/> = false records that the
/// value was absent, so restoring deletes it instead of writing a guessed default.
/// </summary>
public sealed record SettingValue(bool Exists, string Kind, string? Data)
{
    public static SettingValue Absent(string kind) => new(false, kind, null);
    public override string ToString() => Exists ? $"{Kind}:{Data}" : $"{Kind}:<absent>";
}

/// <summary>
/// One atomic, reversible system change. An optimization is a list of primitives; the engine
/// snapshots, applies, verifies and restores them individually.
/// </summary>
public interface ISettingPrimitive
{
    /// <summary>Stable id used as the backup key, e.g. "HKCU\Software\...\Name".</summary>
    string Id { get; }
    string DesiredDescription { get; }
    SettingValue Read();
    /// <summary>True = desired, false = not desired, null = cannot be determined (Windows default decided by driver/OEM).</summary>
    bool? IsDesired(SettingValue current);
    void ApplyDesired();
    void Restore(SettingValue original);
    /// <summary>True when the current value is equivalent to <paramref name="original"/> for this primitive.</summary>
    bool IsRestored(SettingValue current, SettingValue original);
}

public sealed class RegistryDwordPrimitive : ISettingPrimitive
{
    private readonly IRegistryAccess _registry;
    private readonly RegistryRoot _root;
    private readonly string _path;
    private readonly string _name;
    private readonly uint _desired;
    private readonly uint? _valueWhenAbsent;

    /// <param name="valueWhenAbsent">
    /// The documented Windows default when the value does not exist, or null when the effective
    /// value is decided elsewhere (driver, OEM) and therefore unknown.
    /// </param>
    public RegistryDwordPrimitive(IRegistryAccess registry, RegistryRoot root, string path, string name, uint desired, uint? valueWhenAbsent)
    {
        _registry = registry;
        _root = root;
        _path = path;
        _name = name;
        _desired = desired;
        _valueWhenAbsent = valueWhenAbsent;
    }

    public string Id => $"{(_root == RegistryRoot.CurrentUser ? "HKCU" : "HKLM")}\\{_path}\\{_name}";
    public string DesiredDescription => $"{Id} = {_desired} (REG_DWORD)";

    public SettingValue Read()
    {
        RegistryData? data = _registry.Read(_root, _path, _name);
        return data switch
        {
            null => SettingValue.Absent("dword"),
            { Kind: RegistryKind.DWord } => new SettingValue(true, "dword", Convert.ToUInt32(data.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            // A value of an unexpected type is preserved verbatim so it can be restored.
            _ => new SettingValue(true, "string", data.Value.ToString()),
        };
    }

    public bool? IsDesired(SettingValue current)
    {
        if (!current.Exists) return _valueWhenAbsent is { } v ? v == _desired : null;
        if (current.Kind != "dword") return false;
        return uint.TryParse(current.Data, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) && value == _desired;
    }

    public void ApplyDesired() => _registry.WriteDword(_root, _path, _name, _desired);

    public void Restore(SettingValue original)
    {
        if (!original.Exists)
        {
            _registry.DeleteValue(_root, _path, _name);
        }
        else if (original.Kind == "dword")
        {
            _registry.WriteDword(_root, _path, _name, uint.Parse(original.Data!, CultureInfo.InvariantCulture));
        }
        else
        {
            _registry.WriteString(_root, _path, _name, original.Data ?? string.Empty);
        }
    }

    public bool IsRestored(SettingValue current, SettingValue original) => current == original;
}

/// <summary>
/// Manages one token inside a semicolon-delimited registry string such as
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings</c>
/// (e.g. "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;"). Other tokens are always preserved,
/// so restoring this optimization never undoes unrelated graphics settings.
/// </summary>
public sealed class RegistryStringTokenPrimitive : ISettingPrimitive
{
    private readonly IRegistryAccess _registry;
    private readonly RegistryRoot _root;
    private readonly string _path;
    private readonly string _name;
    private readonly string _token;
    private readonly string _desired;
    private readonly string? _valueWhenAbsent;

    public RegistryStringTokenPrimitive(IRegistryAccess registry, RegistryRoot root, string path, string name, string token, string desired, string? valueWhenAbsent)
    {
        _registry = registry;
        _root = root;
        _path = path;
        _name = name;
        _token = token;
        _desired = desired;
        _valueWhenAbsent = valueWhenAbsent;
    }

    public string Id => $"{(_root == RegistryRoot.CurrentUser ? "HKCU" : "HKLM")}\\{_path}\\{_name}#{_token}";
    public string DesiredDescription => $"{_token}={_desired} inside {_name}";

    public SettingValue Read()
    {
        RegistryData? data = _registry.Read(_root, _path, _name);
        string? full = data?.Value as string;
        if (full is null) return SettingValue.Absent("token");
        var tokens = TokenString.Parse(full);
        return tokens.TryGetValue(_token, out string? v)
            ? new SettingValue(true, "token", v)
            : SettingValue.Absent("token");
    }

    public bool? IsDesired(SettingValue current)
    {
        if (!current.Exists) return _valueWhenAbsent is null ? null : _valueWhenAbsent == _desired;
        return string.Equals(current.Data, _desired, StringComparison.Ordinal);
    }

    public void ApplyDesired() => SetToken(_desired);

    public void Restore(SettingValue original) => SetToken(original.Exists ? original.Data : null);

    public bool IsRestored(SettingValue current, SettingValue original) => current == original;

    private void SetToken(string? value)
    {
        string current = _registry.Read(_root, _path, _name)?.Value as string ?? string.Empty;
        string updated = TokenString.With(current, _token, value);
        if (updated.Length == 0)
            _registry.DeleteValue(_root, _path, _name);
        else
            _registry.WriteString(_root, _path, _name, updated);
    }
}

/// <summary>Parser/serializer for "Key=Value;Key=Value;" strings. Preserves token order.</summary>
public static class TokenString
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    /// <summary>Returns <paramref name="text"/> with <paramref name="key"/> set, or removed when value is null.</summary>
    public static string With(string text, string key, string? value)
    {
        var parts = new List<(string Key, string Value)>();
        bool replaced = false;
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq];
            if (k == key)
            {
                if (value is not null && !replaced) parts.Add((k, value));
                replaced = true;
            }
            else
            {
                parts.Add((k, part[(eq + 1)..]));
            }
        }
        if (!replaced && value is not null) parts.Add((key, value));
        return string.Concat(parts.Select(p => $"{p.Key}={p.Value};"));
    }
}

/// <summary>Active power scheme via PowerGetActiveScheme / PowerSetActiveScheme.</summary>
public sealed class PowerSchemePrimitive : ISettingPrimitive
{
    private readonly IPowerManagement _power;
    private readonly Guid _desired;

    public PowerSchemePrimitive(IPowerManagement power, Guid desired)
    {
        _power = power;
        _desired = desired;
    }

    public string Id => "Power\\ActiveScheme";
    public string DesiredDescription => $"Active power scheme = {_desired}";

    public SettingValue Read() => new(true, "guid", _power.GetActiveScheme().ToString("D"));
    public bool? IsDesired(SettingValue current) => Guid.TryParse(current.Data, out Guid g) && g == _desired;
    public void ApplyDesired() => _power.SetActiveScheme(_desired);
    public void Restore(SettingValue original) => _power.SetActiveScheme(Guid.Parse(original.Data!));
    public bool IsRestored(SettingValue current, SettingValue original) =>
        Guid.TryParse(current.Data, out Guid a) && Guid.TryParse(original.Data, out Guid b) && a == b;
}

/// <summary>"Enhance pointer precision" via SystemParametersInfo(SPI_GETMOUSE/SPI_SETMOUSE).</summary>
public sealed class PointerAccelerationPrimitive : ISettingPrimitive
{
    private readonly IMouseSettings _mouse;

    public PointerAccelerationPrimitive(IMouseSettings mouse) => _mouse = mouse;

    public string Id => "User32\\SPI_MOUSE";
    public string DesiredDescription => "Mouse acceleration = 0 (Enhance pointer precision off)";

    public SettingValue Read() => new(true, "mouse", _mouse.Get().ToString());

    public bool? IsDesired(SettingValue current) =>
        MouseParameters.TryParse(current.Data, out var p) ? !p.EnhancePointerPrecision : null;

    public void ApplyDesired()
    {
        // Only the acceleration flag changes; thresholds are kept so restore is exact.
        MouseParameters current = _mouse.Get();
        _mouse.Set(current with { Acceleration = 0 });
    }

    public void Restore(SettingValue original)
    {
        if (!MouseParameters.TryParse(original.Data, out var p))
            throw new InvalidOperationException($"Stored mouse parameters are invalid: '{original.Data}'.");
        _mouse.Set(p);
    }

    public bool IsRestored(SettingValue current, SettingValue original) => current == original;
}
