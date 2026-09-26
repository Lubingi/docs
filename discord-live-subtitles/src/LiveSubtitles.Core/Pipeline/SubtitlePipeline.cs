using System.Collections.Concurrent;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Text;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Pipeline;

/// <summary>
/// The live subtitle pipeline:
/// audio source → resample (16 kHz for VAD/speakers, 24 kHz for OpenAI) → Silero VAD → segmenter →
/// (optional early speaker check for muted speakers) → translation connection → text attribution → transcript.
/// All state is owned by one pipeline thread; speaker embeddings run on a separate worker thread.
/// </summary>
public sealed class SubtitlePipeline : IAsyncDisposable
{
    private const int LiveSourceGapMs = 250;

    private readonly PipelineOptions _options;
    private readonly IVoiceActivityDetector _vad;
    private readonly ISpeakerIdentifier _speakers;
    private readonly TranslationConnection _connection;
    private readonly TranscriptStore _store;
    private readonly ILog _log;
    private readonly SpeechSegmenter _segmenter;
    private readonly StreamAttributor _translationAttr;
    private readonly StreamAttributor _originalAttr;

    private readonly BlockingCollection<Action> _inbox = new(new ConcurrentQueue<Action>());
    private readonly BlockingCollection<Action> _speakerWork = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private readonly Thread _speakerThread;

    private readonly Dictionary<int, SegmentState> _segments = new();
    private readonly List<TimelineSegment> _sent = new();          // segments in model-time order
    private readonly Queue<SendFrame> _sendQueue = new();
    private readonly List<(double ModelMs, DateTimeOffset Wall)> _modelWall = new();
    private readonly List<SegmenterEvent> _events = new();
    private readonly Queue<double> _recentLags = new();

    private StreamResampler? _to16, _to24;
    private int _sourceRate;
    private readonly List<float> _q16 = new(), _q24 = new();
    private DateTimeOffset _lastAudioWall = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStatus = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLevel = DateTimeOffset.MinValue;
    private bool _flushAfterPump;
    private int _finalizeFrom;
    private DateTimeOffset _lastTick = DateTimeOffset.MinValue;
    private bool _dropTailFrames;
    private bool _paused;
    private bool _running;
    private double? _lastFinalLatency;
    private float _levelPeak, _probPeak;
    private ITextPostProcessor _text;

    public SubtitlePipeline(
        PipelineOptions options,
        IVoiceActivityDetector vad,
        ISpeakerIdentifier speakers,
        Func<ITranslationSession> sessionFactory,
        TranscriptStore store,
        ILog log,
        ITextPostProcessor? textProcessor = null,
        ReconnectPolicy? reconnect = null)
    {
        _options = options;
        _vad = vad;
        _speakers = speakers;
        _store = store;
        _log = log;
        _text = textProcessor ?? NoTextPostProcessor.Instance;
        _segmenter = new SpeechSegmenter(options.Segmenter);
        // The original-language transcript aligns better with speech; it anchors where translated lines end.
        (_translationAttr, _originalAttr) = StreamAttributor.CreatePair(options.Attribution, () => _sent, options.ShowOriginal);
        _connection = new TranslationConnection(sessionFactory, reconnect, log);
        _connection.FrameSent += (tag, start, dur, wall) => Post(() => OnFrameSent(tag, start, dur, wall));
        _connection.DeltaReceived += d => Post(() => OnDelta(d));
        _connection.StateChanged += (s, m) => Post(() => { RawEvent?.Invoke($"connection: {s} — {m}"); PublishStatus(force: true); });
        _connection.ServerError += e => { RawEvent?.Invoke("error: " + e); ServerError?.Invoke(e); };
        _connection.AudioSentMs += ms => AudioSent?.Invoke(ms);
        _thread = new Thread(Run) { IsBackground = true, Name = "SubtitlePipeline" };
        _speakerThread = new Thread(RunSpeakerWorker) { IsBackground = true, Name = "SpeakerWorker", Priority = ThreadPriority.BelowNormal };
    }

    public event Action<SegmentDebugInfo>? SegmentDebug;
    public event Action<PipelineStatus>? StatusChanged;
    public event Action<string>? RawEvent;
    public event Action<string>? ServerError;
    /// <summary>Milliseconds of audio actually sent to OpenAI (for cost tracking).</summary>
    public event Action<double>? AudioSent;
    /// <summary>Input level (RMS 0..1) and speech probability, about 10 times a second.</summary>
    public event Action<float, float>? Level;

    public TranscriptStore Transcript => _store;
    public bool IsLiveSource { get; set; } = true;

    public bool Paused
    {
        get => _paused;
        set => Post(() => SetPaused(value));
    }

    public void SetTextProcessor(ITextPostProcessor processor) => Post(() => _text = processor);

    public void Start()
    {
        _running = true;
        _thread.Start();
        _speakerThread.Start();
        _connection.Start();
        _log.Info($"Pipeline started (VAD: {_vad.Name}, speakers: {(_speakers.Enabled ? "on" : "off")}, original text: {_options.ShowOriginal})");
    }

    /// <summary>Thread-safe: feed captured mono audio at any sample rate.</summary>
    public void PushAudio(float[] samples, int sampleRate)
    {
        if (!_running) return;
        Post(() => OnAudio(samples, sampleRate));
    }

    /// <summary>Test hook (CLI --drop-at): drop the OpenAI connection as if the network failed.</summary>
    public void SimulateConnectionDrop() => _connection.SimulateDrop();

    public async Task StopAsync()
    {
        if (!_running) return;
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            _events.Clear();
            _segmenter.Flush(_events);
            HandleEvents();
            flushed.TrySetResult();
        });
        await Task.WhenAny(flushed.Task, Task.Delay(2000)).ConfigureAwait(false);
        await _connection.StopAsync().ConfigureAwait(false);
        // Give late text a moment to land, then finalize everything.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { FinalizeAll(); done.TrySetResult(); });
        await Task.WhenAny(done.Task, Task.Delay(2000)).ConfigureAwait(false);
        _running = false;
        _inbox.CompleteAdding();
        _speakerWork.CompleteAdding();
        _thread.Join(2000);
        _speakerThread.Join(2000);
        _log.Info("Pipeline stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Post(Action action)
    {
        if (_inbox.IsAddingCompleted) return;
        try { _inbox.Add(action); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        while (!_inbox.IsCompleted)
        {
            try
            {
                if (_inbox.TryTake(out var action, 100)) action();
                OnTick();
            }
            catch (InvalidOperationException) when (_inbox.IsCompleted) { break; }
            catch (Exception ex)
            {
                _log.Error("Pipeline error", ex);
            }
        }
    }

    private void QueueSpeakerWork(Action work)
    {
        if (_speakerWork.IsAddingCompleted) return;
        try { _speakerWork.Add(work); } catch (InvalidOperationException) { }
    }

    private void RunSpeakerWorker()
    {
        foreach (var work in _speakerWork.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { _log.Error("Speaker identification error", ex); }
        }
    }

    // ---------------------------------------------------------------- audio

    private void OnAudio(float[] samples, int rate)
    {
        _lastAudioWall = DateTimeOffset.UtcNow;
        Ingest(samples, rate);
    }

    private void Ingest(float[] samples, int rate)
    {
        if (rate != _sourceRate || _to16 == null || _to24 == null)
        {
            _sourceRate = rate;
            _to16 = new StreamResampler(rate, IVoiceActivityDetector.SampleRate);
            _to24 = new StreamResampler(rate, TranslationProtocol.SampleRate);
            _q16.Clear();
            _q24.Clear();
        }
        _q16.AddRange(_to16.Process(samples));
        _q24.AddRange(_to24.Process(samples));

        const int n16 = IVoiceActivityDetector.FrameSize;
        const int n24 = n16 * 3 / 2;
        int frames = Math.Min(_q16.Count / n16, _q24.Count / n24);
        for (int i = 0; i < frames; i++)
        {
            var f16 = _q16.GetRange(i * n16, n16).ToArray();
            var f24 = _q24.GetRange(i * n24, n24).ToArray();
            ProcessFrame(f16, f24);
        }
        if (frames > 0)
        {
            _q16.RemoveRange(0, frames * n16);
            _q24.RemoveRange(0, frames * n24);
        }
    }

    private void ProcessFrame(float[] f16, float[] f24)
    {
        float sum = 0;
        foreach (var s in f16) sum += s * s;
        float rms = MathF.Sqrt(sum / f16.Length);
        _levelPeak = Math.Max(_levelPeak, rms);

        if (_paused) return;

        float prob = _vad.Process(f16);
        _probPeak = Math.Max(_probPeak, prob);
        _events.Clear();
        _segmenter.Process(f16, f24, prob, _events);
        HandleEvents();
    }

    private void HandleEvents()
    {
        foreach (var e in _events)
        {
            switch (e)
            {
                case SendFrame f:
                    _sendQueue.Enqueue(f);
                    if (f.SegmentId is { } sid && _segments.TryGetValue(sid, out var st)) st.QueuedFrames++;
                    break;
                case SegmentStarted s:
                    OnSegmentStarted(s);
                    break;
                case SegmentEarly early:
                    OnSegmentEarly(early);
                    break;
                case SegmentEnded ended:
                    OnSegmentEnded(ended);
                    break;
                case GateClosed:
                    _flushAfterPump = true;
                    break;
            }
        }
        PumpSendQueue();
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        if (paused)
        {
            _events.Clear();
            _segmenter.Flush(_events);
            HandleEvents();
            _vad.Reset();
        }
        _paused = paused;
        _log.Info(paused ? "Paused" : "Resumed");
        PublishStatus(force: true);
    }

    // ---------------------------------------------------------------- segments

    private void OnSegmentStarted(SegmentStarted s)
    {
        var now = DateTimeOffset.UtcNow;
        var st = new SegmentState(s.SegmentId)
        {
            StreamStartMs = s.StreamStartMs,
            WallStart = now - TimeSpan.FromMilliseconds(Math.Max(0, _segmenter.StreamMs - s.StreamStartMs)),
            Decision = _speakers.Enabled && _speakers.AnyMuted ? SegmentDecision.Pending : SegmentDecision.Allowed,
        };
        _segments[s.SegmentId] = st;
        _store.Upsert(BuildLine(st));
        PublishDebug(st);
    }

    private void OnSegmentEarly(SegmentEarly e)
    {
        if (!_segments.TryGetValue(e.SegmentId, out var st)) return;
        if (!_speakers.Enabled) return;
        var audio = e.Audio16;
        int id = e.SegmentId;
        QueueSpeakerWork(() =>
        {
            var match = _speakers.Peek(audio);
            bool muted = match.SpeakerId is { } sp && !match.Uncertain && match.Similarity >= _options.MuteMinSimilarity && _speakers.IsMuted(sp);
            Post(() => OnEarlyDecision(id, match, muted));
        });
    }

    private void OnEarlyDecision(int segmentId, SpeakerMatch match, bool muted)
    {
        if (!_segments.TryGetValue(segmentId, out var st)) return;
        if (st.Match == null && !match.Uncertain)
        {
            st.Match = match; // provisional label until the full segment is assigned; unsure guesses keep "…"
            st.SpeakerId = match.SpeakerId;
        }
        if (st.Decision == SegmentDecision.Pending)
        {
            st.Decision = muted ? SegmentDecision.Suppressed : SegmentDecision.Allowed;
            if (muted) _log.Debug($"Segment {segmentId}: muted speaker, not sent");
        }
        _store.Upsert(BuildLine(st));
        PumpSendQueue();
        PublishDebug(st);
    }

    private void OnSegmentEnded(SegmentEnded e)
    {
        if (!_segments.TryGetValue(e.SegmentId, out var st)) return;
        st.StreamEndMs = e.StreamEndMs;
        st.WallEnd = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, _segmenter.StreamMs - e.StreamEndMs));
        st.EndedBySegmenter = true;
        if (_speakers.Enabled)
        {
            var audio = e.Audio16;
            int id = e.SegmentId;
            QueueSpeakerWork(() =>
            {
                var match = _speakers.Assign(id, audio);
                Post(() => OnSpeakerAssigned(id, match));
            });
        }
        UpdateSpeechEnded(st);
        _store.Upsert(BuildLine(st));
        PublishDebug(st);
    }

    private void OnSpeakerAssigned(int segmentId, SpeakerMatch match)
    {
        if (!_segments.TryGetValue(segmentId, out var st)) return;
        st.Match = match;
        st.SpeakerId = match.SpeakerId;
        if (st.Decision == SegmentDecision.Pending)
        {
            bool muted = match.SpeakerId is { } sp && !match.Uncertain && _speakers.IsMuted(sp);
            st.Decision = muted ? SegmentDecision.Suppressed : SegmentDecision.Allowed;
            PumpSendQueue();
        }
        _store.Upsert(BuildLine(st));
        PublishDebug(st);
    }

    /// <summary>Sends queued frames in order, holding back segments whose speaker check is still pending.</summary>
    private void PumpSendQueue()
    {
        while (_sendQueue.Count > 0)
        {
            var f = _sendQueue.Peek();
            SegmentState? st = null;
            if (f.SegmentId is { } sid) _segments.TryGetValue(sid, out st);
            if (st?.Decision == SegmentDecision.Pending) break;
            _sendQueue.Dequeue();
            if (st != null) st.QueuedFrames--;

            if (st?.Decision == SegmentDecision.Suppressed)
            {
                _dropTailFrames = true;
                UpdateSpeechEnded(st);
                continue;
            }
            if (st == null && _dropTailFrames) continue; // silence after a muted speaker
            if (st != null)
            {
                _dropTailFrames = false;
                st.InFlightFrames++;
            }
            _connection.Append(f.Audio24, new FrameTag(f.SegmentId, f.IsSpeech));
        }
        if (_sendQueue.Count == 0 && _flushAfterPump)
        {
            _flushAfterPump = false;
            _connection.FlushPadding();
        }
    }

    private void OnFrameSent(FrameTag tag, double modelStart, double durationMs, DateTimeOffset wall)
    {
        _modelWall.Add((modelStart, wall));
        if (_modelWall.Count > 4000) _modelWall.RemoveRange(0, 1000);
        if (tag.SegmentId is not { } sid || !_segments.TryGetValue(sid, out var st)) return;
        if (st.Timeline.ModelStartMs == null)
        {
            st.Timeline.ModelStartMs = modelStart;
            st.Timeline.FirstSentWall = wall;
            _sent.Add(st.Timeline);
        }
        if (tag.IsSpeech) st.Timeline.ModelSpeechEndMs = modelStart + durationMs;
        st.Timeline.ModelSpeechEndMs ??= modelStart + durationMs;
        if (tag.Replay) return; // re-sent after a dropped connection: already counted
        st.InFlightFrames--;
        UpdateSpeechEnded(st);
    }

    private static void UpdateSpeechEnded(SegmentState st)
    {
        if (st.Timeline.SpeechEnded || !st.EndedBySegmenter || st.QueuedFrames > 0 || st.InFlightFrames > 0) return;
        st.Timeline.SpeechEnded = true;
        st.Timeline.EndSentWall = DateTimeOffset.UtcNow;
    }

    // ---------------------------------------------------------------- text

    private void OnDelta(TranscriptDelta d)
    {
        var attr = d.Stream == TranscriptStream.Translation ? _translationAttr : _originalAttr;
        var now = DateTimeOffset.UtcNow;
        int? sid = attr.Add(d.Text, d.ModelMs, now);
        RawEvent?.Invoke($"{(d.Stream == TranscriptStream.Translation ? "EN" : "SRC")} @{d.ModelMs:0}ms → seg {sid?.ToString() ?? "-"}: {d.Text}");
        if (sid is not { } id || !_segments.TryGetValue(id, out var st)) return;

        if (d.HadElapsed && WallForModel(d.ModelMs) is { } sentAt)
        {
            double lag = (now - sentAt).TotalMilliseconds;
            _recentLags.Enqueue(lag);
            while (_recentLags.Count > 30) _recentLags.Dequeue();
            st.LastLagMs = lag;
        }
        if (d.Stream == TranscriptStream.Translation && st.FirstTextLatencyMs == null)
            st.FirstTextLatencyMs = (now - st.WallStart).TotalMilliseconds;
        _store.Upsert(BuildLine(st));
        PublishDebug(st);
    }

    private DateTimeOffset? WallForModel(double modelMs)
    {
        if (_modelWall.Count == 0) return null;
        int lo = 0, hi = _modelWall.Count - 1;
        if (modelMs < _modelWall[0].ModelMs) return null;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_modelWall[mid].ModelMs <= modelMs) lo = mid; else hi = mid - 1;
        }
        return _modelWall[lo].Wall;
    }

    // ---------------------------------------------------------------- periodic

    private void OnTick()
    {
        if (!_running) return;
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastTick).TotalMilliseconds < 50) return; // Run() calls this after every message
        _lastTick = now;

        // Capture APIs deliver nothing while an app is silent; synthesize silence so segments can end.
        if (IsLiveSource && _sourceRate > 0 && _lastAudioWall != DateTimeOffset.MinValue)
        {
            double gap = (now - _lastAudioWall).TotalMilliseconds;
            if (gap > LiveSourceGapMs)
            {
                int n = (int)(gap / 1000.0 * _sourceRate);
                _lastAudioWall = now;
                Ingest(new float[n], _sourceRate);
            }
        }

        while (_finalizeFrom < _sent.Count && (!_segments.TryGetValue(_sent[_finalizeFrom].Id, out var f) || f.IsFinal))
            _finalizeFrom++;
        for (int i = _finalizeFrom; i < _sent.Count; i++)
        {
            var tl = _sent[i];
            if (!_segments.TryGetValue(tl.Id, out var st) || st.IsFinal) continue;
            bool done = _translationAttr.IsDone(tl, i, now) && (!_options.ShowOriginal || _originalAttr.IsDone(tl, i, now));
            if (done) Finalize(st, now);
        }
        foreach (var st in _segments.Values)
        {
            if (st.IsFinal || !st.EndedBySegmenter) continue;
            // Muted segments never reach the timeline; close them once the speaker has stopped.
            // Segments whose audio was dropped while offline are closed after a timeout.
            if (st.Decision == SegmentDecision.Suppressed || (st.WallEnd is { } end && (now - end).TotalSeconds > 30))
                Finalize(st, now);
        }

        if ((now - _lastLevel).TotalMilliseconds >= 100)
        {
            _lastLevel = now;
            Level?.Invoke(_levelPeak, _probPeak);
            _levelPeak = 0;
            _probPeak = 0;
        }
        PublishStatus(force: false);
        PruneOldSegments();
    }

    private void Finalize(SegmentState st, DateTimeOffset now)
    {
        st.IsFinal = true;
        if (st.WallEnd is { } end && st.Decision == SegmentDecision.Allowed)
        {
            st.FinalLatencyMs = (now - end).TotalMilliseconds;
            if (_translationAttr.TextFor(st.Id).Length > 0) _lastFinalLatency = st.FinalLatencyMs;
        }
        _store.Upsert(BuildLine(st));
        PublishDebug(st);
    }

    private void FinalizeAll()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var st in _segments.Values)
            if (!st.IsFinal) Finalize(st, now);
    }

    private void PruneOldSegments()
    {
        // Keep only recent working state; finished lines live on in the TranscriptStore.
        if (_segments.Count < 400) return;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var id in _segments.Where(kv => kv.Value.IsFinal && kv.Value.WallStart < cutoff).Select(kv => kv.Key).ToList())
            _segments.Remove(id);
    }

    // ---------------------------------------------------------------- output

    private TranscriptLine BuildLine(SegmentState st)
    {
        var translation = CleanStart(_text.ProcessTranslation(_translationAttr.TextFor(st.Id)));
        var original = _options.ShowOriginal ? CleanStart(_text.ProcessOriginal(_originalAttr.TextFor(st.Id))) : "";
        bool sameLanguage = IsSameLanguage(st, translation, original);
        if (sameLanguage) translation = "";

        // Provisional speaker from the early check until the full segment has been assigned.
        bool uncertain = st.Match?.Uncertain ?? true;
        string provisionalLabel = _speakers.Enabled && st.Match == null ? "…" : "";

        var line = new TranscriptLine
        {
            SegmentId = st.Id,
            StartedAt = st.WallStart,
            StreamStartMs = st.StreamStartMs,
            StreamEndMs = st.StreamEndMs ?? st.StreamStartMs,
            SpeakerId = st.Match?.SpeakerId,
            SpeakerLabel = provisionalLabel,
            SpeakerColor = "#FFFFFF",
            SpeakerConfidence = st.Match?.Similarity ?? 0,
            SpeakerUncertain = uncertain,
            Translation = translation,
            Original = original,
            IsFinal = st.IsFinal,
            SameLanguage = sameLanguage,
            Hidden = sameLanguage && _options.SameLanguage == SameLanguageMode.Hide,
        };
        return TranscriptRelabeler.Relabel(line, _speakers, _options.SameLanguage, st.Decision == SegmentDecision.Suppressed);
    }

    /// <summary>
    /// Speech already in the subtitle language. The model's output for it is unreliable (the live test saw nothing for
    /// 47 s, then paraphrases), so English speech is recognised from the original transcript and shown word for word.
    /// An empty translation alone is not evidence: it also happens when a line's text bled into the next line.
    /// </summary>
    private bool IsSameLanguage(SegmentState st, string translation, string original)
    {
        if (original.Length == 0) return false;
        if (_options.OutputLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return LanguageGuess.LooksEnglish(original) && (st.IsFinal || original.Count(char.IsWhiteSpace) >= 3);
        return st.IsFinal && (translation.Length == 0 || Normalize(translation) == Normalize(original))
               && original.Count(char.IsWhiteSpace) >= 2 && (st.StreamEndMs ?? 0) - st.StreamStartMs >= 1500;
    }

    /// <summary>Drops stray punctuation left at the start of a line ("? Okay" → "Okay").</summary>
    private static string CleanStart(string text) => text.TrimStart(' ', '.', ',', '?', '!', ';', ':', '…').Trim();

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void PublishDebug(SegmentState st)
    {
        if (SegmentDebug == null) return;
        string status = st.Decision switch
        {
            SegmentDecision.Pending => "checking speaker",
            SegmentDecision.Suppressed => "muted (not sent)",
            _ when st.IsFinal => _translationAttr.TextFor(st.Id).Length > 0 ? "final" : "final (no translation)",
            _ when !st.EndedBySegmenter => "speaking",
            _ => "waiting for text",
        };
        var line = _store.Get(st.Id);
        SegmentDebug(new SegmentDebugInfo
        {
            SegmentId = st.Id,
            StreamStartMs = st.StreamStartMs,
            StreamEndMs = st.StreamEndMs ?? _segmenter.StreamMs,
            Status = status,
            SpeakerLabel = line?.SpeakerLabel ?? "",
            Similarity = st.Match?.Similarity ?? 0,
            SecondBest = st.Match?.SecondBest ?? 0,
            Uncertain = st.Match?.Uncertain ?? true,
            ModelStartMs = st.Timeline.ModelStartMs,
            ModelEndMs = st.Timeline.ModelSpeechEndMs,
            FirstTextLatencyMs = st.FirstTextLatencyMs,
            FinalLatencyMs = st.FinalLatencyMs,
            TextLagMs = st.LastLagMs,
            RawTranslation = _translationAttr.TextFor(st.Id),
            RawOriginal = _originalAttr.TextFor(st.Id),
        });
    }

    private void PublishStatus(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastStatus).TotalMilliseconds < 500) return;
        _lastStatus = now;
        StatusChanged?.Invoke(new PipelineStatus
        {
            Connection = _connection.State,
            ConnectionMessage = _connection.StatusMessage,
            Paused = _paused,
            Speaking = _segmenter.InSegment,
            Sending = _segmenter.Sending,
            AudioSentSeconds = _connection.ModelMs / 1000.0,
            AvgTextLagMs = _recentLags.Count > 0 ? _recentLags.Average() : null,
            LastFinalLatencyMs = _lastFinalLatency,
            Segments = _segments.Count,
            Reconnects = _connection.ReconnectCount,
            VadName = _vad.Name,
        });
    }

    private sealed class SegmentState(int id)
    {
        public int Id { get; } = id;
        public TimelineSegment Timeline { get; } = new(id);
        public double StreamStartMs { get; init; }
        public double? StreamEndMs { get; set; }
        public DateTimeOffset WallStart { get; init; }
        public DateTimeOffset? WallEnd { get; set; }
        public SegmentDecision Decision { get; set; }
        public bool EndedBySegmenter { get; set; }
        public int QueuedFrames { get; set; }
        public int InFlightFrames { get; set; }
        public bool IsFinal { get; set; }
        public SpeakerMatch? Match { get; set; }
        public int? SpeakerId { get; set; }
        public double? FirstTextLatencyMs { get; set; }
        public double? FinalLatencyMs { get; set; }
        public double? LastLagMs { get; set; }
    }
}
