using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AirTake.Core;

namespace AirTake.Desktop;

internal static class Storage
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTake");
    private static readonly object LogGate = new();
    public static AppOptions LoadOptions()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(path), Wire.Json) ?? new(); }
        catch (Exception exception) { Log("Settings could not be read: " + exception.Message); return new(); }
    }
    public static void SaveOptions(AppOptions options)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(options, Wire.Json));
        File.Move(path + ".tmp", path, true);
    }
    public static Identity LoadIdentity()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "identity.dpapi");
        IdentityData data;
        if (File.Exists(path))
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            data = JsonSerializer.Deserialize<IdentityData>(clear) ?? throw new InvalidDataException("Cannot decode local pairing identity.");
            CryptographicOperations.ZeroMemory(clear);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=AirTake local receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
            data = new(Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            var clear = JsonSerializer.SerializeToUtf8Bytes(data);
            File.WriteAllBytes(path, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(clear);
        }
        var loaded = new X509Certificate2(Convert.FromBase64String(data.Pfx), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        return new Identity(loaded, data.Token);
    }
    public static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString()).Distinct().OrderBy(a => a.StartsWith("192.168.") ? 0 : a.StartsWith("10.") ? 1 : 2).ToArray();
    public static void Log(string message)
    {
        try { lock (LogGate) { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root, "airtake.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } }
        catch (IOException) { /* A logging failure must not destroy an ongoing take. */ }
    }
    private sealed record IdentityData(string Pfx, string Token);
}
