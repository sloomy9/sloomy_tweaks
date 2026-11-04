namespace SloomyTweaks.Core.Platform;

// Abstractions over operating-system state. The Windows implementations live in
// SloomyTweaks.Platform.Windows; tests use in-memory fakes. Keeping the optimization engine
// behind these interfaces is what makes apply/verify/rollback testable without a live system.

public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
}

public enum RegistryKind
{
    DWord,
    String,
}

public sealed record RegistryData(RegistryKind Kind, object Value);

public interface IRegistryAccess
{
    /// <summary>Returns null when the key or value does not exist.</summary>
    RegistryData? Read(RegistryRoot root, string path, string name);
    void WriteDword(RegistryRoot root, string path, string name, uint value);
    void WriteString(RegistryRoot root, string path, string name, string value);
    /// <summary>Deletes a value; no error if it is already absent.</summary>
    void DeleteValue(RegistryRoot root, string path, string name);
}

public sealed record PowerScheme(Guid Id, string Name);

public interface IPowerManagement
{
    Guid GetActiveScheme();
    void SetActiveScheme(Guid scheme);
    IReadOnlyList<PowerScheme> ListSchemes();
    /// <summary>PowerReadACValueIndex. Null when the setting does not exist on this system.</summary>
    uint? ReadAcValue(Guid scheme, Guid subgroup, Guid setting);
    /// <summary>PowerWriteACValueIndex. Callers re-activate the scheme so the change takes effect.</summary>
    void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value);
}

/// <summary>dwFlags of STICKYKEYS, FILTERKEYS and TOGGLEKEYS (SystemParametersInfo).</summary>
public readonly record struct AccessibilityFlags(uint StickyKeys, uint FilterKeys, uint ToggleKeys)
{
    /// <summary>SKF_HOTKEYACTIVE / FKF_HOTKEYACTIVE / TKF_HOTKEYACTIVE all use bit 0x4.</summary>
    public const uint HotkeyActive = 0x4;

    public bool AnyShortcutActive => ((StickyKeys | FilterKeys | ToggleKeys) & HotkeyActive) != 0;

    public AccessibilityFlags WithoutShortcuts() =>
        new(StickyKeys & ~HotkeyActive, FilterKeys & ~HotkeyActive, ToggleKeys & ~HotkeyActive);

    /// <summary>Copies only the hotkey bits from <paramref name="source"/>; every other flag is kept.</summary>
    public AccessibilityFlags WithShortcutBitsFrom(AccessibilityFlags source) => new(
        (StickyKeys & ~HotkeyActive) | (source.StickyKeys & HotkeyActive),
        (FilterKeys & ~HotkeyActive) | (source.FilterKeys & HotkeyActive),
        (ToggleKeys & ~HotkeyActive) | (source.ToggleKeys & HotkeyActive));

    public override string ToString() => $"{StickyKeys},{FilterKeys},{ToggleKeys}";

    public static bool TryParse(string? text, out AccessibilityFlags value)
    {
        value = default;
        string[]? p = text?.Split(',');
        if (p is not { Length: 3 } || !uint.TryParse(p[0], out uint a) || !uint.TryParse(p[1], out uint b) || !uint.TryParse(p[2], out uint c))
            return false;
        value = new AccessibilityFlags(a, b, c);
        return true;
    }
}

public interface IAccessibilitySettings
{
    AccessibilityFlags Get();
    void Set(AccessibilityFlags flags);
}

/// <summary>NTFS delete notifications (TRIM) through the documented `fsutil behavior` interface.</summary>
public interface IFileSystemBehavior
{
    /// <summary>NTFS DisableDeleteNotify: 0 = TRIM enabled, 1 = disabled. Null if it could not be queried.</summary>
    int? QueryNtfsDisableDeleteNotify();
    void SetNtfsDisableDeleteNotify(int value);
}

/// <summary>The three values behind SPI_GETMOUSE / SPI_SETMOUSE.</summary>
public readonly record struct MouseParameters(int Threshold1, int Threshold2, int Acceleration)
{
    /// <summary>"Enhance pointer precision" is on when the acceleration value is non-zero.</summary>
    public bool EnhancePointerPrecision => Acceleration != 0;
    public override string ToString() => $"{Threshold1},{Threshold2},{Acceleration}";

    public static bool TryParse(string? text, out MouseParameters value)
    {
        value = default;
        string[]? parts = text?.Split(',');
        if (parts is not { Length: 3 }) return false;
        if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b) || !int.TryParse(parts[2], out int c))
            return false;
        value = new MouseParameters(a, b, c);
        return true;
    }
}

public interface IMouseSettings
{
    MouseParameters Get();
    void Set(MouseParameters value);
}

public interface IPrivilegeService
{
    bool IsElevated { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record RestorePointResult(bool Created, string Message);

public interface IRestorePointService
{
    RestorePointResult TryCreate(string description);
}
