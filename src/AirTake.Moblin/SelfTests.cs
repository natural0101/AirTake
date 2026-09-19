using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AirTake.Moblin;

internal static class SelfTests
{
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(new { name, pass = true }); }
        CaptureSession? receiver = null;
        try
        {
            await MediaTools.CheckAsync();
            var options = new CaptureSettings { Directory = Path.GetFullPath(output), RecordMicrophone = false, AutoReconnect = false, LatencyMs = 200 };
            Assert(options.CallerUrl.Contains("latency=200000"), "SRT latency uses microseconds");
            using var preset = JsonDocument.Parse(Uri.UnescapeDataString(options.MoblinUrl[10..]));
            Assert(preset.RootElement.GetProperty("streams")[0].GetProperty("video").GetProperty("fps").GetInt32() == 120, "Moblin preset asks for 120 FPS");
            var args = MediaTools.RecordingArguments(options, "test.mkv");
            Assert(args.Contains("copy") && !args.Contains("-r") && !args.Contains("-vf"), "Video stream-copy without synthetic FPS or scaling");
            Assert(args.Contains("-an"), "Phone audio is not silently used");
            options.RecordMicrophone = true; options.Microphone = "FIFINE Test";
            args = MediaTools.RecordingArguments(options, "test.mkv");
            Assert(args.Contains("audio=FIFINE Test") && args.Contains("1:a:0") && args.Contains("pcm_s16le"), "Microphone is a separate PC input, preserved as PCM");
            options.RecordMicrophone = false;
            Assert(!MediaTools.Redact(options.CallerUrl).Contains(options.Passphrase), "SRT secret redacted in logs");
            var devices = MediaTools.ParseMicrophones("[dshow @ 0] \"Microphone (FIFINE)\" (audio)\n[dshow @ 0] Alternative name \"@device_cm_test\"");
            Assert(devices.Count == 1 && devices[0].Id == "@device_cm_test", "DirectShow stable microphone identifier parsing");
            var fixture = Path.Combine(Path.GetFullPath(output), "source-4k120.ts");
            var generation = await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=black:s=3840x2160:r=120", "-frames:v", "120", "-an", "-c:v", "libx265", "-preset", "ultrafast", "-crf", "36", "-x265-params", "pools=2:frame-threads=2:log-level=error:keyint=120:bframes=0:repeat-headers=1", "-f", "mpegts", fixture], 180);
            Assert(generation.Code == 0, "Generated real HEVC 3840x2160/120 source");
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) options.Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            receiver = new CaptureSession(options);
            receiver.Log += line => File.AppendAllText(Path.Combine(output, "integration.log"), line + "\n");
            receiver.Start();
            await Task.Delay(1500);
            var send = await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-hide_banner", "-re", "-stream_loop", "2", "-i", fixture, "-map", "0:v:0", "-c:v", "copy", "-an", "-f", "mpegts", options.CallerUrl], 35);
            Assert(send.Code == 0, "Encrypted SRT loopback sender completed");
            await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            Assert(receiver.Files.Length == 1, "Receiver persisted a Matroska recording");
            var probe = await MediaTools.RunAsync(MediaTools.Ffprobe, ["-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_read_packets", "-of", "json", receiver.Files[0]]);
            Assert(probe.Code == 0, "Recorded file opens with ffprobe");
            File.WriteAllText(Path.Combine(output, "recording-probe.json"), probe.Output);
            using var info = JsonDocument.Parse(probe.Output);
            var video = info.RootElement.GetProperty("streams")[0];
            Assert(video.GetProperty("width").GetInt32() == 3840 && video.GetProperty("height").GetInt32() == 2160, "Received actual 3840x2160 dimensions");
            Assert(video.GetProperty("codec_name").GetString() == "hevc", "HEVC retained");
            Assert(video.GetProperty("r_frame_rate").GetString() == "120/1", "120 FPS retained in recording");
            Assert(int.Parse(video.GetProperty("nb_read_packets").GetString()!) >= 300, "At least 300 real video packets persisted from three loops");
            var waiting = new CaptureSession(options); waiting.Start(); await Task.Delay(700);
            var stopwatch = Stopwatch.StartNew(); await waiting.StopAsync();
            Assert(stopwatch.Elapsed.TotalSeconds < 8, "Stop while awaiting sender terminates cleanly");
            results.Add(new { name = "Physical iPhone 16 Pro + Wi-Fi + Fifine", pass = (bool?)null, status = "NOT TESTED: physical devices unavailable on CI" });
            File.WriteAllText(Path.Combine(output, "tests.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            if (receiver?.Running == true) await receiver.StopAsync();
            results.Add(new { name = "Failure", pass = false, error = e.ToString() });
            File.WriteAllText(Path.Combine(output, "tests.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
