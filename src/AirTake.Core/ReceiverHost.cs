using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirTake.Core;

public sealed class ReceiverHost : IAsyncDisposable
{
    private readonly AppOptions options;
    private readonly Identity identity;
    private readonly IAudioRecorder audio;
    private readonly Func<TakeStore, Task<string>> finalize;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, TakeStore> stores = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> finishing = new();
    private WebApplication? app;
    private TakeStore? active;
    private volatile bool desired;
    private PhoneTelemetry telemetry = new();
    private long lastSeenTicks;
    private byte[]? preview;
    public string Status { get; private set; } = "Ожидание подключения iPhone";
    public PhoneTelemetry Telemetry => Volatile.Read(ref telemetry);
    public double LastSeenUnix => Interlocked.Read(ref lastSeenTicks) / 1000d;
    public bool PhoneConnected => Clock.Now - LastSeenUnix < 8;
    public bool DesiredRecording => desired;
    public string? ActiveId => active?.Manifest.Id;
    public bool Busy => active is not null || desired || finishing.Values.Any(t => t.IsValueCreated && !t.Value.IsCompleted);
    public byte[]? Preview => Volatile.Read(ref preview);
    public event Action<string>? Log;

    public ReceiverHost(AppOptions options, Identity identity, IAudioRecorder audio, Func<TakeStore, Task<string>>? finalize = null)
    {
        this.options = options; this.identity = identity; this.audio = audio;
        this.finalize = finalize ?? (store => Muxer.FinalizeAsync(store, options.FfmpegPath));
    }
    private void Report(string message) { Status = message; Log?.Invoke(message); }
    public void RequestRecording(bool record)
    {
        if (record)
        {
            options.Capture.Validate();
            if (!PhoneConnected) throw new InvalidOperationException("Сначала подключите AirTake Camera на iPhone.");
            if (Busy) throw new InvalidOperationException("Предыдущая запись ещё не завершена.");
            if (!options.VideoOnly && string.IsNullOrWhiteSpace(options.MicrophoneId)) throw new InvalidOperationException("Выберите микрофон Fifine.");
            TakeStore.EnsureSpace(options.OutputDirectory, 1024L * 1024 * 1024);
            _ = Muxer.Locate(options.FfmpegPath);
        }
        desired = record;
        Report(record ? "Команда REC отправлена на iPhone" : "Остановка; ожидаются оставшиеся фрагменты");
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        options.Capture.Validate();
        Directory.CreateDirectory(options.OutputDirectory);
        if (!IPAddress.TryParse(options.ListenAddress, out var address) || options.Port is < 1024 or > 65535) throw new ArgumentException("Выберите IPv4-адрес локального сетевого адаптера и порт 1024–65535.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ReceiverHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(json => { json.SerializerOptions.PropertyNamingPolicy = Wire.Json.PropertyNamingPolicy; });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = Wire.MaxChunkBytes;
            server.Limits.MaxConcurrentConnections = 8;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Listen(address, options.Port, endpoint => { endpoint.Protocols = HttpProtocols.Http1AndHttp2; endpoint.UseHttps(identity.Certificate); });
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                var expected = Encoding.UTF8.GetBytes("Bearer " + identity.Token);
                if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { context.Response.StatusCode = 401; return; }
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            {
                var code = exception is ProtocolException protocol ? protocol.Status : exception is ArgumentException or System.Text.Json.JsonException ? 400 : 500;
                if (code == 507) desired = false;
                Report(exception.Message);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = code;
                    await context.Response.WriteAsJsonAsync(new { error = exception.Message });
                }
            }
        });
        app.MapGet("/v1/clock", () => Results.Json(new { unixSeconds = Clock.Now, version = Wire.Version }));
        app.MapGet("/v1/control", () =>
        {
            if (active is not null && (audio.Error is not null || TakeStore.FreeBytes(options.OutputDirectory) < 512L * 1024 * 1024))
            {
                desired = false;
                Report(audio.Error ?? "Мало места на диске — остановка записи");
            }
            return Results.Json(new { version = Wire.Version, recording = desired, capture = options.Capture, activeId = ActiveId, status = Status });
        });
        app.MapPost("/v1/heartbeat", async (HttpContext context) =>
        {
            Limit(context, 64 * 1024);
            var value = await context.Request.ReadFromJsonAsync<PhoneTelemetry>(context.RequestAborted) ?? throw new ProtocolException(400, "Missing telemetry.");
            Volatile.Write(ref telemetry, value);
            Interlocked.Exchange(ref lastSeenTicks, (long)(Clock.Now * 1000));
            if (value.Status == "error") { desired = false; Report(value.Message); }
            return Results.Json(new { ok = true });
        });
        app.MapPost("/v1/intent", async (HttpContext context) =>
        {
            Limit(context, 4096);
            var intent = await context.Request.ReadFromJsonAsync<IntentRequest>(context.RequestAborted) ?? throw new ProtocolException(400, "Missing intent.");
            RequestRecording(intent.Recording);
            return Results.Json(new { recording = desired });
        });
        app.MapPost("/v1/start", async (HttpContext context) =>
        {
            Limit(context, 4096);
            var request = await context.Request.ReadFromJsonAsync<StartRequest>(context.RequestAborted) ?? throw new ProtocolException(400, "Missing start request.");
            var id = TakeStore.SafeId(request.Id);
            await lifecycle.WaitAsync(context.RequestAborted);
            try
            {
                if (active?.Manifest.Id == id) return Results.Json(new { id, capture = active.Manifest.Capture });
                if (!desired || active is not null) throw new ProtocolException(409, "Recording has not been armed, or another take is active.");
                var store = TakeStore.Create(options, id, request.ClientId);
                try { if (!options.VideoOnly) await audio.StartAsync(store.AudioPath, options.MicrophoneId); }
                catch (Exception exception) { store.Manifest.State = "failed"; store.Manifest.Note = exception.Message; store.Save(); desired = false; throw; }
                stores[id] = store;
                active = store;
                Report("REC · " + id);
                return Results.Json(new { id, capture = store.Manifest.Capture });
            }
            finally { lifecycle.Release(); }
        });
        app.MapPost("/v1/takes/{id}/chunks/{index:int}", async (string id, int index, HttpContext context) =>
        {
            var store = Store(id);
            Limit(context, Wire.MaxChunkBytes);
            var next = await store.WriteChunkAsync(index, context.Request.ContentLength!.Value, context.Request.Headers["X-SHA256"].ToString(), context.Request.Body, context.RequestAborted);
            return Results.Json(new { nextIndex = next });
        });
        app.MapPost("/v1/takes/{id}/end", async (string id, HttpContext context) =>
        {
            Limit(context, 16384);
            var end = await context.Request.ReadFromJsonAsync<TakeEnd>(context.RequestAborted) ?? throw new ProtocolException(400, "Missing capture metrics.");
            end.Validate();
            await EndAsync(Store(id), end);
            return Results.Json(new { ok = true });
        });
        app.MapPost("/v1/takes/{id}/finish", async (string id, HttpContext context) =>
        {
            Limit(context, 4096);
            var request = await context.Request.ReadFromJsonAsync<FinishRequest>(context.RequestAborted) ?? throw new ProtocolException(400, "Missing segment count.");
            var path = await FinishAsync(Store(id), request.Chunks);
            return Results.Json(new { ok = true, file = Path.GetFileName(path) });
        });
        app.MapPost("/v1/preview", async (HttpContext context) =>
        {
            Limit(context, 512 * 1024);
            using var stream = new MemoryStream();
            await context.Request.Body.CopyToAsync(stream, context.RequestAborted);
            var bytes = stream.ToArray();
            if (bytes.Length < 3 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new ProtocolException(400, "Expected a JPEG preview.");
            Volatile.Write(ref preview, bytes);
            return Results.Json(new { ok = true });
        });
        app.MapPost("/v1/speed", async (HttpContext context) =>
        {
            Limit(context, Wire.MaxChunkBytes);
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            return Results.Json(new { bytes = context.Request.ContentLength });
        });
        await app.StartAsync(cancellationToken);
        Report($"Приёмник запущен на {options.ListenAddress}:{options.Port}");
    }
    private static void Limit(HttpContext context, long maximum)
    {
        if (context.Request.ContentLength is not { } length || length < 0 || length > maximum) throw new ProtocolException(413, "A bounded Content-Length is required.");
    }
    private TakeStore Store(string id) => stores.GetOrAdd(TakeStore.SafeId(id), key => TakeStore.Load(options.OutputDirectory, key));
    private async Task EndAsync(TakeStore store, TakeEnd end)
    {
        await lifecycle.WaitAsync();
        try
        {
            await store.Gate.WaitAsync();
            try
            {
                if (store.Manifest.End is not null) return;
                if (active?.Manifest.Id == store.Manifest.Id)
                {
                    store.Manifest.Audio = await audio.StopAsync();
                    desired = false;
                }
                else if (!store.Manifest.VideoOnly) store.Manifest.Note = "Receiver restarted: microphone continuity must be checked.";
                store.Manifest.End = end;
                store.Manifest.State = "uploading";
                store.Save();
                Report("Камера остановлена · приём оставшихся фрагментов");
            }
            finally { store.Gate.Release(); }
        }
        finally { lifecycle.Release(); }
    }
    private async Task<string> FinishAsync(TakeStore store, int count)
    {
        if (store.Manifest.State == "complete" && File.Exists(store.Manifest.OutputFile)) return store.Manifest.OutputFile;
        var task = finishing.GetOrAdd(store.Manifest.Id, _ => new Lazy<Task<string>>(async () =>
        {
            await store.Gate.WaitAsync();
            try { store.ValidateComplete(count); store.Manifest.State = "finalizing"; store.Save(); }
            finally { store.Gate.Release(); }
            try
            {
                Report("Сборка MP4 без перекодирования видео…");
                var output = await finalize(store);
                await store.Gate.WaitAsync();
                try { store.Manifest.OutputFile = output; store.Manifest.State = "complete"; store.Save(); store.DeleteTemporarySources(); }
                finally { store.Gate.Release(); }
                if (active?.Manifest.Id == store.Manifest.Id) active = null;
                var dropped = store.Manifest.End!.CaptureDrops + store.Manifest.End.EncoderDrops + store.Manifest.End.WriterDrops;
                Report($"Сохранено: {output}" + (dropped > 0 || store.Manifest.End.Interrupted ? " · ПРОВЕРЬТЕ ПРОПУСКИ В take.json" : ""));
                return output;
            }
            catch (Exception exception)
            {
                await store.Gate.WaitAsync();
                try { store.Manifest.State = "finalize-failed"; store.Manifest.Note = exception.Message; store.Save(); }
                finally { store.Gate.Release(); }
                Report("Сборка не завершена; исходные фрагменты сохранены. " + exception.Message);
                throw;
            }
        })).Value;
        try { return await task; }
        catch { finishing.TryRemove(store.Manifest.Id, out _); throw; }
    }
    public async Task<string> RecoverAsync(string directory, bool videoOnly)
    {
        if (Busy) throw new InvalidOperationException("Сначала завершите текущую запись.");
        var store = TakeStore.Load(Path.GetDirectoryName(directory)!, Path.GetFileName(directory));
        if (videoOnly) store.Manifest.VideoOnly = true;
        store.Manifest.End ??= new TakeEnd { Interrupted = true, Error = "Recovered after interruption; final video segment may be missing." };
        store.Manifest.State = "uploading";
        store.Save();
        return await FinishAsync(store, store.Manifest.Chunks.Count);
    }
    public async ValueTask DisposeAsync()
    {
        desired = false;
        await lifecycle.WaitAsync();
        try
        {
            if (active is not null && active.Manifest.End is null)
            {
                active.Manifest.Audio = await audio.StopAsync();
                active.Manifest.Note = "Receiver closed before capture finished. Pending video may still be on the iPhone.";
                active.Save();
            }
            if (app is not null) { await app.StopAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); app = null; }
        }
        finally { lifecycle.Release(); }
    }
    private sealed record IntentRequest(bool Recording);
    private sealed record StartRequest(string Id, string ClientId);
    private sealed record FinishRequest(int Chunks);
}
