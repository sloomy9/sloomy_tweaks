using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SloomyTweaks.Core.Processes;
using static SloomyTweaks.Platform.Windows.Interop.NativeMethods;

namespace SloomyTweaks.Platform.Windows;

/// <summary>
/// Read-only process, startup and service inventory. Sloomy Tweaks does not terminate processes
/// or change services in this version; the Processes &amp; Services page is informational.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsProcessServiceInfo : IProcessServiceInfo
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _lastCpu = new();

    public IReadOnlyList<ProcessEntry> GetProcesses()
    {
        var result = new List<ProcessEntry>();
        DateTime now = DateTime.UtcNow;
        var seen = new HashSet<int>();
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                seen.Add(p.Id);
                double? cpu = null;
                long ws = 0;
                try
                {
                    ws = p.WorkingSet64;
                    TimeSpan total = p.TotalProcessorTime;
                    if (_lastCpu.TryGetValue(p.Id, out var last))
                    {
                        double elapsed = (now - last.At).TotalMilliseconds * Environment.ProcessorCount;
                        if (elapsed > 0) cpu = Math.Clamp((total - last.Cpu).TotalMilliseconds * 100.0 / elapsed, 0, 100);
                    }
                    _lastCpu[p.Id] = (total, now);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected/system processes deny access to timing information.
                }
                result.Add(new ProcessEntry(p.Id, p.ProcessName, cpu, ws, ProcessCatalog.IsCritical(p.ProcessName)));
            }
        }
        foreach (int stale in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(stale);
        return result.OrderByDescending(r => r.CpuPercent ?? 0).ThenByDescending(r => r.WorkingSetBytes).ToList();
    }

    public IReadOnlyList<StartupEntry> GetStartupEntries()
    {
        var result = new List<StartupEntry>();
        void ReadRun(RegistryKey root, string scope, string path)
        {
            using RegistryKey? key = root.OpenSubKey(path);
            if (key is null) return;
            foreach (string name in key.GetValueNames())
                result.Add(new StartupEntry(name, key.GetValue(name)?.ToString() ?? string.Empty, $"{scope}\\{path}"));
        }
        using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            ReadRun(hkcu, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run");
        using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            ReadRun(hklm, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run");
        using (RegistryKey hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            ReadRun(hklm32, "HKLM (32-bit)", @"Software\Microsoft\Windows\CurrentVersion\Run");

        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            string dir = Environment.GetFolderPath(folder);
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    result.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, folder.ToString()));
            }
        }
        return result;
    }

    public IReadOnlyList<ServiceEntry> GetServices()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, null, 0, out uint needed, out _, ref resume, null);
            byte[] buffer = new byte[needed + 4096];
            resume = 0;
            uint count;
            fixed (byte* p = buffer)
            {
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, p, (uint)buffer.Length, out needed, out count, ref resume, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumServicesStatusEx failed.");

                var result = new List<ServiceEntry>((int)count);
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                int size = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>((IntPtr)(p + i * size));
                    string name = Marshal.PtrToStringUni(entry.lpServiceName) ?? string.Empty;
                    string display = Marshal.PtrToStringUni(entry.lpDisplayName) ?? name;
                    using RegistryKey? key = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                    int start = key?.GetValue("Start") is int s ? s : -1;
                    string[] deps = key?.GetValue("DependOnService") as string[] ?? Array.Empty<string>();
                    result.Add(new ServiceEntry(name, display, StateName(entry.ServiceStatusProcess.dwCurrentState), StartName(start), deps, ProcessCatalog.IsProtectedService(name)));
                }
                return result.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running", 5 => "Continuing", 6 => "Pausing", 7 => "Paused", _ => "Unknown",
    };

    private static string StartName(int start) => start switch
    {
        0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown",
    };
}
