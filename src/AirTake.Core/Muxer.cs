using System.Diagnostics;
using System.Globalization;

namespace AirTake.Core;

public static class Muxer
{
    public static string Locate(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath)) throw new FileNotFoundException("Configured FFmpeg does not exist.", configuredPath);
            return Path.GetFullPath(configuredPath);
        }
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name);
        if (File.Exists(bundled)) return bundled;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("FFmpeg was not found. Extract the ENTIRE AirTake ZIP, including its tools folder.");
    }
    public static List<string> Arguments(TakeStore store, string output)
    {
        var manifest = store.Manifest;
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", "pipe:0" };
        var withAudio = !manifest.VideoOnly && manifest.Audio is not null && File.Exists(store.AudioPath);
        if (!manifest.VideoOnly && !withAudio) throw new InvalidOperationException("Microphone audio is missing. Sources are preserved; use video-only recovery explicitly.");
        if (withAudio) { arguments.Add("-i"); arguments.Add(store.AudioPath); }
        arguments.AddRange(["-map", "0:v:0", "-c:v", "copy", "-tag:v", "hvc1"]);
        if (withAudio)
        {
            var difference = manifest.Audio!.StartUnix - manifest.End!.VideoStartUnix + manifest.AudioOffsetMs / 1000d;
            if (!double.IsFinite(difference) || Math.Abs(difference) > 3600) throw new InvalidDataException("Invalid audio/video clock alignment; recover with corrected timing.");
            var filter = difference < 0
                ? "atrim=start=" + (-difference).ToString("F6", CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS"
                : "asetpts=PTS-STARTPTS,adelay=" + (difference * 1000).ToString("F3", CultureInfo.InvariantCulture) + ":all=1";
            filter += ",aresample=48000,apad";
            arguments.AddRange(["-map", "1:a:0", "-af", filter, "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-shortest"]);
        }
        if (manifest.End is { Duration: > 0 } end) arguments.AddRange(["-t", end.Duration.ToString("F6", CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-movflags", "+faststart", "-f", "mp4", output]);
        return arguments;
    }
    public static async Task<string> FinalizeAsync(TakeStore store, string configuredFfmpeg, CancellationToken cancellationToken = default)
    {
        var executable = Locate(configuredFfmpeg);
        TakeStore.EnsureSpace(store.DirectoryPath, store.Manifest.Chunks.Sum(c => c.Bytes) + 256L * 1024 * 1024);
        var output = Path.Combine(store.DirectoryPath, "AirTake.mp4");
        var partial = output + ".partial";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var argument in Arguments(store, partial)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start FFmpeg.");
        await using var log = new StreamWriter(Path.Combine(store.DirectoryPath, "mux.log"), append: false);
        var stderr = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line) await log.WriteLineAsync(line); }, cancellationToken);
        try
        {
            await store.CopyVideoToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            await log.FlushAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length < 32) throw new IOException($"FFmpeg failed ({process.ExitCode}). See mux.log; original segments have not been removed.");
            File.Move(partial, output, true);
            return output;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await stderr; } catch (Exception) { /* Preserve the original failure. */ }
            throw;
        }
    }
}
