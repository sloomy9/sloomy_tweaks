using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SloomyTweaks.Core.Licensing;
using static SloomyTweaks.Platform.Windows.Interop.NativeMethods;

namespace SloomyTweaks.Platform.Windows;

/// <summary>
/// Privacy-conscious device identity: SHA-256 of an application-specific prefix and the Windows
/// MachineGuid (a random value Windows generates at installation). No hardware serial numbers,
/// MAC addresses or disk IDs are read. The hash cannot be reversed to the MachineGuid and is
/// meaningless outside Sloomy Tweaks. It changes when Windows is reinstalled.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    private const string Salt = "SloomyTweaks.DeviceId.v1:";

    public WindowsDeviceIdentity()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? crypto = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        string machineGuid = crypto?.GetValue("MachineGuid") as string
            ?? throw new InvalidOperationException("Windows MachineGuid is not available.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + machineGuid.Trim().ToLowerInvariant()));
        DeviceId = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        DeviceName = Environment.MachineName;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
}

/// <summary>Stores the signed license token encrypted with DPAPI for the current Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ILicenseTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sloomy Tweaks license token");
    private readonly string _path;

    public DpapiTokenStore(string path) => _path = path;

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_path)));
        }
        catch (Exception e) when (e is Win32Exception or IOException or CryptographicException)
        {
            return null; // Corrupt or from another user profile: treat as not activated.
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(token)));
        File.Move(temp, _path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = dataHandle.AddrOfPinnedObject() };
            var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Sloomy Tweaks", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            dataHandle.Free();
            entropyHandle.Free();
        }
    }
}
