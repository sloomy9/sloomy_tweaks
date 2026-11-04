using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SloomyTweaks.Core.Diagnostics;
using SloomyTweaks.Core.Platform;
using static SloomyTweaks.Platform.Windows.Interop.NativeMethods;

namespace SloomyTweaks.Platform.Windows;

/// <summary>Read-only system diagnostics from documented Windows sources. Never changes anything.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsSystemDiagnostics : ISystemDiagnostics
{
    // Well-known power setting GUIDs (winnt.h).
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid CoreParkingMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    private readonly IPowerManagement _power;
    private readonly IFileSystemBehavior _fs;

    public WindowsSystemDiagnostics(IPowerManagement power, IFileSystemBehavior fs)
    {
        _power = power;
        _fs = fs;
    }

    public IReadOnlyList<DiagnosticRow> ProcessorPowerPolicy()
    {
        Guid scheme;
        try { scheme = _power.GetActiveScheme(); }
        catch (System.ComponentModel.Win32Exception) { return new[] { new DiagnosticRow("Processor power policy", "Unavailable") }; }

        string Pct(Guid setting) => _power.ReadAcValue(scheme, SubProcessor, setting) is { } v ? $"{v}%" : "Not exposed by this plan";
        string boost = _power.ReadAcValue(scheme, SubProcessor, PerfBoostMode) switch
        {
            0 => "Disabled",
            1 => "Enabled",
            2 => "Aggressive",
            3 => "Efficient enabled",
            4 => "Efficient aggressive",
            5 => "Aggressive at guaranteed",
            6 => "Efficient aggressive at guaranteed",
            null => "Not exposed by this plan",
            { } other => other.ToString(CultureInfo.InvariantCulture),
        };
        var rows = new List<DiagnosticRow>
        {
            new("Minimum processor state (plugged in)", Pct(ProcThrottleMin)),
            new("Maximum processor state (plugged in)", Pct(ProcThrottleMax)),
            new("Core parking: minimum unparked cores", Pct(CoreParkingMinCores)),
            new("Processor boost mode", boost),
        };
        if (_power.ReadAcValue(scheme, SubProcessor, ProcThrottleMax) is { } max && max < 100)
            rows.Add(new DiagnosticRow("Note", "Maximum processor state is below 100%, which caps CPU frequency on this plan.", DiagnosticSeverity.Warning));
        if (_power.ReadAcValue(scheme, SubProcessor, PerfBoostMode) is 0)
            rows.Add(new DiagnosticRow("Note", "Processor boost is disabled on this plan.", DiagnosticSeverity.Warning));
        return rows;
    }

    public IReadOnlyList<DiagnosticRow> Memory()
    {
        var rows = new List<DiagnosticRow>();
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (GlobalMemoryStatusEx(ref status))
        {
            ulong limit = status.ullTotalPageFile; // commit limit = RAM + page files
            ulong charge = limit - status.ullAvailPageFile;
            double pct = limit == 0 ? 0 : charge * 100.0 / limit;
            rows.Add(new DiagnosticRow("Commit charge / limit", $"{Gb(charge)} of {Gb(limit)} ({pct:0}%)",
                pct >= 90 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info));
            if (pct >= 90) rows.Add(new DiagnosticRow("Memory pressure", "Commit charge is close to the limit; applications may fail to allocate memory.", DiagnosticSeverity.Warning));
        }

        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? mm = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        string[] files = (mm?.GetValue("PagingFiles") as string[] ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        rows.Add(DescribePageFile(files));
        return rows;
    }

    /// <summary>Interprets the PagingFiles registry value written by the Virtual Memory dialog.</summary>
    public static DiagnosticRow DescribePageFile(string[] entries)
    {
        if (entries.Length == 0)
            return new DiagnosticRow("Page file", "Disabled. Microsoft recommends a page file for crash dumps and commit headroom.", DiagnosticSeverity.Warning);
        if (entries.Any(e => e.StartsWith("?:", StringComparison.Ordinal)))
            return new DiagnosticRow("Page file", "Managed automatically by Windows (recommended)", DiagnosticSeverity.Good);
        var parts = entries.Select(e =>
        {
            string[] p = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string drive = p[0].Length >= 2 ? p[0][..2] : p[0];
            return p.Length >= 3 && p[1] == "0" && p[2] == "0" ? $"{drive} system managed" : p.Length >= 3 ? $"{drive} custom {p[1]}–{p[2]} MB" : $"{drive}";
        });
        return new DiagnosticRow("Page file", string.Join(", ", parts));
    }

    public IReadOnlyList<DisplayInfo> Displays()
    {
        var result = new List<DisplayInfo>();
        var adapter = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
                if (EnumDisplaySettingsW(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    var monitor = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                    string name = EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString)
                        ? monitor.DeviceString.Trim()
                        : adapter.DeviceName;
                    result.Add(new DisplayInfo(name, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));
                }
            }
            adapter = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        }
        return result;
    }

    public int? PointerSpeed()
    {
        int speed = 0;
        return SystemParametersInfoW(SPI_GETMOUSESPEED, 0, &speed, 0) ? speed : null;
    }

    public string? ReadFortniteSettings()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2_000_000) return null;
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DiagnosticRow TrimStatus()
    {
        int? v;
        try { v = _fs.QueryNtfsDisableDeleteNotify(); }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { v = null; }
        return v switch
        {
            0 => new DiagnosticRow("TRIM (NTFS)", "Enabled", DiagnosticSeverity.Good),
            1 => new DiagnosticRow("TRIM (NTFS)", "Disabled", DiagnosticSeverity.Warning),
            _ => new DiagnosticRow("TRIM (NTFS)", "Unknown — querying requires administrator rights"),
        };
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
}
