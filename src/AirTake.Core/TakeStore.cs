using System.Security.Cryptography;
using System.Text.Json;

namespace AirTake.Core;

public sealed class TakeStore
{
    public string DirectoryPath { get; }
    public TakeManifest Manifest { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string ManifestPath => Path.Combine(DirectoryPath, "take.json");
    public string AudioPath => Path.Combine(DirectoryPath, "microphone.wav");
    private TakeStore(string path, TakeManifest manifest) { DirectoryPath = path; Manifest = manifest; }

    public static string SafeId(string id) => Guid.TryParseExact(id, "D", out var guid) ? guid.ToString("D") : throw new ProtocolException(400, "Invalid take ID.");
    public static TakeStore Create(AppOptions options, string id, string clientId)
    {
        id = SafeId(id);
        var path = Path.Combine(Path.GetFullPath(options.OutputDirectory), id);
        if (Directory.Exists(path)) throw new ProtocolException(409, "Take already exists; do not start it again.");
        Directory.CreateDirectory(path);
        var store = new TakeStore(path, new TakeManifest { Id = id, ClientId = clientId, Capture = options.Capture, AudioOffsetMs = options.AudioOffsetMs, VideoOnly = options.VideoOnly, KeepSources = options.KeepSources });
        store.Save();
        return store;
    }
    public static TakeStore Load(string root, string id)
    {
        var path = Path.Combine(Path.GetFullPath(root), SafeId(id));
        var file = Path.Combine(path, "take.json");
        if (!File.Exists(file)) throw new ProtocolException(404, "Take not found. Check the output folder on the PC.");
        var manifest = JsonSerializer.Deserialize<TakeManifest>(File.ReadAllText(file), Wire.Json) ?? throw new InvalidDataException("Invalid take manifest.");
        if (manifest.Id != SafeId(id) || manifest.ProtocolVersion != Wire.Version) throw new InvalidDataException("Manifest identity/version mismatch.");
        return new TakeStore(path, manifest);
    }
    // Atomic journal replacement; callers serialize manifest changes using Gate.
    public void Save()
    {
        var temporary = ManifestPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, Manifest, Wire.Json);
            file.Flush(true);
        }
        File.Move(temporary, ManifestPath, true);
    }
    public string ChunkPath(int index) => Path.Combine(DirectoryPath, $"{index:D8}.seg");

    public async Task<int> WriteChunkAsync(int index, long length, string sha256, Stream source, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index > 86401 || length is <= 0 or > Wire.MaxChunkBytes) throw new ProtocolException(413, "Chunk index/size exceeds protocol limit.");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ProtocolException(400, "A SHA-256 checksum is required.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Manifest.State is "complete" or "finalizing") throw new ProtocolException(409, "Take is already being finalized.");
            if (index < Manifest.Chunks.Count)
            {
                var prior = Manifest.Chunks[index];
                if (prior.Bytes != length || !prior.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new ProtocolException(409, "Conflicting retransmission.");
                var existing = ChunkPath(index);
                if (!File.Exists(existing) || new FileInfo(existing).Length != length) throw new ProtocolException(500, "Previously acknowledged chunk is missing from disk.");
                return Manifest.Chunks.Count;
            }
            if (index != Manifest.Chunks.Count) throw new ProtocolException(409, $"Expected chunk {Manifest.Chunks.Count}, received {index}.");
            EnsureSpace(DirectoryPath, length + 256L * 1024 * 1024);
            var partial = ChunkPath(index) + ".partial";
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                var buffer = new byte[128 * 1024];
                var prefix = new byte[8];
                long total = 0;
                while (total < length)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - total)), cancellationToken);
                    if (count == 0) throw new ProtocolException(400, "Truncated upload.");
                    if (total < 8) buffer.AsSpan(0, (int)Math.Min(count, 8 - total)).CopyTo(prefix.AsSpan((int)total));
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    total += count;
                }
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!actualHash.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new ProtocolException(422, "Chunk checksum mismatch; resend this chunk.");
                if (index == 0 && (length < 8 || !prefix.AsSpan(4, 4).SequenceEqual("ftyp"u8))) throw new ProtocolException(422, "Chunk zero must be the MP4 initialization segment.");
                await destination.FlushAsync(cancellationToken);
                destination.Flush(true);
                await destination.DisposeAsync();
                File.Move(partial, ChunkPath(index), true);
                Manifest.Chunks.Add(new ChunkInfo(index, length, actualHash));
                Save();
                // Acknowledgement is sent only AFTER both data and journal were flushed.
                return Manifest.Chunks.Count;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        finally { Gate.Release(); }
    }

    public void ValidateComplete(int expectedChunks)
    {
        if (expectedChunks < 2 || expectedChunks != Manifest.Chunks.Count) throw new ProtocolException(409, "Not all video segments have arrived.");
        if (Manifest.End is null) throw new ProtocolException(409, "The capture-end message has not arrived.");
        for (var i = 0; i < expectedChunks; i++)
            if (Manifest.Chunks[i].Index != i || !File.Exists(ChunkPath(i))) throw new ProtocolException(409, "Missing video segment.");
    }

    public async Task CopyVideoToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[128 * 1024];
        foreach (var chunk in Manifest.Chunks)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = new FileStream(ChunkPath(chunk.Index), FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                total += count;
            }
            if (total != chunk.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Disk corruption in chunk {chunk.Index}.");
        }
    }
    public async Task<string> ExportFragmentedVideoAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(DirectoryPath, "video.fragmented.mp4");
        await using (var target = new FileStream(path + ".partial", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyVideoToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            target.Flush(true);
        }
        File.Move(path + ".partial", path, true);
        return path;
    }
    public void DeleteTemporarySources()
    {
        if (Manifest.State != "complete" || Manifest.KeepSources || !File.Exists(Manifest.OutputFile)) return;
        foreach (var chunk in Manifest.Chunks) File.Delete(ChunkPath(chunk.Index));
        if (File.Exists(AudioPath)) File.Delete(AudioPath);
    }
    public static long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public static void EnsureSpace(string path, long required)
    {
        if (FreeBytes(path) < required) throw new ProtocolException(507, "Insufficient disk space; recording has been stopped. Pending segments remain on the phone.");
    }
}
