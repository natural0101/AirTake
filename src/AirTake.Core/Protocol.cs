using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirTake.Core;

public sealed record CaptureSettings(int Width = 3840, int Height = 2160, int Fps = 120,
    int BitrateMbps = 120, int BufferMiB = 1024, bool StopOnDroppedFrame = true)
{
    public void Validate()
    {
        if ((Width, Height) is not ((3840, 2160) or (1920, 1080))) throw new ArgumentException("Unsupported resolution");
        if (Fps is not (30 or 60 or 120)) throw new ArgumentException("Unsupported FPS");
        if (BitrateMbps is < 20 or > 200) throw new ArgumentException("Bitrate must be 20–200 Mbps");
        if (BufferMiB is < 128 or > 4096) throw new ArgumentException("Buffer must be 128–4096 MiB");
    }
}
public sealed record Pairing(int Version, string Url, string Token, string Fingerprint);
public sealed record Control(Guid? TakeId, bool Recording, CaptureSettings Settings);
public sealed record PhoneStatus(string Name, double Fps, long Captured, long Encoded, long Dropped,
    long QueueBytes, string Thermal, string? Error, Guid? TakeId, string? PreviewJpeg = null);
public sealed record Completion(int ChunkCount, long Captured, long Encoded, long Dropped,
    double FirstVideoTimeMs, double DurationSeconds, string Reason);
public sealed record ChunkReceipt(int Sequence, long Bytes, string Sha256);
public sealed class TakeManifest
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public CaptureSettings Settings { get; set; } = new();
    public string State { get; set; } = "recording";
    public double? FirstAudioTimeMs { get; set; }
    public int AudioSampleRate { get; set; }
    public int AudioChannels { get; set; }
    public int AudioOffsetMs { get; set; }
    public Completion? Completion { get; set; }
    public string? Error { get; set; }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON: {Path.GetFileName(path)}");
    public static void AtomicWrite<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Options); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
// A wall-clock anchor plus a monotonic clock avoids jumps while a take is running.
public static class Clock
{
    private static readonly double Epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    public static double NowMs => Epoch + Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
}
public static class Integrity
{
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    public static bool SecretEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
}
