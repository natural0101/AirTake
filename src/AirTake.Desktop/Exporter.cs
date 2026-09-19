using System.Diagnostics;
using System.Globalization;
using AirTake.Core;
namespace AirTake.Desktop;

public static class Exporter
{
    public static async Task<string> Export(TakeStore store, Guid id)
    {
        var manifest = store.Get(id); string folder = store.Folder(id);
        string target = Path.Combine(folder, "AirTake.mp4");
        if (manifest.State.StartsWith("exported", StringComparison.Ordinal) && File.Exists(target)) return target;
        var exe = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Не найден tools/ffmpeg.exe. Распакуйте весь ZIP, не переносите один EXE.");
        string video = Path.Combine(folder, "video.mp4"), audio = Path.Combine(folder, "microphone.wav");
        if (!File.Exists(video)) throw new FileNotFoundException("Видео ещё не собрано: дождитесь передачи всех фрагментов.");
        store.RequireSpace(new FileInfo(video).Length + (File.Exists(audio) ? new FileInfo(audio).Length : 0));
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", video };
        bool hasAudio = File.Exists(audio) && manifest.FirstAudioTimeMs is not null;
        if (hasAudio)
        {
            double offset = (manifest.FirstAudioTimeMs!.Value - manifest.Completion!.FirstVideoTimeMs + manifest.AudioOffsetMs) / 1000;
            args.AddRange(["-itsoffset", offset.ToString("F6", CultureInfo.InvariantCulture), "-i", audio,
                "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
                "-af", "aresample=async=1:first_pts=0,apad", "-shortest"]);
        }
        else args.AddRange(["-map", "0:v:0", "-an"]);
        args.AddRange(["-c:v", "copy", "-tag:v", "hvc1", "-movflags", "+faststart", "-f", "mp4", Path.Combine(folder, "export.tmp")]);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить FFmpeg");
        var errors = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); string log = await errors; await stdout;
        await File.WriteAllTextAsync(Path.Combine(folder, "export.log"), log);
        if (process.ExitCode != 0) throw new IOException("Ошибка экспорта. Оригиналы сохранены; подробности в export.log.");
        File.Move(Path.Combine(folder, "export.tmp"), target, true);
        await store.Update(id, m => { m.State = m.Completion!.Dropped == 0 && m.Completion.Reason == "user" ? "exported" : "exported-with-warning"; });
        return target;
    }
}
