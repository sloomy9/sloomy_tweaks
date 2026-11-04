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
