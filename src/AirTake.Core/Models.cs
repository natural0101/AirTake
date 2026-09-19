using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AirTake.Core;

public static class Wire
{
    public const int Version = 1;
    public const int MaxChunkBytes = 64 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

// Monotonic elapsed time prevents an OS clock correction in the middle of a take.
public static class Clock
{
    private static readonly double Epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    private static readonly long Start = Stopwatch.GetTimestamp();
    public static double Now => Epoch + Stopwatch.GetElapsedTime(Start).TotalSeconds;
}

public sealed record CaptureOptions
{
    public int Width { get; init; } = 3840;
    public int Height { get; init; } = 2160;
    public int Fps { get; init; } = 120;
    public int BitrateMbps { get; init; } = 120;
    public int BufferMiB { get; init; } = 512;
    public bool Preview { get; init; }
    public double ExposureBias { get; init; }
    public double Focus { get; init; } = -1;
    public int WhiteBalanceKelvin { get; init; }
    public void Validate()
    {
        if (!((Width == 3840 && Height == 2160) || (Width == 1920 && Height == 1080))) throw new ArgumentException("Only 3840x2160 or 1920x1080 is supported.");
        if (Fps is not (30 or 60 or 120)) throw new ArgumentException("FPS must be 30, 60 or 120.");
        if (BitrateMbps is < 10 or > 200 || BufferMiB is < 128 or > 4096) throw new ArgumentException("Invalid bitrate or buffer limit.");
        if (!double.IsFinite(ExposureBias) || ExposureBias is < -2 or > 2 || !double.IsFinite(Focus) || Focus is < -1 or > 1) throw new ArgumentException("Invalid camera settings.");
        if (WhiteBalanceKelvin != 0 && WhiteBalanceKelvin is < 2000 or > 9000) throw new ArgumentException("Invalid white balance.");
    }
}

public sealed class AppOptions
{
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AirTake");
    public string ListenAddress { get; set; } = "";
    public int Port { get; set; } = 48721;
    public string MicrophoneId { get; set; } = "";
    public bool VideoOnly { get; set; }
    public bool KeepSources { get; set; }
    public int AudioOffsetMs { get; set; }
    public string FfmpegPath { get; set; } = "";
    public CaptureOptions Capture { get; set; } = new();
}

public sealed record Identity(X509Certificate2 Certificate, string Token)
{
    public string Pin => Wire.Hash(Certificate.RawData);
    public string PairingUri(string host, int port) => $"airtake://pair?host={Uri.EscapeDataString(host)}&port={port}&token={Token}&pin={Pin}&v=1";
    public static Identity Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AirTake local receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        return new(new X509Certificate2(certificate.Export(X509ContentType.Pfx)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
    }
}

public sealed record PhoneTelemetry
{
    public string ClientId { get; init; } = "";
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string Mode { get; init; } = "";
    public double Fps { get; init; }
    public long Captured { get; init; }
    public long Dropped { get; init; }
    public long PendingBytes { get; init; }
    public double Battery { get; init; } = -1;
    public string Thermal { get; init; } = "unknown";
    public bool HardwareEncoder { get; init; }
    public string[] Formats { get; init; } = [];
}

public sealed record TakeEnd
{
    public double VideoStartUnix { get; init; }
    public double Duration { get; init; }
    public double ClockRttMs { get; init; }
    public long Captured { get; init; }
    public long Encoded { get; init; }
    public long CaptureDrops { get; init; }
    public long EncoderDrops { get; init; }
    public long WriterDrops { get; init; }
    public bool Interrupted { get; init; }
    public string Error { get; init; } = "";
    public void Validate()
    {
        if (!double.IsFinite(VideoStartUnix) || VideoStartUnix < 0 || !double.IsFinite(Duration) || Duration is < 0 or > 86400 || !double.IsFinite(ClockRttMs) || ClockRttMs < 0) throw new ArgumentException("Invalid timestamps.");
        if (Captured < 0 || Encoded < 0 || CaptureDrops < 0 || EncoderDrops < 0 || WriterDrops < 0) throw new ArgumentException("Invalid counters.");
    }
}

public sealed record AudioInfo(double StartUnix, int SampleRate, int Channels, int Bits, long SampleFrames, string? Error = null);
public sealed record ChunkInfo(int Index, long Bytes, string Sha256);
public sealed class TakeManifest
{
    public int ProtocolVersion { get; set; } = Wire.Version;
    public string Id { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string State { get; set; } = "recording";
    public double CreatedUnix { get; set; } = Clock.Now;
    public CaptureOptions Capture { get; set; } = new();
    public List<ChunkInfo> Chunks { get; set; } = [];
    public TakeEnd? End { get; set; }
    public AudioInfo? Audio { get; set; }
    public int AudioOffsetMs { get; set; }
    public bool VideoOnly { get; set; }
    public bool KeepSources { get; set; }
    public string OutputFile { get; set; } = "";
    public string Note { get; set; } = "";
}

public interface IAudioRecorder : IDisposable
{
    Task StartAsync(string path, string deviceId);
    Task<AudioInfo?> StopAsync();
    string? Error { get; }
}

public sealed class NoAudioRecorder : IAudioRecorder
{
    public string? Error => null;
    public Task StartAsync(string path, string deviceId) => Task.CompletedTask;
    public Task<AudioInfo?> StopAsync() => Task.FromResult<AudioInfo?>(null);
    public void Dispose() { }
}

public sealed class ProtocolException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
