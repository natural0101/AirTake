using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AirTake.Core;

// Fragments are never acknowledged before their checksum and durable disk write succeed.
// Originals remain on disk after export, including failed/interrupted takes.
public sealed class TakeStore(string root, long reserveBytes = 5L * 1024 * 1024 * 1024)
{
    public const long MaximumChunkBytes = 64L * 1024 * 1024;
    public string Root { get; } = Path.GetFullPath(root);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public string Folder(Guid id) => Path.Combine(Root, id.ToString("N"));
    private string ManifestPath(Guid id) => Path.Combine(Folder(id), "take.json");
    public void RequireSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        if (drive.AvailableFreeSpace - bytes < reserveBytes) throw new IOException("Недостаточно места: достигнут резерв диска.");
    }
    public TakeManifest Create(CaptureSettings settings, int offsetMs)
    {
        settings.Validate(); Directory.CreateDirectory(Root); RequireSpace(0);
        var manifest = new TakeManifest { Id = Guid.NewGuid(), Settings = settings, AudioOffsetMs = offsetMs };
        Directory.CreateDirectory(Path.Combine(Folder(manifest.Id), "chunks"));
        Json.AtomicWrite(ManifestPath(manifest.Id), manifest);
        return manifest;
    }
    public TakeManifest Get(Guid id)
    {
        if (!File.Exists(ManifestPath(id))) throw new FileNotFoundException("Take not found");
        return Json.Read<TakeManifest>(ManifestPath(id));
    }
    public async Task Update(Guid id, Action<TakeManifest> update)
    {
        var gate = gates.GetOrAdd(id, _ => new(1)); await gate.WaitAsync();
        try { var manifest = Get(id); update(manifest); Json.AtomicWrite(ManifestPath(id), manifest); }
        finally { gate.Release(); }
    }
    public async Task<ChunkReceipt> Put(Guid id, int sequence, string expectedHash, Stream input, long length, CancellationToken ct)
    {
        if (sequence is < 0 or > 1_000_000 || !Integrity.ValidHash(expectedHash) || length <= 0 || length > MaximumChunkBytes)
            throw new ArgumentException("Invalid chunk metadata");
        var gate = gates.GetOrAdd(id, _ => new(1)); await gate.WaitAsync(ct);
        string? temp = null;
        try
        {
            var manifest = Get(id);
            string path = Path.Combine(Folder(id), "chunks", $"{sequence:D8}.bin");
            if (File.Exists(path))
            {
                var receipt = Json.Read<ChunkReceipt>(path + ".json");
                if (receipt.Bytes != length || !receipt.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A different chunk already occupies this sequence");
                return receipt;
            }
            if (manifest.Completion is not null) throw new InvalidOperationException("Take already sealed");
            RequireSpace(length); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + ".upload";
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    int n = await input.ReadAsync(buffer, ct); if (n == 0) break;
                    received += n; if (received > length) throw new InvalidDataException("Oversized chunk");
                    digest.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            if (received != length || !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chunk checksum/length mismatch");
            var result = new ChunkReceipt(sequence, received, hash);
            // Receipt precedes rename. An interrupted write is safe to retry; the .bin is the commit marker.
            Json.AtomicWrite(path + ".json", result); File.Move(temp, path, true); temp = null;
            return result;
        }
        finally { if (temp is not null && File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
    public async Task<string> Seal(Guid id, Completion completion, CancellationToken ct)
    {
        if (completion.ChunkCount is < 2 or > 1_000_001 || completion.Encoded <= 0 || completion.Captured < completion.Encoded
            || completion.Dropped < 0 || !double.IsFinite(completion.DurationSeconds) || completion.DurationSeconds <= 0
            || !double.IsFinite(completion.FirstVideoTimeMs)) throw new ArgumentException("Invalid completion");
        var gate = gates.GetOrAdd(id, _ => new(1)); await gate.WaitAsync(ct);
        var temp = Path.Combine(Folder(id), "video.assembling");
        try
        {
            var manifest = Get(id); var video = Path.Combine(Folder(id), "video.mp4");
            if (manifest.Completion is not null)
            {
                if (manifest.Completion != completion) throw new InvalidDataException("Conflicting completion");
                if (File.Exists(video)) return video;
            }
            var receipts = new List<ChunkReceipt>();
            for (int i = 0; i < completion.ChunkCount; i++)
            {
                string path = Path.Combine(Folder(id), "chunks", $"{i:D8}.bin");
                if (!File.Exists(path) || !File.Exists(path + ".json")) throw new InvalidDataException($"Missing chunk {i}");
                receipts.Add(Json.Read<ChunkReceipt>(path + ".json"));
            }
            RequireSpace(receipts.Sum(x => x.Bytes));
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                for (int i = 0; i < receipts.Count; i++)
                {
                    string path = Path.Combine(Folder(id), "chunks", $"{i:D8}.bin");
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).ToLowerInvariant();
                    if (source.Length != receipts[i].Bytes || hash != receipts[i].Sha256) throw new InvalidDataException($"Disk integrity failure: chunk {i}");
                    source.Position = 0; await source.CopyToAsync(output, ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            File.Move(temp, video, true);
            manifest.Completion = completion;
            manifest.State = completion.Dropped == 0 && completion.Reason == "user" ? "received" : "received-with-warning";
            Json.AtomicWrite(ManifestPath(id), manifest);
            return video;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
    public IEnumerable<TakeManifest> List()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var path in Directory.EnumerateFiles(Root, "take.json", SearchOption.AllDirectories))
        {
            TakeManifest? item = null; try { item = Json.Read<TakeManifest>(path); } catch (Exception) { }
            if (item is not null) yield return item;
        }
    }
}
