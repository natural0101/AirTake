using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AirTake.Core;

var root = Path.Combine(Path.GetTempPath(), "airtake-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
int passed = 0;
var failures = new List<string>();
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromMinutes(2)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add(name + ": " + e); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
async Task Reject(int code, Func<Task> action)
{
    try { await action(); throw new Exception("Expected rejection " + code); }
    catch (ProtocolException e) { Assert(e.Status == code, $"Expected {code}, got {e.Status}"); }
}
// AirTake's wire protocol deliberately requires Content-Length; JsonContent is
// chunked by default. Use bounded StringContent like the iPhone client does.
Task<HttpResponseMessage> Post(HttpClient client, string path, object value) => client.PostAsync(path, new StringContent(JsonSerializer.Serialize(value, Wire.Json), Encoding.UTF8, "application/json"));
var init = new byte[] { 0,0,0,16,102,116,121,112,105,115,111,54,0,0,0,0 };
var media = Encoding.ASCII.GetBytes("\0\0\0\u0010moofTESTDATA");
AppOptions Options() => new() { OutputDirectory = root, VideoOnly = true, KeepSources = true, FfmpegPath = Environment.ProcessPath! };
TakeStore NewStore() => TakeStore.Create(Options(), Guid.NewGuid().ToString(), "test-client");
Task<int> Send(TakeStore store, int index, byte[] data, string? hash = null) => store.WriteChunkAsync(index, data.Length, hash ?? Wire.Hash(data), new MemoryStream(data));
await Test("capture settings validated", () => { new CaptureOptions().Validate(); return Task.CompletedTask; });
await Test("unsupported FPS rejected", () => { try { new CaptureOptions { Fps = 24 }.Validate(); throw new Exception("Accepted 24 FPS"); } catch (ArgumentException) { } return Task.CompletedTask; });
await Test("NaN camera setting rejected", () => { try { new CaptureOptions { Focus = double.NaN }.Validate(); throw new Exception("Accepted NaN"); } catch (ArgumentException) { } return Task.CompletedTask; });
await Test("SHA256 known vector", () => { Assert(Wire.Hash("abc"u8) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"); return Task.CompletedTask; });
await Test("path traversal rejected", () => Reject(400, () => { TakeStore.SafeId("../../outside"); return Task.CompletedTask; }));
await Test("ACK follows durable journal", async () => { var s = NewStore(); Assert(await Send(s, 0, init) == 1); var loaded = TakeStore.Load(root, s.Manifest.Id); Assert(loaded.Manifest.Chunks.Count == 1 && File.ReadAllBytes(loaded.ChunkPath(0)).SequenceEqual(init)); });
await Test("duplicate upload idempotent", async () => { var s = NewStore(); await Send(s, 0, init); Assert(await Send(s, 0, init) == 1); Assert(s.Manifest.Chunks.Count == 1); });
await Test("conflicting duplicate rejected", async () => { var s = NewStore(); await Send(s, 0, init); await Reject(409, async () => { await Send(s, 0, media); }); });
await Test("out-of-order upload rejected", () => Reject(409, async () => { await Send(NewStore(), 2, media); }));
await Test("bad checksum leaves no committed chunk", async () => { var s = NewStore(); await Reject(422, async () => { await Send(s, 0, init, new string('0',64)); }); Assert(s.Manifest.Chunks.Count == 0 && !File.Exists(s.ChunkPath(0))); });
await Test("truncated upload rejected", () => Reject(400, async () => { await NewStore().WriteChunkAsync(0, init.Length + 1, Wire.Hash(init), new MemoryStream(init)); }));
await Test("oversized upload rejected before allocation", () => Reject(413, async () => { await NewStore().WriteChunkAsync(0, Wire.MaxChunkBytes + 1L, Wire.Hash(init), Stream.Null); }));
await Test("non-MP4 initialization rejected", () => Reject(422, async () => { await Send(NewStore(), 0, media); }));
await Test("incomplete take cannot finalize", async () => { var s = NewStore(); await Send(s, 0, init); await Reject(409, () => { s.ValidateComplete(2); return Task.CompletedTask; }); });
await Test("source assembly is byte-exact", async () => { var s = NewStore(); await Send(s, 0, init); await Send(s, 1, media); using var output = new MemoryStream(); await s.CopyVideoToAsync(output); Assert(output.ToArray().SequenceEqual(init.Concat(media))); });
await Test("on-disk corruption detected", async () => { var s = NewStore(); await Send(s, 0, init); File.WriteAllBytes(s.ChunkPath(0), media); try { await s.CopyVideoToAsync(Stream.Null); throw new Exception("Corruption ignored"); } catch (InvalidDataException) { } });
await Test("receiver restart preserves next sequence number", async () => { var s = NewStore(); await Send(s, 0, init); var restored = TakeStore.Load(root,s.Manifest.Id); Assert(await Send(restored,0,init) == 1); Assert(await Send(restored,1,media) == 2); });
await Test("identity tokens are random and certificate pins match", () => { var a = Identity.Create(); var b = Identity.Create(); Assert(a.Token.Length == 64 && a.Token != b.Token && a.Pin == Wire.Hash(a.Certificate.RawData)); a.Certificate.Dispose(); b.Certificate.Dispose(); return Task.CompletedTask; });
await Test("HTTPS receiver: auth, bounded JSON, REC, retransmission, finish, abort", async () =>
{
    var options = Options(); options.ListenAddress = "127.0.0.1";
    var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); options.Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
    var identity = Identity.Create();
    await using var server = new ReceiverHost(options, identity, new NoAudioRecorder(), s => s.ExportFragmentedVideoAsync());
    server.Log += text => Console.WriteLine("  receiver: " + text);
    await server.StartAsync();
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && Wire.Hash(certificate.RawData) == identity.Pin };
    using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{options.Port}"), Timeout = TimeSpan.FromSeconds(15) };
    Assert((await client.GetAsync("/v1/clock")).StatusCode == HttpStatusCode.Unauthorized);
    client.DefaultRequestHeaders.Authorization = new("Bearer", identity.Token);
    var clock = await client.GetFromJsonAsync<JsonElement>("/v1/clock"); Assert(clock.GetProperty("version").GetInt32() == 1);
    Assert((await client.PostAsJsonAsync("/v1/heartbeat", new { clientId = "integration", status = "ready" })).StatusCode == HttpStatusCode.RequestEntityTooLarge, "Unbounded JSON must be rejected");
    (await Post(client,"/v1/heartbeat", new { clientId = "integration", status = "ready" })).EnsureSuccessStatusCode();
    var id = Guid.NewGuid().ToString();
    Assert((await Post(client,"/v1/start", new { id, clientId = "integration" })).StatusCode == HttpStatusCode.Conflict);
    server.RequestRecording(true);
    (await Post(client,"/v1/start", new { id, clientId = "integration" })).EnsureSuccessStatusCode();
    (await Post(client,"/v1/start", new { id, clientId = "integration" })).EnsureSuccessStatusCode();
    Assert((await Post(client,$"/v1/takes/{id}/finish", new { chunks = 2 })).StatusCode == HttpStatusCode.Conflict);
    for (int i = 0; i < 2; i++)
    {
        var data = i == 0 ? init : media;
        using var content = new ByteArrayContent(data); content.Headers.Add("X-SHA256", Wire.Hash(data));
        (await client.PostAsync($"/v1/takes/{id}/chunks/{i}", content)).EnsureSuccessStatusCode();
    }
    Assert((await Post(client,$"/v1/takes/{id}/abort", new { })).StatusCode == HttpStatusCode.Conflict, "Nonempty take must not be aborted");
    (await Post(client,$"/v1/takes/{id}/end", new TakeEnd { VideoStartUnix = Clock.Now, Duration = 0.2, Captured = 24, Encoded = 24 })).EnsureSuccessStatusCode();
    var response = await Post(client,$"/v1/takes/{id}/finish", new { chunks = 2 });
    Assert(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    Assert(!server.Busy);
    (await Post(client,$"/v1/takes/{id}/finish", new { chunks = 2 })).EnsureSuccessStatusCode();
    Assert(TakeStore.Load(root, id).Manifest.State == "complete");
    var empty = Guid.NewGuid().ToString();
    server.RequestRecording(true);
    (await Post(client,"/v1/start", new { id = empty, clientId = "integration" })).EnsureSuccessStatusCode();
    (await Post(client,$"/v1/takes/{empty}/abort", new { })).EnsureSuccessStatusCode();
    (await Post(client,$"/v1/takes/{empty}/abort", new { })).EnsureSuccessStatusCode();
    Assert(!server.Busy && TakeStore.Load(root,empty).Manifest.State == "aborted-empty");
    identity.Certificate.Dispose();
});

if (args.Length > 0)
{
    await Test("real FFmpeg: 120 FPS HEVC + PCM microphone -> MP4/AAC", async () =>
    {
        var data = Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hevc-120.b64")).Trim());
        var firstMoof = 0;
        for (int offset = 0; offset + 8 <= data.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset,4)));
            if (Encoding.ASCII.GetString(data, offset + 4, 4) == "moof") { firstMoof = offset; break; }
            if (length < 8) throw new InvalidDataException("Invalid fixture"); offset += length;
        }
        Assert(firstMoof > 0);
        var options = Options(); options.VideoOnly = false;
        var s = TakeStore.Create(options, Guid.NewGuid().ToString(), "mux-fixture");
        await Send(s, 0, data[..firstMoof]); await Send(s, 1, data[firstMoof..]);
        const int samples = 24000;
        using (var writer = new BinaryWriter(File.Create(s.AudioPath)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 440 / 48000) * 2000));
        }
        s.Manifest.Audio = new AudioInfo(Clock.Now - 0.1,48000,1,16,samples);
        s.Manifest.End = new TakeEnd { VideoStartUnix = s.Manifest.Audio.StartUnix + 0.1, Duration = 0.2, Captured = 24, Encoded = 24 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = await Muxer.FinalizeAsync(s, args[0], timeout.Token);
        Assert(File.Exists(output) && new FileInfo(output).Length > 100);
        var start = new ProcessStartInfo(args[0]) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-hide_banner"); start.ArgumentList.Add("-i"); start.ArgumentList.Add(output);
        using var p = Process.Start(start)!; var info = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(timeout.Token);
        Assert(info.Contains("120 fps") && info.Contains("hevc") && info.Contains("aac"), info);
        Directory.CreateDirectory("qa"); File.Copy(output, Path.Combine("qa","mux-120fps.mp4"), true); File.WriteAllText(Path.Combine("qa","mux-probe.txt"), info);
    });
}
Console.WriteLine($"RESULT: {passed} passed, {failures.Count} failed");
Directory.CreateDirectory("qa");
File.WriteAllText(Path.Combine("qa","tests.json"), JsonSerializer.Serialize(new { passed, failed = failures.Count, failures }, Wire.Json));
try { Directory.Delete(root, true); } catch (IOException) { }
return failures.Count == 0 ? 0 : 1;
