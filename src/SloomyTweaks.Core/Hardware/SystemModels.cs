namespace SloomyTweaks.Core.Hardware;

public enum CpuVendor
{
    Unknown,
    Intel,
    Amd,
    Other,
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Microsoft,
    Other,
}

public sealed record GpuInfo(string Name, GpuVendor Vendor, string? DriverVersion, ulong? DedicatedMemoryBytes);

/// <summary>
/// Facts about the running system used for compatibility decisions. Every value is read from
/// Windows at runtime; "unknown" is represented explicitly rather than guessed.
/// </summary>
public sealed record SystemContext
{
    public required int WindowsBuild { get; init; }
    public int WindowsUbr { get; init; }
    public string? WindowsDisplayVersion { get; init; }
    public string? WindowsEdition { get; init; }
    public required string CpuName { get; init; }
    public required CpuVendor CpuVendor { get; init; }
    public int? PhysicalCores { get; init; }
    public required int LogicalProcessors { get; init; }
    /// <summary>True when Windows reports more than one core efficiency class (Intel P/E cores).</summary>
    public bool? IsHybridCpu { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public ulong? TotalPhysicalMemoryBytes { get; init; }
    public DateTimeOffset? LastBootTime { get; init; }
    public bool IsElevated { get; init; }

    public bool IsWindows11 => WindowsBuild >= 22000;

    /// <summary>LTSC editions ship without the Microsoft Store and Xbox Game Bar.</summary>
    public bool IsLtsc =>
        WindowsEdition?.Contains("LTSC", StringComparison.OrdinalIgnoreCase) == true ||
        WindowsEdition?.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) == true ||
        WindowsEdition?.Contains("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsAmdX3D => CpuVendor == CpuVendor.Amd && CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);

    public bool HasGpuFrom(GpuVendor vendor) => Gpus.Any(g => g.Vendor == vendor);
}

/// <summary>Snapshot shown on the dashboard. Null means Windows did not provide the value.</summary>
public sealed record SystemSnapshot
{
    public string? CpuName { get; init; }
    public int? PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }
    public string? CpuArchitecture { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public ulong? TotalPhysicalMemoryBytes { get; init; }
    public string? MotherboardManufacturer { get; init; }
    public string? MotherboardProduct { get; init; }
    public string? BiosVersion { get; init; }
    public string? WindowsProductName { get; init; }
    public string? WindowsEdition { get; init; }
    public string? WindowsDisplayVersion { get; init; }
    public int WindowsBuild { get; init; }
    public int WindowsUbr { get; init; }
    public string? ActivePowerPlan { get; init; }
    public TimeSpan Uptime { get; init; }
}

public readonly record struct UsageSample(double CpuPercent, double MemoryPercent, ulong MemoryUsedBytes, ulong MemoryTotalBytes);

public interface IHardwareInfoProvider
{
    SystemSnapshot GetSnapshot();
    SystemContext GetContext();
}

public interface IUsageSampler
{
    /// <summary>CPU utilization since the previous call (first call returns 0 for CPU).</summary>
    UsageSample Sample();
}

public static class ByteFormat
{
    public static string ToGiB(ulong? bytes) =>
        bytes is { } b ? $"{b / 1024d / 1024d / 1024d:0.#} GB" : "Unknown";
}
