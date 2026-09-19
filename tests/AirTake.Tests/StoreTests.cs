using System.Text;
using System.Text.Json;
using AirTake.Core;
using Xunit;

namespace AirTake.Tests;
public sealed class StoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AirTake-tests", Guid.NewGuid().ToString("N"));
    private TakeStore Store => new(root, 0);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static Task<ChunkReceipt> Put(TakeStore s, Guid id, int sequence, string text) { var data = Encoding.UTF8.GetBytes(text); return s.Put(id, sequence, Integrity.Hash(data), new MemoryStream(data), data.Length, default); }
    private static Completion Complete(int chunks = 2) => new(chunks, 120, 120, 0, 100000, 1, "user");
    [Theory][InlineData(3840,2160,120)][InlineData(3840,2160,60)][InlineData(3840,2160,30)][InlineData(1920,1080,120)]
    public void ValidModes(int w, int h, int fps) => new CaptureSettings(w,h,fps).Validate();
    [Theory][InlineData(0,2160,120,120,1024)][InlineData(3840,2160,119,120,1024)][InlineData(3840,2160,120,0,1024)][InlineData(3840,2160,120,120,32)]
    public void InvalidModes(int w,int h,int fps,int bitrate,int buffer) => Assert.Throws<ArgumentException>(() => new CaptureSettings(w,h,fps,bitrate,buffer).Validate());
    [Fact] public void HashKnownVector() => Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Integrity.Hash("abc"u8));
    [Fact] public void Tokens() { Assert.True(Integrity.SecretEquals("abc","abc")); Assert.False(Integrity.SecretEquals("abc","ab")); Assert.False(Integrity.SecretEquals("abc","abd")); }
    [Fact] public async Task ChunkIsDurableAndIdempotent()
    {
        var store = Store; var take = store.Create(new(),0);
        var first = await Put(store,take.Id,0,"initialization"); var retry = await Put(store,take.Id,0,"initialization");
        Assert.Equal(first,retry); Assert.Equal("initialization", File.ReadAllText(Path.Combine(store.Folder(take.Id),"chunks","00000000.bin")));
    }
    [Fact] public async Task ConflictingRetryRejected()
    {
        var store=Store; var take=store.Create(new(),0); await Put(store,take.Id,0,"a");
        await Assert.ThrowsAsync<InvalidDataException>(()=>Put(store,take.Id,0,"b"));
    }
    [Fact] public async Task CorruptUploadNeverCommitted()
    {
        var store=Store; var take=store.Create(new(),0);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Put(take.Id,0,new string('0',64),new MemoryStream("abc"u8.ToArray()),3,default));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Folder(take.Id),"chunks")));
    }
    [Fact] public async Task TruncatedUploadNeverCommitted()
    {
        var store=Store; var take=store.Create(new(),0);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Put(take.Id,0,Integrity.Hash("abc"u8),new MemoryStream("ab"u8.ToArray()),3,default));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.Folder(take.Id),"chunks")));
    }
    [Fact] public async Task InvalidLengthsRejected()
    {
        var store=Store; var take=store.Create(new(),0);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.Put(take.Id,-1,Integrity.Hash("a"u8),new MemoryStream(),1,default));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.Put(take.Id,0,Integrity.Hash("a"u8),new MemoryStream(),TakeStore.MaximumChunkBytes+1,default));
    }
    [Fact] public async Task MissingSequenceBlocksCompletion()
    {
        var store=Store; var take=store.Create(new(),0); await Put(store,take.Id,0,"init"); await Put(store,take.Id,2,"later");
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Seal(take.Id,Complete(3),default));
        Assert.Null(store.Get(take.Id).Completion);
    }
    [Fact] public async Task OutOfOrderAssemblyAndDuplicateCompletion()
    {
        var store=Store; var take=store.Create(new(),0); await Put(store,take.Id,1,"fragment"); await Put(store,take.Id,0,"init");
        var path=await store.Seal(take.Id,Complete(),default); Assert.Equal("initfragment",File.ReadAllText(path));
        Assert.Equal(path,await store.Seal(take.Id,Complete(),default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Seal(take.Id,Complete() with { Encoded=119 },default));
    }
    [Fact] public async Task CorruptionOnDiskDetectedBeforeSeal()
    {
        var store=Store; var take=store.Create(new(),0); await Put(store,take.Id,0,"init"); await Put(store,take.Id,1,"data");
        File.WriteAllText(Path.Combine(store.Folder(take.Id),"chunks","00000001.bin"),"evil");
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.Seal(take.Id,Complete(),default));
        Assert.False(File.Exists(Path.Combine(store.Folder(take.Id),"video.mp4")));
    }
    [Fact] public async Task StoreResumesAfterRestart()
    {
        var store=Store; var take=store.Create(new(),70); await Put(store,take.Id,0,"init");
        var reopened=Store; Assert.Equal(70,reopened.Get(take.Id).AudioOffsetMs);
        await Put(reopened,take.Id,1,"data"); Assert.True(File.Exists(await reopened.Seal(take.Id,Complete(),default)));
    }
    [Fact] public async Task ConcurrentRetriesCommitOnce()
    {
        var store=Store; var take=store.Create(new(),0); var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Put(store,take.Id,0,"init")));
        Assert.Single(results.Distinct());
    }
    [Fact] public void JsonUsesSharedCamelCase()
    {
        var json=JsonSerializer.Serialize(new Control(null,false,new()),Json.Options);
        Assert.Contains("\"bitrateMbps\"",json); Assert.Contains("\"takeId\"",json); Assert.Contains("\"stopOnDroppedFrame\"",json);
    }
}
