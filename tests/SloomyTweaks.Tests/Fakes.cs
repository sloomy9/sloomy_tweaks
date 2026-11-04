using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Licensing;
using SloomyTweaks.Core.Platform;
using SloomyTweaks.Core.Processes;
using SloomyTweaks.Presentation.Services;

namespace SloomyTweaks.Tests;

public sealed class FakeRegistry : IRegistryAccess
{
    public readonly Dictionary<string, RegistryData> Values = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, bool>? FailWrite { get; set; }
    /// <summary>Simulates a policy that silently reverts writes.</summary>
    public bool IgnoreWrites { get; set; }
    public int Writes { get; private set; }

    public static string Key(RegistryRoot root, string path, string name) => $"{root}\\{path}\\{name}";

    public RegistryData? Read(RegistryRoot root, string path, string name) => Values.GetValueOrDefault(Key(root, path, name));

    public void WriteDword(RegistryRoot root, string path, string name, uint value) => Write(Key(root, path, name), new RegistryData(RegistryKind.DWord, value));
    public void WriteString(RegistryRoot root, string path, string name, string value) => Write(Key(root, path, name), new RegistryData(RegistryKind.String, value));

    public void DeleteValue(RegistryRoot root, string path, string name)
    {
        if (!IgnoreWrites) Values.Remove(Key(root, path, name));
    }

    private void Write(string key, RegistryData data)
    {
        Writes++;
        if (FailWrite?.Invoke(key) == true) throw new UnauthorizedAccessException($"Access to {key} denied (simulated).");
        if (!IgnoreWrites) Values[key] = data;
    }
}

public sealed class FakePower : IPowerManagement
{
    public Guid Active { get; set; } = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public List<PowerScheme> Schemes { get; } = new()
    {
        new(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "Balanced"),
        new(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "High performance"),
    };
    public Guid GetActiveScheme() => Active;
    public void SetActiveScheme(Guid scheme) => Active = scheme;
    public IReadOnlyList<PowerScheme> ListSchemes() => Schemes;
}

public sealed class FakeMouse : IMouseSettings
{
    public MouseParameters Value { get; set; } = new(6, 10, 1); // Windows default: acceleration on
    public MouseParameters Get() => Value;
    public void Set(MouseParameters value) => Value = value;
}

public sealed class FakePrivileges : IPrivilegeService
{
    public FakePrivileges(bool elevated) => IsElevated = elevated;
    public bool IsElevated { get; set; }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
}

public sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class FakeRestorePoints : IRestorePointService
{
    public int Calls { get; private set; }
    public RestorePointResult TryCreate(string description) { Calls++; return new RestorePointResult(true, "ok"); }
}

public static class TestSystems
{
    public static SystemContext Win11(int build = 26100, string cpu = "AMD Ryzen 7 7800X3D 8-Core Processor", CpuVendor vendor = CpuVendor.Amd,
        string edition = "Professional", bool? hybrid = false, DateTimeOffset? boot = null) => new()
    {
        WindowsBuild = build,
        WindowsEdition = edition,
        CpuName = cpu,
        CpuVendor = vendor,
        PhysicalCores = 8,
        LogicalProcessors = 16,
        IsHybridCpu = hybrid,
        Gpus = new[] { new GpuInfo("NVIDIA GeForce RTX 4070", GpuVendor.Nvidia, "32.0.15.6094", 12UL << 30) },
        TotalPhysicalMemoryBytes = 32UL << 30,
        LastBootTime = boot ?? new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
    };
}

public sealed class FakeHardware : IHardwareInfoProvider
{
    public SystemContext Context { get; set; } = TestSystems.Win11();
    public SystemContext GetContext() => Context;
    public SystemSnapshot GetSnapshot() => new()
    {
        CpuName = Context.CpuName,
        PhysicalCores = Context.PhysicalCores,
        LogicalProcessors = Context.LogicalProcessors,
        CpuArchitecture = "X64",
        Gpus = Context.Gpus,
        TotalPhysicalMemoryBytes = Context.TotalPhysicalMemoryBytes,
        MotherboardManufacturer = "Test Board Co.",
        MotherboardProduct = "B650",
        WindowsProductName = "Windows 11 Pro",
        WindowsDisplayVersion = "24H2",
        WindowsBuild = Context.WindowsBuild,
        WindowsUbr = 2161,
        ActivePowerPlan = "Balanced",
        Uptime = TimeSpan.FromHours(3),
    };
}

public sealed class FakeUsage : IUsageSampler
{
    public UsageSample Sample() => new(12.5, 40, 12UL << 30, 32UL << 30);
}

public sealed class FakeProcesses : IProcessServiceInfo
{
    public IReadOnlyList<ProcessEntry> GetProcesses() => new[] { new ProcessEntry(4, "System", null, 1 << 20, true), new ProcessEntry(100, "FortniteClient-Win64-Shipping", 35, 4L << 30, false) };
    public IReadOnlyList<StartupEntry> GetStartupEntries() => new[] { new StartupEntry("Discord", "\"C:\\Discord\\Update.exe\"", "HKCU\\Run") };
    public IReadOnlyList<ServiceEntry> GetServices() => new[] { new ServiceEntry("wuauserv", "Windows Update", "Running", "Manual", Array.Empty<string>(), true) };
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public List<string> Confirmations { get; } = new();
    public string? NextOpenFile { get; set; }
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        Confirmations.Add(title + "\n" + message);
        return Task.FromResult(ConfirmResult);
    }
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    public string? PickOpenFile(string title, string filter) => NextOpenFile;
    public string? PickSaveFile(string title, string filter, string defaultFileName) => null;
    public void OpenUrl(string url) { }
    public void OpenFolder(string path) { }
}

public sealed class FakeAppActions : IAppActions
{
    public string ApplicationVersion => "0.1.0-test";
    public string LogDirectory => Path.GetTempPath();
    public string DataDirectory => Path.GetTempPath();
    public bool IsElevated { get; set; }
    public bool RestartElevated() => false;
    public bool StartWithWindows { get; private set; }
    public bool IsStartWithWindowsEnabled() => StartWithWindows;
    public void SetStartWithWindows(bool enabled) => StartWithWindows = enabled;
}

public sealed class FakeDevice : IDeviceIdentity
{
    public FakeDevice(string id = "0123456789abcdef0123456789abcdef", string name = "TEST-PC") { DeviceId = id; DeviceName = name; }
    public string DeviceId { get; }
    public string DeviceName { get; }
}
