using LiveSubtitles.App.Audio;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Text;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.App.Services;

/// <summary>Builds and runs one subtitle session: audio source + pipeline + OpenAI connection.</summary>
public sealed class SessionController
{
    private readonly ILog _log;
    private IAudioSource? _source;
    private SubtitlePipeline? _pipeline;
    private IVoiceActivityDetector? _vad;

    public SessionController(ILog log, TranscriptStore transcript)
    {
        _log = log;
        Transcript = transcript;
    }

    public TranscriptStore Transcript { get; }
    public bool IsRunning => _pipeline != null;
    public bool IsPaused => _pipeline?.Paused ?? false;
    public IAudioSource? Source => _source;
    public AudioFileSource? FileSource => _source as AudioFileSource;

    public event Action<PipelineStatus>? StatusChanged;
    public event Action<SegmentDebugInfo>? SegmentDebug;
    public event Action<string>? RawEvent;
    public event Action<string>? SourceStatus;
    public event Action<float, float>? Level;
    public event Action<double>? AudioSent;
    public event Action<string>? ServerError;
    public event Action<Exception?>? SourceStopped;

    public async Task StartAsync(AppSettings settings, string apiKey, ISpeakerIdentifier speakers, ITextPostProcessor text, IAudioEffect? fileEffect)
    {
        if (IsRunning) return;
        _vad = CreateVad();
        var sessionConfig = settings.ToSessionConfig();
        var options = settings.ToPipelineOptions();
        int nextId = Transcript.Snapshot().Select(l => l.SegmentId).DefaultIfEmpty(0).Max() + 1;
        options = options with { Segmenter = options.Segmenter with { FirstSegmentId = nextId } };
        var pipeline = new SubtitlePipeline(
            options, _vad, settings.SpeakerIdEnabled ? speakers : new NoSpeakerIdentifier(),
            () => new RealtimeTranslationSession(sessionConfig, apiKey, _log),
            Transcript, _log, text);
        pipeline.StatusChanged += s => StatusChanged?.Invoke(s);
        pipeline.SegmentDebug += d => SegmentDebug?.Invoke(d);
        pipeline.RawEvent += e => RawEvent?.Invoke(e);
        pipeline.Level += (l, p) => Level?.Invoke(l, p);
        pipeline.AudioSent += ms => AudioSent?.Invoke(ms);
        pipeline.ServerError += e => ServerError?.Invoke(e);

        var source = CreateSource(settings, fileEffect);
        source.SamplesAvailable += (_, e) => pipeline.PushAudio(e.Samples, e.SampleRate);
        source.StatusChanged += (_, s) => SourceStatus?.Invoke(s);
        source.Stopped += (_, ex) => SourceStopped?.Invoke(ex);

        _pipeline = pipeline;
        _source = source;
        pipeline.Start();
        try
        {
            await source.StartAsync(CancellationToken.None);
        }
        catch
        {
            await StopAsync();
            throw;
        }
        _log.Info($"Session started: source={settings.Source}, output={settings.OutputLanguage}");
    }

    private IAudioSource CreateSource(AppSettings s, IAudioEffect? fileEffect) => s.Source switch
    {
        SourceKind.Discord => new AppAudioSource("Discord", ProcessTree.DiscordNames, s.FallbackToDeviceLoopback, s.OutputDeviceId, _log),
        SourceKind.App when !string.IsNullOrWhiteSpace(s.AppProcessName) =>
            new AppAudioSource(s.AppProcessName!, new[] { s.AppProcessName! }, s.FallbackToDeviceLoopback, s.OutputDeviceId, _log),
        SourceKind.App => throw new InvalidOperationException("Pick an app to capture first."),
        SourceKind.Device => new DeviceLoopbackSource(s.OutputDeviceId, _log),
        SourceKind.File when File.Exists(s.LastAudioFile) => new AudioFileSource(s.LastAudioFile!, s.PlayFileToSpeakers, fileEffect, _log),
        SourceKind.File => throw new InvalidOperationException("Choose an audio file first."),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private IVoiceActivityDetector CreateVad()
    {
        try
        {
            if (File.Exists(AppPaths.VadModel)) return new SileroVad(AppPaths.VadModel);
            _log.Warn("Silero VAD model missing; using the energy-based fallback detector");
        }
        catch (Exception ex)
        {
            _log.Error("Could not load Silero VAD; using the energy-based fallback detector", ex);
        }
        return new EnergyVad();
    }

    public void SetPaused(bool paused)
    {
        if (_pipeline != null) _pipeline.Paused = paused;
    }

    public void SetTextProcessor(ITextPostProcessor processor) => _pipeline?.SetTextProcessor(processor);

    public async Task StopAsync()
    {
        var source = _source;
        var pipeline = _pipeline;
        _source = null;
        _pipeline = null;
        try { source?.Dispose(); } catch (Exception ex) { _log.Warn("Stopping source", ex); }
        if (pipeline != null) await pipeline.StopAsync();
        _vad?.Dispose();
        _vad = null;
    }
}
