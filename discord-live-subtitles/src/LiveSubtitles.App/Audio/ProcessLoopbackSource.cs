using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveSubtitles.App.Audio;

/// <summary>
/// Captures only the audio rendered by one process tree using the Windows 10 2004+ process-loopback API
/// (ActivateAudioInterfaceAsync + AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, via NAudio). The user's microphone and
/// other apps are not included.
/// </summary>
public sealed class ProcessLoopbackSource : IAudioSource
{
    private readonly int _pid;
    private readonly ILog _log;
    private WasapiRecorder? _recorder;
    private WaveFormat? _format;

    public ProcessLoopbackSource(int pid, string displayName, ILog log)
    {
        _pid = pid;
        _log = log;
        DisplayName = displayName;
    }

    public string DisplayName { get; }
    public int ProcessId => _pid;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception?>? Stopped;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Per-app audio capture needs Windows 10 version 2004 (build 19041) or newer.");
        // 48 kHz stereo 16-bit is what Microsoft's ApplicationLoopback sample uses; the engine converts to it (AUTOCONVERTPCM).
        _format = new WaveFormat(48000, 16, 2);
        _recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)_pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(_format)
            .WithBufferLength(40)
            .BuildAsync().ConfigureAwait(false);
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) _log.Warn($"Process capture for PID {_pid} stopped", e.Exception);
            Stopped?.Invoke(this, e.Exception);
        };
        _recorder.StartRecording();
        _log.Info($"Process loopback capture started for {DisplayName} (PID {_pid})");
        StatusChanged?.Invoke(this, $"Capturing {DisplayName} (PID {_pid})");
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _format!;
        float[] mono = (flags & AudioClientBufferFlags.Silent) != 0
            ? new float[buffer.Length / format.BlockAlign]
            : WaveConvert.ToMonoFloat(buffer, format);
        if (mono.Length > 0) SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(mono, format.SampleRate));
    }

    public void Stop()
    {
        try { _recorder?.StopRecording(); } catch (Exception ex) { _log.Debug("Stop capture: " + ex.Message); }
    }

    public void Dispose()
    {
        Stop();
        _recorder?.Dispose();
        _recorder = null;
    }
}
