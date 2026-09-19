using AirTake.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirTake.Desktop;

internal sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed class FifineRecorder : IAudioRecorder
{
    private readonly object gate = new();
    private WasapiCapture? capture;
    private MMDevice? device;
    private WaveFileWriter? writer;
    private TaskCompletionSource<AudioInfo?>? stopped;
    private AudioInfo? lastInfo;
    private long bytes;
    private double firstSample = double.NaN;
    private double lastFlush;
    private string? error;
    private float peak;
    public string? Error => Volatile.Read(ref error);
    public float Peak => Volatile.Read(ref peak);
    public string FormatDescription { get; private set; } = "WASAPI · запись с выбранного микрофона";

    public static AudioDevice[] Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToArray();
    }
    public Task StartAsync(string path, string deviceId)
    {
        lock (gate)
        {
            if (capture is not null) throw new InvalidOperationException("Microphone is already recording.");
            if (string.IsNullOrWhiteSpace(deviceId)) throw new InvalidOperationException("Select the Fifine microphone first.");
            error = null; bytes = 0; firstSample = double.NaN; lastInfo = null; peak = 0;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                device = enumerator.GetDevice(deviceId);
                capture = new WasapiCapture(device, true, 20);
                writer = new WaveFileWriter(path, capture.WaveFormat);
                FormatDescription = $"{device.FriendlyName} · {capture.WaveFormat.SampleRate} Hz · {capture.WaveFormat.BitsPerSample} bit · {capture.WaveFormat.Channels} ch";
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                capture.StartRecording();
            }
            catch
            {
                writer?.Dispose(); writer = null;
                capture?.Dispose(); capture = null;
                device?.Dispose(); device = null;
                throw;
            }
        }
        return Task.CompletedTask;
    }
    private void OnData(object? sender, WaveInEventArgs e)
    {
        bool stopOnError = false;
        lock (gate)
        {
            if (writer is null || capture is null || e.BytesRecorded == 0) return;
            try
            {
                var format = capture.WaveFormat;
                if (double.IsNaN(firstSample)) firstSample = Clock.Now - e.BytesRecorded / (double)format.AverageBytesPerSecond;
                if (bytes + e.BytesRecorded > 3_900_000_000L) throw new IOException("WAV approaches its 4 GB limit; stop and start a new take.");
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                bytes += e.BytesRecorded;
                if (Clock.Now - lastFlush > 1) { writer.Flush(); lastFlush = Clock.Now; }
                float maximum = 0;
                var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                for (var i = 0; i + format.BlockAlign <= e.BytesRecorded; i += format.BlockAlign)
                {
                    var sample = isFloat && format.BitsPerSample == 32 ? BitConverter.ToSingle(e.Buffer, i)
                        : format.BitsPerSample == 16 ? BitConverter.ToInt16(e.Buffer, i) / 32768f
                        : format.BitsPerSample == 32 ? BitConverter.ToInt32(e.Buffer, i) / 2147483648f : 0;
                    if (float.IsFinite(sample)) maximum = Math.Max(maximum, Math.Abs(sample));
                }
                Volatile.Write(ref peak, maximum);
            }
            catch (Exception exception) { error = exception.Message; stopOnError = true; }
        }
        if (stopOnError) capture?.StopRecording();
    }
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            if (e.Exception is not null) error = e.Exception.Message;
            if (capture is not null)
            {
                var format = capture.WaveFormat;
                lastInfo = new AudioInfo(double.IsNaN(firstSample) ? Clock.Now : firstSample, format.SampleRate, format.Channels, format.BitsPerSample, bytes / format.BlockAlign, error);
            }
            try { writer?.Dispose(); }
            catch (Exception exception) { error = exception.Message; }
            writer = null;
            Volatile.Write(ref peak, 0);
            stopped?.TrySetResult(lastInfo);
        }
    }
    public async Task<AudioInfo?> StopAsync()
    {
        WasapiCapture? instance;
        Task<AudioInfo?>? completion;
        lock (gate) { instance = capture; completion = stopped?.Task; }
        if (instance is null || completion is null) return lastInfo;
        instance.StopRecording();
        var info = await completion.WaitAsync(TimeSpan.FromSeconds(10));
        lock (gate)
        {
            instance.DataAvailable -= OnData;
            instance.RecordingStopped -= OnStopped;
            instance.Dispose(); capture = null;
            device?.Dispose(); device = null;
        }
        return info;
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}
