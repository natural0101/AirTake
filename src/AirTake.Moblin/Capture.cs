using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirTake.Moblin;

public sealed class CaptureSettings
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public int LatencyMs { get; set; } = 2000;
    public int TargetFps { get; set; } = 120;
    public int BitrateMbps { get; set; } = 120;
    public string Passphrase { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AirTake");
    public bool RecordMicrophone { get; set; } = true;
    public string Microphone { get; set; } = "";
    public int MicrophoneOffsetMs { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool Preview { get; set; }
    public bool ExportMp4 { get; set; }

    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Выберите IPv4-адрес сетевого адаптера компьютера.");
        if (Port is < 1024 or > 65535) throw new ArgumentException("Порт: 1024–65535.");
        if (LatencyMs is < 200 or > 10000) throw new ArgumentException("Буфер SRT: 200–10000 мс.");
        if (TargetFps is not (30 or 60 or 120)) throw new ArgumentException("FPS пресета: 30, 60 или 120.");
        if (BitrateMbps is < 10 or > 200) throw new ArgumentException("Битрейт пресета: 10–200 Мбит/с.");
        if (!Regex.IsMatch(Passphrase, "^[A-Za-z0-9]{10,79}$")) throw new ArgumentException("Пароль SRT: 10–79 латинских букв или цифр.");
        if (RecordMicrophone && string.IsNullOrWhiteSpace(Microphone)) throw new ArgumentException("Выберите свой Fifine в списке микрофонов.");
        if (MicrophoneOffsetMs is < -10000 or > 10000) throw new ArgumentException("Сдвиг звука: от -10000 до 10000 мс.");
        if (!Path.IsPathFullyQualified(Directory)) throw new ArgumentException("Выберите полный путь к папке записи.");
    }

    // Two latency windows plus one second of headroom, with bounded memory use.
    // FFmpeg forwards ffs to SRTO_FC; the generous flow window prevents clipping RCVBUF.
    public long ReceiveBufferBytes => Math.Clamp(BitrateMbps * 125000L * (2L * LatencyMs + 1000) / 1000, 16777216L, 536870912L);
    public string CallerUrl => $"srt://{Address}:{Port}?mode=caller&latency={LatencyMs * 1000L}&passphrase={Passphrase}&pbkeylen=16";
    public string ListenerUrl => $"srt://{Address}:{Port}?mode=listener&transtype=live&latency={LatencyMs * 1000L}&rcvbuf={ReceiveBufferBytes}&ffs=1048576&passphrase={Passphrase}&pbkeylen=16";

    // This imports a Moblin preset; it does not remotely control an active camera.
    public string MoblinUrl => "moblin://?" + Uri.EscapeDataString(JsonSerializer.Serialize(new
    {
        streams = new[] { new {
            name = "AirTake", url = CallerUrl, selected = true,
            video = new { resolution = "3840x2160", fps = TargetFps, bitrate = BitrateMbps * 1000000,
                codec = "H.265/HEVC", bFrames = false, maxKeyFrameInterval = 1 },
            srt = new { latency = LatencyMs, adaptiveBitrateEnabled = false }
        } }
    }));
}

public sealed record MicrophoneDevice(string Name, string Id)
{
    public override string ToString() => Name;
}

public static class MediaTools
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "tools");
    public static string Ffmpeg => Path.Combine(Root, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Root, "ffprobe.exe");
    public static string Ffplay => Path.Combine(Root, "ffplay.exe");

    public static Process Create(string executable, IEnumerable<string> args, bool stdin = false)
    {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = stdin
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return new Process { StartInfo = info };
    }

    public static async Task<(int Code, string Output, string Error)> RunAsync(string executable, IEnumerable<string> args, int timeoutSeconds = 30)
    {
        using var p = Create(executable, args);
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        catch { try { p.Kill(true); } catch { } await p.WaitForExitAsync(); throw; }
        return (p.ExitCode, await output, await error);
    }

    public static async Task CheckAsync()
    {
        foreach (var exe in new[] { Ffmpeg, Ffprobe, Ffplay })
            if (!File.Exists(exe)) throw new FileNotFoundException("Распакуйте весь архив AirTake: папка tools должна лежать рядом с AirTake.exe.", exe);
        var check = await RunAsync(Ffmpeg, ["-hide_banner", "-protocols"]);
        if (check.Code != 0 || !Regex.IsMatch(check.Output + check.Error, @"(?m)^\s*srt\s*$"))
            throw new InvalidOperationException("В этой сборке FFmpeg нет SRT. Используйте полный архив AirTake из Releases.");
    }

    public static List<MicrophoneDevice> ParseMicrophones(string text)
    {
        var list = new List<MicrophoneDevice>();
        var pendingAudio = false;
        foreach (var line in text.Split('\n'))
        {
            var device = Regex.Match(line, "\"(?<name>[^\"]+)\" \\((?<kind>audio|video)\\)");
            if (device.Success)
            {
                pendingAudio = device.Groups["kind"].Value == "audio";
                if (pendingAudio) list.Add(new(device.Groups["name"].Value, device.Groups["name"].Value));
                continue;
            }
            var alternate = Regex.Match(line, "Alternative name \"(?<id>[^\"]+)\"");
            if (alternate.Success)
            {
                if (pendingAudio && list.Count > 0 && line.Contains("dshow", StringComparison.OrdinalIgnoreCase))
                    list[^1] = list[^1] with { Id = alternate.Groups["id"].Value };
                pendingAudio = false;
            }
        }
        return list;
    }

    public static async Task<List<MicrophoneDevice>> MicrophonesAsync()
    {
        var result = await RunAsync(Ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], 20);
        return ParseMicrophones(result.Error);
    }

    public static List<string> RecordingArguments(CaptureSettings s, string file, int previewPort = 0)
    {
        s.Validate();
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-nostats", "-y", "-stats_period", "0.5", "-progress", "pipe:1",
            "-thread_queue_size", "2048", "-probesize", "4194304", "-analyzeduration", "1000000", "-i", s.ListenerUrl };
        if (s.RecordMicrophone)
        {
            args.AddRange(["-thread_queue_size", "512", "-itsoffset", (s.MicrophoneOffsetMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),
                "-f", "dshow", "-audio_buffer_size", "50", "-i", "audio=" + s.Microphone]);
        }
        args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        if (s.RecordMicrophone) args.AddRange(["-map", "1:a:0", "-c:a", "pcm_s16le", "-ar", "48000", "-shortest"]);
        else args.Add("-an");
        args.AddRange(["-max_interleave_delta", "1000000", "-cluster_time_limit", "1000", "-flush_packets", "1", "-f", "matroska", file]);
        if (previewPort > 0)
            args.AddRange(["-map", "0:v:0", "-c:v", "copy", "-an", "-f", "mpegts", $"udp://127.0.0.1:{previewPort}?pkt_size=1316"]);
        // No -r, fps filter or video encoder: preserve the incoming stream.
        return args;
    }

    public static long FreeBytes(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    public static string Redact(string message) => Regex.Replace(message, @"passphrase=[^&\s\""']+", "passphrase=REDACTED", RegexOptions.IgnoreCase);
}

public sealed class CaptureSession
{
    private readonly CaptureSettings settings;
    private readonly object gate = new();
    private readonly List<string> files = new();
    private readonly List<object> parts = new();
    private CancellationTokenSource? cancel;
    private Task? worker;
    private Process? process;
    private Process? preview;
    private long bytes;
    private long frames;
    private long durationUs;
    private string state = "Готово";
    private string stream = "Нет входящего видео";
    public event Action<string>? Log;
    public string State => Volatile.Read(ref state);
    public string Stream => Volatile.Read(ref stream);
    public long Bytes => Interlocked.Read(ref bytes);
    public long Frames => Interlocked.Read(ref frames);
    public double Seconds => Interlocked.Read(ref durationUs) / 1000000.0;
    public bool Running => worker is { IsCompleted: false };
    public string SessionDirectory { get; private set; } = "";
    public string[] Files { get { lock (gate) return files.ToArray(); } }
    public Task Completion => worker ?? Task.CompletedTask;

    public CaptureSession(CaptureSettings settings) => this.settings = settings;
    private void SetState(string text) => Volatile.Write(ref state, text);
    private void Message(string message) { try { Log?.Invoke(MediaTools.Redact(message)); } catch { } }

    public void Start()
    {
        if (Running) throw new InvalidOperationException("Запись уже запущена.");
        settings.Validate();
        System.IO.Directory.CreateDirectory(settings.Directory);
        if (MediaTools.FreeBytes(settings.Directory) is >= 0 and < 1073741824)
            throw new IOException("Для старта нужен минимум 1 ГБ свободного места.");
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Parse(settings.Address), settings.Port))) { }
        SessionDirectory = Path.Combine(settings.Directory, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(SessionDirectory);
        cancel = new CancellationTokenSource();
        SetState("Ожидание Moblin — включите трансляцию на телефоне");
        worker = Task.Run(() => LoopAsync(cancel.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var index = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                index++;
                var file = Path.Combine(SessionDirectory, $"take-{index:000}.mkv");
                var logPath = Path.Combine(SessionDirectory, $"take-{index:000}.log");
                var forced = false;
                Interlocked.Exchange(ref bytes, 0);
                Interlocked.Exchange(ref frames, 0);
                Interlocked.Exchange(ref durationUs, 0);
                SetState(index == 1 ? "Ожидание Moblin" : "Переподключение — ожидание Moblin");
                var previewPort = 0;
                if (settings.Preview)
                {
                    using (var port = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) previewPort = ((IPEndPoint)port.Client.LocalEndPoint!).Port;
                    preview = MediaTools.Create(MediaTools.Ffplay, ["-hide_banner", "-loglevel", "error", "-framedrop", "-an", "-sync", "video", "-autoexit", "-window_title", "AirTake — просмотр", "-i", $"udp://127.0.0.1:{previewPort}?fifo_size=65536&overrun_nonfatal=1"]);
                    preview.Start();
                    _ = preview.StandardError.ReadToEndAsync();
                    _ = preview.StandardOutput.ReadToEndAsync();
                }
                using var p = MediaTools.Create(MediaTools.Ffmpeg, MediaTools.RecordingArguments(settings, file, previewPort), true);
                using var logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                var started = DateTimeOffset.UtcNow;
                lock (gate) process = p;
                p.Start();
                var errors = Task.Run(async () => {
                    while (await p.StandardError.ReadLineAsync() is { } line)
                    {
                        line = MediaTools.Redact(line);
                        await logWriter.WriteLineAsync(line);
                        if (line.Contains("Video:", StringComparison.Ordinal)) Volatile.Write(ref stream, line.Trim());
                        if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase)) Message(line);
                    }
                });
                var progress = Task.Run(async () => {
                    while (await p.StandardOutput.ReadLineAsync() is { } line)
                    {
                        var split = line.IndexOf('=');
                        if (split < 0 || !long.TryParse(line[(split + 1)..].Trim(), out var value)) continue;
                        switch (line[..split])
                        {
                            case "frame": Interlocked.Exchange(ref frames, value); break;
                            case "total_size": Interlocked.Exchange(ref bytes, value); if (value > 1000) SetState("● ЗАПИСЬ"); break;
                            case "out_time_us": Interlocked.Exchange(ref durationUs, Math.Max(0, value)); break;
                        }
                    }
                });
                long observedFrames = 0;
                var lastVideo = Stopwatch.GetTimestamp();
                var stalled = false;
                while (!p.HasExited && !token.IsCancellationRequested)
                {
                    if (MediaTools.FreeBytes(settings.Directory) is >= 0 and < 536870912)
                    {
                        Message("Остановка: на диске осталось менее 512 МБ. Исходные файлы сохранены.");
                        cancel?.Cancel(); break;
                    }
                    var currentFrames = Frames;
                    if (currentFrames != observedFrames) { observedFrames = currentFrames; lastVideo = Stopwatch.GetTimestamp(); }
                    else if (observedFrames > 0 && Stopwatch.GetElapsedTime(lastVideo).TotalSeconds > Math.Max(20, settings.LatencyMs / 1000.0 * 3))
                    {
                        stalled = true;
                        SetState("Видеопоток остановился — сохранение части");
                        Message("Нет новых видеокадров: заканчиваем текущую часть, вместо бесконечной записи одного звука.");
                        break;
                    }
                    await Task.Delay(200);
                }
                if (!p.HasExited)
                {
                    try { await p.StandardInput.WriteLineAsync("q"); await p.StandardInput.FlushAsync(); } catch { }
                    try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                    catch (TimeoutException) { forced = true; try { p.Kill(true); } catch { } await p.WaitForExitAsync(); }
                }
                await Task.WhenAll(errors, progress);
                lock (gate) process = null;
                StopPreview();
                var length = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (length > 1000) { lock (gate) files.Add(file); }
                parts.Add(new { file = Path.GetFileName(file), startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                    bytes = length, reportedFrames = Frames, reportedDurationSeconds = Seconds, exitCode = p.ExitCode, forcedStop = forced, videoStalled = stalled });
                await SaveManifestAsync();
                Message($"Часть {index}: {length / 1000000.0:F1} МБ, кадров по FFmpeg: {Frames}. Код выхода: {p.ExitCode}.");
                if (token.IsCancellationRequested || !settings.AutoReconnect) break;
                if (index >= 30) { Message("Остановка после 30 частей: проверьте сеть и журнал."); break; }
                Message("Поток завершился. Ожидается новое подключение; разрыв не восстанавливается задним числом.");
                try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
            }
            SetState("Запись остановлена");
            if (settings.ExportMp4 && Files.Length > 0)
            {
                SetState("Сборка MP4 — исходные MKV сохраняются");
                foreach (var source in Files)
                {
                    if (MediaTools.FreeBytes(settings.Directory) is var free && free >= 0 && free < new FileInfo(source).Length + 536870912)
                    { Message("Недостаточно места для MP4. Исходный MKV сохранён."); continue; }
                    var final = Path.ChangeExtension(source, ".mp4");
                    var partial = Path.ChangeExtension(source, ".partial.mp4");
                    var result = await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-hide_banner", "-y", "-i", source, "-map", "0:v:0", "-map", "0:a?", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", partial], 3600);
                    if (result.Code == 0) File.Move(partial, final, true);
                    else Message("Не удалось собрать MP4. MKV сохранён. " + result.Error[^Math.Min(600, result.Error.Length)..]);
                }
                SetState("Готово — файлы сохранены");
            }
        }
        catch (Exception e) { SetState("Ошибка — смотрите журнал"); Message(e.ToString()); }
        finally
        {
            lock (gate) { if (process is { } remaining) { try { if (!remaining.HasExited) remaining.Kill(true); } catch { } } process = null; }
            StopPreview();
            try { await SaveManifestAsync(); } catch (Exception e) { Message(e.Message); }
        }
    }

    private Task SaveManifestAsync() => File.WriteAllTextAsync(Path.Combine(SessionDirectory, "session.json"), JsonSerializer.Serialize(new {
        application = "AirTake Moblin 0.2.0", targetResolution = "3840x2160", targetFps = settings.TargetFps,
        targetBitrateMbps = settings.BitrateMbps, sourceIsUnverifiedUntilReceived = true,
        microphone = settings.RecordMicrophone ? settings.Microphone : null, microphoneOffsetMs = settings.MicrophoneOffsetMs,
        automaticExactAvSync = false, recoverMissingSourceFrames = false, parts
    }, new JsonSerializerOptions { WriteIndented = true }));

    private void StopPreview()
    {
        var p = preview; preview = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        p.Dispose();
    }

    public async Task StopAsync()
    {
        cancel?.Cancel();
        if (worker is not null) await worker;
    }
}
