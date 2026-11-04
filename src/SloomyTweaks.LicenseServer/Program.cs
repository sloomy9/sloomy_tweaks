using System.Security.Cryptography;
using System.Text;
using SloomyTweaks.LicenseServer;

// Administrative command-line tools (run on the server host, never in the client):
//   generate-signing-key <private.pem> <public.pem>   create an ECDSA P-256 signing key pair
//   hash-admin-key <key>                              print the SHA-256 to put in Sloomy:AdminKeySha256
//   generate-admin-key                                print a random admin key and its hash
//   generate-pepper                                   print a random Sloomy:KeyPepper value
if (args.Length > 0)
{
    switch (args[0])
    {
        case "generate-signing-key" when args.Length == 3:
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(args[1], UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine($"Private key: {args[1]} (keep secret)\nPublic key:  {args[2]} (embed in the Sloomy Tweaks client)");
            return 0;
        }
        case "hash-admin-key" when args.Length == 2:
            Console.WriteLine(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(args[1]))).ToLowerInvariant());
            return 0;
        case "generate-admin-key":
        {
            string adminKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Console.WriteLine($"Admin key (give to administrators, store in a password manager): {adminKey}");
            Console.WriteLine($"Sloomy:AdminKeySha256: {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(adminKey))).ToLowerInvariant()}");
            return 0;
        }
        case "generate-pepper":
            Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            return 0;
    }
}

WebApplication app = LicenseServerApp.Build(args);
app.Run();
return 0;
