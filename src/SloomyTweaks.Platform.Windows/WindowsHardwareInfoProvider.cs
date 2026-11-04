using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SloomyTweaks.Core.Hardware;
using SloomyTweaks.Core.Platform;
using static SloomyTweaks.Platform.Windows.Interop.NativeMethods;

namespace SloomyTweaks.Platform.Windows;

/// <summary>
/// Reads real hardware and OS information from documented sources: the registry hardware
/// description keys populated by Windows at boot, EnumDisplayDevices, GetLogicalProcessorInformationEx,
/// GetPhysicallyInstalledSystemMemory and the power API. No WMI dependency.
/// Values Windows does not provide are returned as null and shown as "Unknown".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHardwareInfoProvider : IHardwareInfoProvider
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private readonly IPowerManagement _power;
    private readonly IPrivilegeService _privileges;
    private readonly Lazy<CpuTopology> _topology = new(ReadTopology);

    public WindowsHardwareInfoProvider(IPowerManagement power, IPrivilegeService privileges)
    {
        _power = power;
        _privileges = privileges;
    }

    public SystemSnapshot GetSnapshot()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var (product, edition, display, build, ubr) = ReadWindowsVersion(hklm);
        using RegistryKey? bios = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");

        string? powerPlan = null;
        try { powerPlan = WindowsPowerManagement.ReadName(_power.GetActiveScheme()); }
        catch (System.ComponentModel.Win32Exception) { }

        return new SystemSnapshot
        {
            CpuName = ReadCpuName(hklm),
            PhysicalCores = _topology.Value.PhysicalCores,
            LogicalProcessors = Environment.ProcessorCount,
            CpuArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            Gpus = ReadGpus(hklm),
            TotalPhysicalMemoryBytes = ReadInstalledMemory(),
            MotherboardManufacturer = Clean(bios?.GetValue("BaseBoardManufacturer") as string),
            MotherboardProduct = Clean(bios?.GetValue("BaseBoardProduct") as string),
            BiosVersion = Clean(bios?.GetValue("BIOSVersion") as string),
            WindowsProductName = product,
            WindowsEdition = edition,
            WindowsDisplayVersion = display,
            WindowsBuild = build,
            WindowsUbr = ubr,
            ActivePowerPlan = powerPlan,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    public SystemContext GetContext()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var (_, edition, display, build, ubr) = ReadWindowsVersion(hklm);
        using RegistryKey? cpu = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string vendorId = cpu?.GetValue("VendorIdentifier") as string ?? string.Empty;

        return new SystemContext
        {
            WindowsBuild = build,
            WindowsUbr = ubr,
            WindowsDisplayVersion = display,
            WindowsEdition = edition,
            CpuName = ReadCpuName(hklm) ?? "Unknown",
            CpuVendor = vendorId switch
            {
                "GenuineIntel" => CpuVendor.Intel,
                "AuthenticAMD" => CpuVendor.Amd,
                "" => CpuVendor.Unknown,
                _ => CpuVendor.Other,
            },
            PhysicalCores = _topology.Value.PhysicalCores,
            LogicalProcessors = Environment.ProcessorCount,
            IsHybridCpu = _topology.Value.EfficiencyClasses is { } classes ? classes > 1 : null,
            Gpus = ReadGpus(hklm),
            TotalPhysicalMemoryBytes = ReadInstalledMemory(),
            LastBootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            IsElevated = _privileges.IsElevated,
        };
    }

    private static (string? Product, string? Edition, string? Display, int Build, int Ubr) ReadWindowsVersion(RegistryKey hklm)
    {
        using RegistryKey? cv = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = int.TryParse(cv?.GetValue("CurrentBuildNumber") as string, out int b) ? b : Environment.OSVersion.Version.Build;
        int ubr = cv?.GetValue("UBR") is int u ? u : 0;
        string? product = cv?.GetValue("ProductName") as string;
        // Windows 11 still reports "Windows 10" in ProductName; the build number is authoritative.
        if (product is not null && build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return (product, cv?.GetValue("EditionID") as string, cv?.GetValue("DisplayVersion") as string, build, ubr);
    }

    private static string? ReadCpuName(RegistryKey hklm)
    {
        using RegistryKey? cpu = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return Clean(cpu?.GetValue("ProcessorNameString") as string);
    }

    private static ulong? ReadInstalledMemory()
    {
        if (GetPhysicallyInstalledSystemMemory(out ulong kb)) return kb * 1024;
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : null;
    }

    /// <summary>
    /// Active adapters come from EnumDisplayDevices; driver version and memory come from the
    /// display class key entry with the matching description. Stale class entries for removed
    /// hardware are ignored because only adapters Windows currently enumerates are reported.
    /// </summary>
    internal static IReadOnlyList<GpuInfo> ReadGpus(RegistryKey hklm)
    {
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> device id
        var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0 && !string.IsNullOrWhiteSpace(device.DeviceString))
                active.TryAdd(device.DeviceString.Trim(), device.DeviceID ?? string.Empty);
            device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        }

        var classInfo = new Dictionary<string, (string? Version, ulong? Memory, string? Match)>(StringComparer.OrdinalIgnoreCase);
        using (RegistryKey? cls = hklm.OpenSubKey(DisplayClassKey))
        {
            if (cls is not null)
            {
                foreach (string sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
                {
                    try
                    {
                        using RegistryKey? k = cls.OpenSubKey(sub);
                        if (k?.GetValue("DriverDesc") is not string desc) continue;
                        ulong? mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                        {
                            long l => (ulong)l,
                            byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                            int n => (ulong)(uint)n,
                            _ => null,
                        };
                        classInfo[desc.Trim()] = (k.GetValue("DriverVersion") as string, mem, k.GetValue("MatchingDeviceId") as string);
                    }
                    catch (System.Security.SecurityException) { /* some subkeys are ACL-protected */ }
                }
            }
        }

        var result = new List<GpuInfo>();
        foreach (var (name, deviceId) in active)
        {
            classInfo.TryGetValue(name, out var info);
            result.Add(new GpuInfo(name, VendorFrom(deviceId + " " + info.Match + " " + name), info.Version, info.Memory));
        }
        return result;
    }

    internal static GpuVendor VendorFrom(string text)
    {
        string t = text.ToUpperInvariant();
        if (t.Contains("VEN_10DE") || t.Contains("NVIDIA")) return GpuVendor.Nvidia;
        if (t.Contains("VEN_1002") || t.Contains("VEN_1022") || t.Contains("AMD") || t.Contains("RADEON")) return GpuVendor.Amd;
        if (t.Contains("VEN_8086") || t.Contains("INTEL")) return GpuVendor.Intel;
        if (t.Contains("MICROSOFT") || t.Contains("BASIC DISPLAY")) return GpuVendor.Microsoft;
        return string.IsNullOrWhiteSpace(t) ? GpuVendor.Unknown : GpuVendor.Other;
    }

    private sealed record CpuTopology(int? PhysicalCores, int? EfficiencyClasses);

    private static CpuTopology ReadTopology()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
        if (length == 0) return new CpuTopology(null, null);
        byte[] buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, p, ref length)) return new CpuTopology(null, null);
        }

        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(int) Size(int) PROCESSOR_RELATIONSHIP{Flags(byte) EfficiencyClass(byte) ...}
        int cores = 0;
        var classes = new HashSet<byte>();
        int offset = 0;
        while (offset + 10 <= length)
        {
            int relationship = BitConverter.ToInt32(buffer, offset);
            int size = BitConverter.ToInt32(buffer, offset + 4);
            if (size <= 0) break;
            if (relationship == RelationProcessorCore)
            {
                cores++;
                classes.Add(buffer[offset + 9]);
            }
            offset += size;
        }
        return new CpuTopology(cores > 0 ? cores : null, classes.Count > 0 ? classes.Count : null);
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // OEM placeholders are not real values.
        return t is "To be filled by O.E.M." or "Default string" or "System Product Name" ? null : t;
    }
}

/// <summary>System-wide CPU and memory utilization from GetSystemTimes and GlobalMemoryStatusEx.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsUsageSampler : IUsageSampler
{
    private long _lastIdle, _lastKernel, _lastUser;
    private bool _primed;

    public UsageSample Sample()
    {
        double cpu = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            if (_primed)
            {
                long idleDelta = idle - _lastIdle;
                long total = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle time
                if (total > 0) cpu = Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
            }
            (_lastIdle, _lastKernel, _lastUser, _primed) = (idle, kernel, user, true);
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!GlobalMemoryStatusEx(ref mem)) return new UsageSample(cpu, 0, 0, 0);
        ulong used = mem.ullTotalPhys - mem.ullAvailPhys;
        return new UsageSample(cpu, mem.ullTotalPhys == 0 ? 0 : used * 100.0 / mem.ullTotalPhys, used, mem.ullTotalPhys);
    }
}
