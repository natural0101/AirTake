using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirTake.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace AirTake.Desktop;

public sealed class Receiver(Preferences preferences) : IAsyncDisposable
{
    private WebApplication? app;
    private X509Certificate2? certificate;
    private readonly SemaphoreSlim uploads = new(2);
    private readonly SemaphoreSlim completions = new(1);
    private Control command = new(null, false, preferences.Capture);
    public TakeStore Store { get; } = new(preferences.OutputFolder, (long)preferences.ReserveGiB * 1024 * 1024 * 1024);
    private PhoneStatus? phone;
    public PhoneStatus? Phone
    {
        get
        {
            var current = Volatile.Read(ref command); var snapshot = phone;
            // Old-take errors must not stop a newly requested take before the phone has seen it.
            return current.Recording && snapshot is not null && snapshot.TakeId != current.TakeId
                ? snapshot with { Error = null } : snapshot;
        }
        private set { phone = value; }
    }
    public double LastSeenMs { get; private set; }
    public bool PhoneOnline => Clock.NowMs - LastSeenMs < 5000;
    public Pairing? Pairing { get; private set; }
    public Action<string>? Log { get; set; }
    public Func<Guid, Task>? OnCompleted { get; set; }
    public void SetCommand(Guid? id, bool recording) => Volatile.Write(ref command, new(id, recording, preferences.Capture));
    public async Task Start()
    {
        Directory.CreateDirectory(Preferences.DataFolder);
        var certPath = Path.Combine(Preferences.DataFolder, "receiver.pfx");
        if (File.Exists(certPath)) certificate = new X509Certificate2(certPath, (string?)null, X509KeyStorageFlags.Exportable);
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=AirTake local receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
            var bytes = created.Export(X509ContentType.Pfx);
            File.WriteAllBytes(certPath, bytes);
            certificate = new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.Exportable);
        }
        Pairing = new(1, $"https://{preferences.Address}:{preferences.Port}", preferences.Token, Integrity.Hash(certificate.RawData));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = TakeStore.MaximumChunkBytes;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(IPAddress.Parse(preferences.Address), preferences.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https => { https.ServerCertificate = certificate; https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13; });
            });
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            string auth = context.Request.Headers.Authorization.ToString();
            if (!Integrity.SecretEquals("Bearer " + preferences.Token, auth)) { context.Response.StatusCode = 401; return; }
            try { await next(context); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FileNotFoundException or InvalidOperationException or IOException)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = ex is FileNotFoundException ? 404 : ex is IOException ? 507 : 409;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message }); Log?.Invoke(ex.Message);
            }
        });
        app.MapGet("/api/time", () => Results.Json(new { serverTimeMs = Clock.NowMs }));
        app.MapPost("/api/status", async (HttpContext context) =>
        {
            if (context.Request.ContentLength is null or > 512_000) return Results.BadRequest();
            var status = await context.Request.ReadFromJsonAsync<PhoneStatus>();
            if (status is null || status.Name.Length > 256 || status.Thermal.Length > 64 || (status.Error?.Length ?? 0) > 2048) return Results.BadRequest();
            Phone = status; LastSeenMs = Clock.NowMs;
            return Results.Json(Volatile.Read(ref command));
        });
        app.MapPut("/api/takes/{id:guid}/chunks/{sequence:int}", async (Guid id, int sequence, HttpContext context) =>
        {
            if (context.Request.ContentLength is not long length) return Results.BadRequest();
            await uploads.WaitAsync(context.RequestAborted);
            try
            {
                var receipt = await Store.Put(id, sequence, context.Request.Headers["X-Content-SHA256"].ToString(),
                    context.Request.Body, length, context.RequestAborted);
                return Results.Json(receipt);
            }
            finally { uploads.Release(); }
        });
        app.MapPost("/api/takes/{id:guid}/complete", async (Guid id, HttpContext context) =>
        {
            if (context.Request.ContentLength is null or > 16_384) return Results.BadRequest();
            var completion = await context.Request.ReadFromJsonAsync<Completion>();
            if (completion is null) return Results.BadRequest();
            // Once commit starts, a client timeout must not cancel a durable seal/export.
            await completions.WaitAsync();
            try
            {
                await Store.Seal(id, completion, CancellationToken.None);
                if (OnCompleted is not null) await OnCompleted(id);
                return Results.Json(new { ok = true });
            }
            finally { completions.Release(); }
        });
        app.MapGet("/api/takes/{id:guid}", (Guid id) => Results.Json(Store.Get(id)));
        await app.StartAsync(); Log?.Invoke("Приёмник запущен. TLS и проверка SHA-256 включены.");
    }
    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); }
        certificate?.Dispose();
    }
}
