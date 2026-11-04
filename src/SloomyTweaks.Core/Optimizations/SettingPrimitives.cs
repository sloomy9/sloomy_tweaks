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

/// <summary>
/// Turns off the keyboard shortcuts that open accessibility prompts during games (Shift ×5 for
/// Sticky Keys, holding right Shift for Filter Keys, holding Num Lock for Toggle Keys). Only the
/// hotkey bits change; whether the features themselves are on is left untouched.
/// </summary>
public sealed class AccessibilityShortcutsPrimitive : ISettingPrimitive
{
    private readonly IAccessibilitySettings _settings;

    public AccessibilityShortcutsPrimitive(IAccessibilitySettings settings) => _settings = settings;

    public string Id => "User32\\SPI_ACCESSIBILITY_HOTKEYS";
    public string DesiredDescription => "Sticky/Filter/Toggle Keys keyboard shortcuts = off (SKF/FKF/TKF_HOTKEYACTIVE cleared)";

    public SettingValue Read() => new(true, "a11y", _settings.Get().ToString());

    public bool? IsDesired(SettingValue current) =>
        AccessibilityFlags.TryParse(current.Data, out var f) ? !f.AnyShortcutActive : null;

    public void ApplyDesired() => _settings.Set(_settings.Get().WithoutShortcuts());

    public void Restore(SettingValue original)
    {
        if (!AccessibilityFlags.TryParse(original.Data, out var o))
            throw new InvalidOperationException($"Stored accessibility flags are invalid: '{original.Data}'.");
        _settings.Set(_settings.Get().WithShortcutBitsFrom(o));
    }

    public bool IsRestored(SettingValue current, SettingValue original) =>
        AccessibilityFlags.TryParse(current.Data, out var c) && AccessibilityFlags.TryParse(original.Data, out var o)
        && (c.StickyKeys & AccessibilityFlags.HotkeyActive) == (o.StickyKeys & AccessibilityFlags.HotkeyActive)
        && (c.FilterKeys & AccessibilityFlags.HotkeyActive) == (o.FilterKeys & AccessibilityFlags.HotkeyActive)
        && (c.ToggleKeys & AccessibilityFlags.HotkeyActive) == (o.ToggleKeys & AccessibilityFlags.HotkeyActive);
}

/// <summary>
/// One AC value of the active power plan (PowerReadACValueIndex / PowerWriteACValueIndex). The scheme
/// the value was read from is stored with it, so restore writes back to that same plan even if the user
/// switched plans in the meantime.
/// </summary>
public sealed class PowerSettingPrimitive : ISettingPrimitive
{
    private readonly IPowerManagement _power;
    private readonly Guid _subgroup;
    private readonly Guid _setting;
    private readonly uint _desired;
    private readonly string _name;

    public PowerSettingPrimitive(IPowerManagement power, Guid subgroup, Guid setting, uint desired, string name)
    {
        _power = power;
        _subgroup = subgroup;
        _setting = setting;
        _desired = desired;
        _name = name;
    }

    public string Id => $"Power\\{_subgroup:D}\\{_setting:D}";
    public string DesiredDescription => $"{_name} (plugged in) = {_desired} on the active power plan";

    public SettingValue Read()
    {
        Guid scheme = _power.GetActiveScheme();
        uint? v = _power.ReadAcValue(scheme, _subgroup, _setting);
        return v is null ? SettingValue.Absent("power") : new SettingValue(true, "power", $"{scheme:D}|{v}");
    }

    public bool? IsDesired(SettingValue current)
    {
        if (!current.Exists) return null;
        return TryParse(current.Data, out _, out uint v) ? v == _desired : null;
    }

    public void ApplyDesired()
    {
        Guid scheme = _power.GetActiveScheme();
        _power.WriteAcValue(scheme, _subgroup, _setting, _desired);
        _power.SetActiveScheme(scheme); // re-activate so the new value takes effect
    }

    public void Restore(SettingValue original)
    {
        if (!original.Exists || !TryParse(original.Data, out Guid scheme, out uint v))
            throw new InvalidOperationException("No original power setting value was recorded.");
        _power.WriteAcValue(scheme, _subgroup, _setting, v);
        if (_power.GetActiveScheme() == scheme) _power.SetActiveScheme(scheme);
    }

    public bool IsRestored(SettingValue current, SettingValue original)
    {
        if (!TryParse(original.Data, out Guid scheme, out uint v)) return false;
        return _power.ReadAcValue(scheme, _subgroup, _setting) == v;
    }

    private static bool TryParse(string? data, out Guid scheme, out uint value)
    {
        scheme = Guid.Empty;
        value = 0;
        string[]? p = data?.Split('|');
        return p is { Length: 2 } && Guid.TryParse(p[0], out scheme) && uint.TryParse(p[1], out value);
    }
}

/// <summary>NTFS delete notifications (TRIM) via `fsutil behavior ... disabledeletenotify`. Desired: 0 (enabled).</summary>
public sealed class TrimPrimitive : ISettingPrimitive
{
    private readonly IFileSystemBehavior _fs;

    public TrimPrimitive(IFileSystemBehavior fs) => _fs = fs;

    public string Id => "Fsutil\\NTFS\\DisableDeleteNotify";
    public string DesiredDescription => "NTFS DisableDeleteNotify = 0 (TRIM enabled)";

    public SettingValue Read() => _fs.QueryNtfsDisableDeleteNotify() is { } v
        ? new SettingValue(true, "fsutil", v.ToString(CultureInfo.InvariantCulture))
        : SettingValue.Absent("fsutil");

    public bool? IsDesired(SettingValue current) => current.Exists ? current.Data == "0" : null;
    public void ApplyDesired() => _fs.SetNtfsDisableDeleteNotify(0);

    public void Restore(SettingValue original)
    {
        if (!original.Exists) return; // the value could not be read before; nothing to put back
        _fs.SetNtfsDisableDeleteNotify(int.Parse(original.Data!, CultureInfo.InvariantCulture));
    }

    public bool IsRestored(SettingValue current, SettingValue original) => !original.Exists || current == original;
}
