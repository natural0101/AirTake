using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AirTake.Core;
namespace AirTake.Desktop;

public static class IntegrationSmoke
{
    public static async Task Run(string fixtureFolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "AirTake-integration-" + Guid.NewGuid().ToString("N"));
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0)); occupied.Listen(1);
        int busyPort = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        var preferences = new Preferences { Address = "127.0.0.1", Port = busyPort, OutputFolder = root, ReserveGiB = 1 };
        await using var receiver = new Receiver(preferences); await receiver.Start();
        Check(preferences.Port != busyPort && preferences.Port > 0, "automatic port conflict recovery");
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && Integrity.Hash(certificate.RawData) == receiver.Pairing!.Fingerprint };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(receiver.Pairing!.Url), Timeout = TimeSpan.FromSeconds(120) };
        var denied = await http.GetAsync("/api/time"); Check(denied.StatusCode == HttpStatusCode.Unauthorized, "authentication");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", preferences.Token);
        await Ensure(await http.GetAsync("/api/time"));
        // PostAsJsonAsync uses chunked transfer; it is a valid bounded request.
        await Ensure(await http.PostAsJsonAsync("/api/status", new PhoneStatus("Synthetic camera", 120, 0, 0, 0, 0, "nominal", null, null)));
        var take = receiver.Store.Create(new(), 0); var source = await File.ReadAllBytesAsync(Path.Combine(fixtureFolder, "source.mp4"));
        int half = source.Length/2;
        for(int i=0; i<2; i++)
        {
            var bytes = i == 0 ? source[..half] : source[half..];
            using var content = new ByteArrayContent(bytes); content.Headers.Add("X-Content-SHA256", Integrity.Hash(bytes));
            await Ensure(await http.PutAsync($"/api/takes/{take.Id}/chunks/{i}",content));
        }
        await Ensure(await http.PostAsJsonAsync($"/api/takes/{take.Id}/complete",new Completion(2,120,120,0,100000,1,"user")));
        var reconstructed = await File.ReadAllBytesAsync(Path.Combine(receiver.Store.Folder(take.Id), "video.mp4"));
        Check(Integrity.Hash(source)==Integrity.Hash(reconstructed), "TLS upload/reassembly exact bytes");
        File.Copy(Path.Combine(fixtureFolder,"microphone.wav"),Path.Combine(receiver.Store.Folder(take.Id),"microphone.wav"));
        await receiver.Store.Update(take.Id,m=> { m.FirstAudioTimeMs=100000; m.AudioChannels=1; m.AudioSampleRate=48000; });
        string result;
        try { result = await Exporter.Export(receiver.Store,take.Id); }
        catch (Exception ex)
        {
            string logPath = Path.Combine(receiver.Store.Folder(take.Id), "export.log");
            throw new IOException(File.Exists(logPath) ? File.ReadAllText(logPath) : "No export log", ex);
        }
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"tools","ffprobe.exe")) { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true };
        foreach(var arg in new[]{"-v","error","-show_streams","-of","json",result}) info.ArgumentList.Add(arg);
        using var probe=Process.Start(info)!; string json=await probe.StandardOutput.ReadToEndAsync(); string error=await probe.StandardError.ReadToEndAsync(); await probe.WaitForExitAsync();
        Check(probe.ExitCode==0,"ffprobe: "+error);
        using var document=JsonDocument.Parse(json); var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
        Check(video.GetProperty("width").GetInt32()==3840 && video.GetProperty("height").GetInt32()==2160,"4K dimensions");
        Check(video.GetProperty("codec_name").GetString()=="hevc","HEVC copy");
        Check(video.GetProperty("avg_frame_rate").GetString()=="120/1","120 FPS preserved");
        Check(streams.Any(s=>s.GetProperty("codec_type").GetString()=="audio" && s.GetProperty("sample_rate").GetString()=="48000"),"48 kHz audio mux");
        await File.WriteAllTextAsync(Path.Combine(fixtureFolder,"integration-result.txt"),"PASS: occupied-port recovery, TLS pin, authentication, chunked JSON, upload, reassembly, HEVC 3840x2160/120, AAC 48 kHz. Synthetic fixture; real camera NOT tested.\n"+json);
    }
    private static async Task Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new IOException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    private static void Check(bool condition,string test) { if(!condition) throw new InvalidOperationException("Integration failed: "+test); }
}
