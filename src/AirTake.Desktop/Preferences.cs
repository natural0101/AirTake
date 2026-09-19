using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using AirTake.Core;
namespace AirTake.Desktop;

public sealed class Preferences
{
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTake");
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AirTake");
    public string Address { get; set; } = Addresses().FirstOrDefault() ?? "127.0.0.1";
    public int Port { get; set; } = 49712;
    public int ReserveGiB { get; set; } = 5;
    public string Token { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string? MicrophoneId { get; set; }
    public bool RecordAudio { get; set; } = true;
    public int AudioOffsetMs { get; set; }
    public CaptureSettings Capture { get; set; } = new();
    public static Preferences Load()
    {
        Directory.CreateDirectory(DataFolder); var path = Path.Combine(DataFolder, "settings.json");
        if (!File.Exists(path)) return new();
        try { return Json.Read<Preferences>(path); }
        catch { File.Copy(path, path + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true); return new(); }
    }
    public void Save() { Directory.CreateDirectory(DataFolder); Json.AtomicWrite(Path.Combine(DataFolder, "settings.json"), this); }
    public static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
        .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Where(ip => { var a = ip.GetAddressBytes(); return a[0] == 10 || a[0] == 192 && a[1] == 168 || a[0] == 172 && a[1] is >= 16 and <= 31; })
        .Select(ip => ip.ToString()).Distinct().ToArray();
}
