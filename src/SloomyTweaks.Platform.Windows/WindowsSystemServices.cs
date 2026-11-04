using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using SloomyTweaks.Core.Platform;
using static SloomyTweaks.Platform.Windows.Interop.NativeMethods;

namespace SloomyTweaks.Platform.Windows;

/// <summary>Registry access through Microsoft.Win32.Registry, always using the 64-bit view.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryAccess : IRegistryAccess
{
    public RegistryData? Read(RegistryRoot root, string path, string name)
    {
        using RegistryKey baseKey = Open(root);
        using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
        if (key is null) return null;
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistryData(RegistryKind.DWord, unchecked((uint)(int)value)),
            RegistryValueKind.String or RegistryValueKind.ExpandString => new RegistryData(RegistryKind.String, (string)value),
            _ => new RegistryData(RegistryKind.String, value.ToString() ?? string.Empty),
        };
    }

    public void WriteDword(RegistryRoot root, string path, string name, uint value)
    {
        using RegistryKey baseKey = Open(root);
        using RegistryKey key = baseKey.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create registry key {path}.");
        key.SetValue(name, unchecked((int)value), RegistryValueKind.DWord);
    }

    public void WriteString(RegistryRoot root, string path, string name, string value)
    {
        using RegistryKey baseKey = Open(root);
        using RegistryKey key = baseKey.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create registry key {path}.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(RegistryRoot root, string path, string name)
    {
        using RegistryKey baseKey = Open(root);
        using RegistryKey? key = baseKey.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static RegistryKey Open(RegistryRoot root) => RegistryKey.OpenBaseKey(
        root == RegistryRoot.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        RegistryView.Registry64);
}

/// <summary>Power schemes through the documented PowrProf API (no powercfg.exe shelling).</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsPowerManagement : IPowerManagement
{
    public Guid GetActiveScheme()
    {
        uint rc = PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr);
        if (rc != ERROR_SUCCESS) throw new Win32Exception((int)rc, "PowerGetActiveScheme failed.");
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public void SetActiveScheme(Guid scheme)
    {
        uint rc = PowerSetActiveScheme(IntPtr.Zero, in scheme);
        if (rc != ERROR_SUCCESS) throw new Win32Exception((int)rc, $"PowerSetActiveScheme({scheme}) failed.");
    }

    public IReadOnlyList<PowerScheme> ListSchemes()
    {
        var result = new List<PowerScheme>();
        byte* buffer = stackalloc byte[16];
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buffer, ref size);
            if (rc == ERROR_NO_MORE_ITEMS) break;
            if (rc != ERROR_SUCCESS) throw new Win32Exception((int)rc, "PowerEnumerate failed.");
            var id = new Guid(new ReadOnlySpan<byte>(buffer, 16));
            result.Add(new PowerScheme(id, ReadName(id)));
        }
        return result;
    }

    public uint? ReadAcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        uint rc = PowerReadACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, out uint value);
        return rc == ERROR_SUCCESS ? value : null;
    }

    public void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value)
    {
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, in scheme, in subgroup, in setting, value);
        if (rc != ERROR_SUCCESS) throw new Win32Exception((int)rc, "PowerWriteACValueIndex failed.");
    }

    public static string ReadName(Guid scheme)
    {
        uint size = 0;
        uint rc = PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (rc != ERROR_SUCCESS || size == 0) return scheme.ToString("D");
        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            rc = PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, p, ref size);
        }
        return rc == ERROR_SUCCESS ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : scheme.ToString("D");
    }
}

/// <summary>SystemParametersInfo(SPI_GETMOUSE/SPI_SETMOUSE): the API behind "Enhance pointer precision".</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsMouseSettings : IMouseSettings
{
    public MouseParameters Get()
    {
        int* values = stackalloc int[3];
        if (!SystemParametersInfoW(SPI_GETMOUSE, 0, values, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETMOUSE failed.");
        return new MouseParameters(values[0], values[1], values[2]);
    }

    public void Set(MouseParameters value)
    {
        int* values = stackalloc int[3];
        values[0] = value.Threshold1;
        values[1] = value.Threshold2;
        values[2] = value.Acceleration;
        // Persist to the user profile and broadcast WM_SETTINGCHANGE so the change applies immediately.
        if (!SystemParametersInfoW(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETMOUSE failed.");
    }
}

/// <summary>STICKYKEYS / FILTERKEYS / TOGGLEKEYS flags through SystemParametersInfo.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsAccessibilitySettings : IAccessibilitySettings
{
    public AccessibilityFlags Get()
    {
        var sticky = new FLAGSONLY { cbSize = (uint)sizeof(FLAGSONLY) };
        var toggle = new FLAGSONLY { cbSize = (uint)sizeof(FLAGSONLY) };
        var filter = new FILTERKEYS { cbSize = (uint)sizeof(FILTERKEYS) };
        Check(SystemParametersInfoW(SPI_GETSTICKYKEYS, sticky.cbSize, &sticky, 0), "SPI_GETSTICKYKEYS");
        Check(SystemParametersInfoW(SPI_GETFILTERKEYS, filter.cbSize, &filter, 0), "SPI_GETFILTERKEYS");
        Check(SystemParametersInfoW(SPI_GETTOGGLEKEYS, toggle.cbSize, &toggle, 0), "SPI_GETTOGGLEKEYS");
        return new AccessibilityFlags(sticky.dwFlags, filter.dwFlags, toggle.dwFlags);
    }

    public void Set(AccessibilityFlags flags)
    {
        // Read the full structures first so every other member (timings etc.) is written back unchanged.
        var sticky = new FLAGSONLY { cbSize = (uint)sizeof(FLAGSONLY) };
        var toggle = new FLAGSONLY { cbSize = (uint)sizeof(FLAGSONLY) };
        var filter = new FILTERKEYS { cbSize = (uint)sizeof(FILTERKEYS) };
        Check(SystemParametersInfoW(SPI_GETSTICKYKEYS, sticky.cbSize, &sticky, 0), "SPI_GETSTICKYKEYS");
        Check(SystemParametersInfoW(SPI_GETFILTERKEYS, filter.cbSize, &filter, 0), "SPI_GETFILTERKEYS");
        Check(SystemParametersInfoW(SPI_GETTOGGLEKEYS, toggle.cbSize, &toggle, 0), "SPI_GETTOGGLEKEYS");
        sticky.dwFlags = flags.StickyKeys;
        filter.dwFlags = flags.FilterKeys;
        toggle.dwFlags = flags.ToggleKeys;
        const uint persist = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;
        Check(SystemParametersInfoW(SPI_SETSTICKYKEYS, sticky.cbSize, &sticky, persist), "SPI_SETSTICKYKEYS");
        Check(SystemParametersInfoW(SPI_SETFILTERKEYS, filter.cbSize, &filter, persist), "SPI_SETFILTERKEYS");
        Check(SystemParametersInfoW(SPI_SETTOGGLEKEYS, toggle.cbSize, &toggle, persist), "SPI_SETTOGGLEKEYS");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), what + " failed.");
    }
}

/// <summary>
/// TRIM through the documented `fsutil behavior` command (there is no public API for this setting).
/// fsutil requires administrator rights; queries without them return null.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSystemBehavior : IFileSystemBehavior
{
    private static readonly string Fsutil = Path.Combine(Environment.SystemDirectory, "fsutil.exe");

    public int? QueryNtfsDisableDeleteNotify()
    {
        var (code, output) = Run("behavior", "query", "DisableDeleteNotify");
        return code == 0 ? ParseNtfs(output) : null;
    }

    public void SetNtfsDisableDeleteNotify(int value)
    {
        if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
        // Documented form: applies to NTFS (and ReFS v1).
        var (code, output) = Run("behavior", "set", "disabledeletenotify", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (code != 0) throw new InvalidOperationException($"fsutil exited with code {code}: {output.Trim()}");
    }

    /// <summary>Accepts "NTFS DisableDeleteNotify = 0 (...)" (Windows 10+) and "DisableDeleteNotify = 0" (older).</summary>
    public static int? ParseNtfs(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"NTFS\s+DisableDeleteNotify\s*=\s*([01])");
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(output, @"^\s*DisableDeleteNotify\s*=\s*([01])", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value[0] - '0' : null;
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Fsutil)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("fsutil could not be started.");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException("fsutil did not respond within 10 seconds.");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegeService : IPrivilegeService
{
    // With UAC, an unelevated administrator token is filtered and IsInRole returns false.
    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>Creates System Restore points through SRSetRestorePointW. Requires administrator rights and System Protection enabled.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestorePointService : IRestorePointService
{
    public RestorePointResult TryCreate(string description)
    {
        try
        {
            var begin = new RESTOREPOINTINFOW
            {
                dwEventType = BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = MODIFY_SETTINGS,
                llSequenceNumber = 0,
                szDescription = description.Length > 255 ? description[..255] : description,
            };
            if (!SRSetRestorePointW(ref begin, out STATEMGRSTATUS status))
            {
                // Windows also throttles restore point creation (by default one per 24 hours).
                string reason = status.nStatus switch
                {
                    1058 => "System Protection is disabled",          // ERROR_SERVICE_DISABLED
                    5 => "administrator rights are required",           // ERROR_ACCESS_DENIED
                    _ => $"Windows returned status {status.nStatus}",
                };
                return new RestorePointResult(false, reason);
            }
            var end = new RESTOREPOINTINFOW
            {
                dwEventType = END_SYSTEM_CHANGE,
                dwRestorePtType = MODIFY_SETTINGS,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = string.Empty,
            };
            SRSetRestorePointW(ref end, out _);
            return new RestorePointResult(true, "Restore point created.");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new RestorePointResult(false, "System Restore is not available on this edition");
        }
    }
}

/// <summary>Registers Sloomy Tweaks in HKCU\...\Run. Runs unelevated, so no UAC prompt at sign-in.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sloomy Tweaks";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\" --minimized", RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
