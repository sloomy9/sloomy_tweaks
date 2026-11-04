namespace SloomyTweaks.Core.Processes;

public sealed record ProcessEntry(int Id, string Name, double? CpuPercent, long WorkingSetBytes, bool IsCritical);

public sealed record StartupEntry(string Name, string Command, string Location);

public sealed record ServiceEntry(string Name, string DisplayName, string State, string StartType, IReadOnlyList<string> DependsOn, bool IsProtected);

public interface IProcessServiceInfo
{
    IReadOnlyList<ProcessEntry> GetProcesses();
    IReadOnlyList<StartupEntry> GetStartupEntries();
    IReadOnlyList<ServiceEntry> GetServices();
}

/// <summary>Processes and services Sloomy Tweaks must never stop, disable or recommend changing.</summary>
public static class ProcessCatalog
{
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "explorer", "sihost", "ctfmon", "MsMpEng", "NisSrv", "SecurityHealthService",
        "Memory Compression", "audiodg", "spoolsv", "LsaIso", "WUDFHost", "conhost",
    };

    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows Update, security and core platform services.
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "CryptSvc", "TrustedInstaller",
        "WinDefend", "WdNisSvc", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "Sense",
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Schedule", "ProfSvc",
        "Winmgmt", "PlugPlay", "Power", "AudioSrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "nsi", "Netman",
    };

    public static bool IsCritical(string processName) => CriticalProcesses.Contains(processName);
    public static bool IsProtectedService(string serviceName) => ProtectedServices.Contains(serviceName);
}
