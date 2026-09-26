using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveSubtitles.App.Audio;

public sealed record OutputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Fallback: captures everything played on one output device (includes other apps, but never the microphone).</summary>
public sealed class DeviceLoopbackSource : IAudioSource
{
    private readonly string? _deviceId;
    private readonly ILog _log;
    private WasapiRecorder? _recorder;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;

    public DeviceLoopbackSource(string? deviceId, ILog log)
    {
        _deviceId = deviceId;
        _log = log;
        DisplayName = "Output device";
    }

    public string DisplayName { get; private set; }

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception?>? Stopped;

    public static List<OutputDevice> ListDevices()
    {
        using var en = new MMDeviceEnumerator();
        var list = new List<OutputDevice>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (d) list.Add(new OutputDevice(d.ID, d.FriendlyName));
        return list;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(_deviceId)
            ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : _enumerator.GetDevice(_deviceId);
        DisplayName = _device.FriendlyName;
        _recorder = new WasapiRecorderBuilder().WithDevice(_device).WithLoopbackCapture().WithBufferLength(40).Build();
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        _recorder.StartRecording();
        _log.Info($"Device loopback capture started on {DisplayName}");
        StatusChanged?.Invoke(this, $"Capturing everything playing on {DisplayName}");
        return Task.CompletedTask;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _recorder!.WaveFormat;
        float[] mono = (flags & AudioClientBufferFlags.Silent) != 0
            ? new float[buffer.Length / format.BlockAlign]
            : WaveConvert.ToMonoFloat(buffer, format);
        if (mono.Length > 0) SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(mono, format.SampleRate));
    }

    public void Stop()
    {
        try { _recorder?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _recorder?.Dispose();
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}
