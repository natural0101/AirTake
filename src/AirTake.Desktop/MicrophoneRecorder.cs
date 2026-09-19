using AirTake.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace AirTake.Desktop;

public sealed record Microphone(string Id, string Name) { public override string ToString() => Name; }
public sealed class MicrophoneRecorder
{
    private WasapiCapture? capture;
    private MMDevice? device;
    private WaveFileWriter? writer;
    private readonly object gate = new();
    private TaskCompletionSource? stopped;
    private bool first;
    public double? FirstSampleTimeMs { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public Exception? Error { get; private set; }
    public static Microphone[] Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => { var item = new Microphone(d.ID, d.FriendlyName); d.Dispose(); return item; }).ToArray();
    }
    public void Start(string id, string path)
    {
        using var enumerator = new MMDeviceEnumerator(); device = enumerator.GetDevice(id);
        capture = new WasapiCapture(device); SampleRate = capture.WaveFormat.SampleRate; Channels = capture.WaveFormat.Channels;
        writer = new WaveFileWriter(path, capture.WaveFormat); first = true; Error = null;
        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, args) =>
        {
            lock (gate)
            {
                if (writer is null) return;
                try
                {
                    if (first) { FirstSampleTimeMs = Clock.NowMs - 1000.0 * args.BytesRecorded / capture.WaveFormat.AverageBytesPerSecond; first = false; }
                    // Standard WAV uses a 32-bit size. Stop well before its limit; never silently corrupt it.
                    if (writer.Length + args.BytesRecorded > 3_500_000_000) throw new IOException("Достигнут лимит WAV. Завершите дубль.");
                    writer.Write(args.Buffer, 0, args.BytesRecorded);
                }
                catch (Exception ex) { Error = ex; }
            }
        };
        capture.RecordingStopped += (_, args) =>
        {
            lock (gate) { Error ??= args.Exception; writer?.Dispose(); writer = null; }
            stopped.TrySetResult();
        };
        capture.StartRecording();
    }
    public async Task Stop()
    {
        if (capture is null) return;
        capture.StopRecording();
        if (stopped is not null) await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        capture.Dispose(); capture = null; device?.Dispose(); device = null;
    }
}
