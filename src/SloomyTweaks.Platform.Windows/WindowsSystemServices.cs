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
