using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;

namespace LiveSubtitles.Core.Translation;

public enum ConnectionState { Stopped, Connecting, Connected, Reconnecting, Failed }

public enum TranscriptStream { Translation, Original }

/// <summary>Identifies which speech segment an audio frame belongs to (null = gap/tail silence).</summary>
public readonly record struct FrameTag(int? SegmentId, bool IsSpeech);

public sealed record TranscriptDelta(TranscriptStream Stream, string Text, double ModelMs, bool HadElapsed, DateTimeOffset ReceivedAt);

public sealed record ReconnectPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Speech kept while disconnected and replayed after reconnecting.</summary>
    public TimeSpan MaxBacklog { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Start a fresh session this long before the server-side expiry, during a quiet moment.</summary>
    public TimeSpan RotateBeforeExpiry { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Never rotate a session younger than this (guards against a very short server-side expiry).</summary>
    public TimeSpan MinSessionAge { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan DelayFor(int attempt)
    {
        double ms = InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 10));
        ms = Math.Min(ms, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms * (0.8 + Random.Shared.NextDouble() * 0.4));
    }
}

/// <summary>
/// Owns the translation WebSocket: connects, reconnects with exponential backoff, keeps a short audio backlog
/// while offline, rotates sessions before they expire, and maps server <c>elapsed_ms</c> values onto one
/// continuous "model timeline" across sessions so transcript text can be matched to speech segments.
/// Audio is appended from a single producer thread (the pipeline).
/// </summary>
public sealed class TranslationConnection : IAsyncDisposable
{
    private readonly Func<ITranslationSession> _factory;
    private readonly ReconnectPolicy _policy;
    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly Queue<(float[] Audio, FrameTag Tag)> _backlog = new();
    private double _backlogMs;
    private readonly List<float> _chunk = new(TranslationProtocol.ChunkSamples * 2);
    private ActiveSession? _current;
    private double _globalModelMs;
    private DateTimeOffset _lastSpeechSent = DateTimeOffset.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _rotating;

    public TranslationConnection(Func<ITranslationSession> factory, ReconnectPolicy? policy = null, ILog? log = null)
    {
        _factory = factory;
        _policy = policy ?? new ReconnectPolicy();
        _log = log ?? NullLog.Instance;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Stopped;
    public string StatusMessage { get; private set; } = "Stopped";
    public double ModelMs { get { lock (_lock) return _globalModelMs; } }
    public int ReconnectCount { get; private set; }

    public event Action<ConnectionState, string>? StateChanged;
    /// <summary>A frame was handed to a live session; ModelStartMs is its position on the model timeline.</summary>
    public event Action<FrameTag, double, double, DateTimeOffset>? FrameSent;
    public event Action<TranscriptDelta>? DeltaReceived;
    public event Action<string>? ServerError;
    public event Action<double>? AudioSentMs;
    public event Action<int>? OutputAudioBytes;

    public void Start()
    {
        if (_runTask != null) return;
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Queues a 24 kHz frame for sending. Called from the pipeline thread only.</summary>
    public void Append(float[] audio24, FrameTag tag)
    {
        lock (_lock)
        {
            if (_current == null)
            {
                _backlog.Enqueue((audio24, tag));
                _backlogMs += audio24.Length * 1000.0 / TranslationProtocol.SampleRate;
                while (_backlogMs > _policy.MaxBacklog.TotalMilliseconds && _backlog.Count > 0)
                {
                    var dropped = _backlog.Dequeue();
                    _backlogMs -= dropped.Audio.Length * 1000.0 / TranslationProtocol.SampleRate;
                }
                return;
            }
            AppendToSessionLocked(audio24, tag);
        }
    }

    /// <summary>Pads the pending chunk with silence to a whole 200 ms frame and sends it, so the server
    /// processes the end of speech instead of waiting for more audio.</summary>
    public void FlushPadding()
    {
        lock (_lock)
        {
            if (_current == null || _chunk.Count == 0) return;
            int pad = TranslationProtocol.ChunkSamples - _chunk.Count;
            if (pad > 0) AppendToSessionLocked(new float[pad], new FrameTag(null, false));
        }
    }

    private void AppendToSessionLocked(float[] audio24, FrameTag tag)
    {
        double durationMs = audio24.Length * 1000.0 / TranslationProtocol.SampleRate;
        var now = DateTimeOffset.UtcNow;
        FrameSent?.Invoke(tag, _globalModelMs, durationMs, now);
        _globalModelMs += durationMs;
        _current!.SentMs += durationMs;
        if (tag.IsSpeech) _lastSpeechSent = now;
        _chunk.AddRange(audio24);
        while (_chunk.Count >= TranslationProtocol.ChunkSamples)
        {
            var bytes = Pcm16.FromFloat(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_chunk)[..TranslationProtocol.ChunkSamples]);
            _chunk.RemoveRange(0, TranslationProtocol.ChunkSamples);
            _current.Session.SendAudio(bytes);
            AudioSentMs?.Invoke(TranslationProtocol.ChunkMs);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(ReconnectCount == 0 && attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting,
                attempt == 0 ? "Connecting to OpenAI…" : $"Reconnecting (attempt {attempt + 1})…");
            var session = _factory();
            var pending = new ActiveSession(session); // subscribes to Ended before connecting
            try
            {
                await session.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (TranslationConnectException ex) when (ex.IsFatal)
            {
                _log.Error("Translation connect failed (not retrying)", ex);
                await session.DisposeAsync().ConfigureAwait(false);
                SetState(ConnectionState.Failed, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("Translation connect failed", ex);
                await session.DisposeAsync().ConfigureAwait(false);
                var delay = _policy.DelayFor(attempt++);
                SetState(ConnectionState.Reconnecting, $"{Short(ex)} — retrying in {delay.TotalSeconds:0}s");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            ActiveSession active;
            lock (_lock)
            {
                active = Install(pending);
                // Replay speech captured while offline.
                while (_backlog.Count > 0)
                {
                    var (audio, tag) = _backlog.Dequeue();
                    AppendToSessionLocked(audio, tag);
                }
                _backlogMs = 0;
            }
            if (attempt > 0 || ReconnectCount > 0) _log.Info("Translation reconnected");
            attempt = 0;
            SetState(ConnectionState.Connected, "Connected");

            // Supervise whichever session is current, rotating it shortly before it expires.
            while (!ct.IsCancellationRequested && !active.EndedTask.IsCompleted)
            {
                await Task.WhenAny(active.EndedTask, Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
                if (!active.EndedTask.IsCompleted && ShouldRotate(active))
                {
                    var next = await RotateAsync(active, ct).ConfigureAwait(false);
                    if (next != null) active = next;
                }
            }

            lock (_lock)
            {
                if (_current == active) { _current = null; _chunk.Clear(); }
            }
            if (ct.IsCancellationRequested) break;
            var end = active.End;
            await active.Session.DisposeAsync().ConfigureAwait(false);
            if (end?.Fatal == true)
            {
                SetState(ConnectionState.Failed, end.Reason);
                return;
            }
            ReconnectCount++;
            var wait = _policy.DelayFor(attempt++);
            SetState(ConnectionState.Reconnecting, $"Connection dropped ({end?.Reason ?? "unknown"}) — reconnecting in {wait.TotalSeconds:0}s");
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        SetState(ConnectionState.Stopped, "Stopped");
    }

    private ActiveSession Install(ActiveSession active)
    {
        active.Offset = _globalModelMs;
        active.InstalledAt = DateTimeOffset.UtcNow;
        active.Session.EventReceived += ev => OnServerEvent(active, ev);
        _current = active;
        _chunk.Clear();
        return active;
    }

    private bool ShouldRotate(ActiveSession active)
    {
        if (_rotating || active.ExpiresAt is not { } expires) return false;
        var now = DateTimeOffset.UtcNow;
        if (now - active.InstalledAt < _policy.MinSessionAge) return false;
        if (now < expires - _policy.RotateBeforeExpiry) return false;
        bool quiet = now - _lastSpeechSent > TimeSpan.FromSeconds(2);
        return quiet || now > expires - TimeSpan.FromSeconds(20);
    }

    private async Task<ActiveSession?> RotateAsync(ActiveSession old, CancellationToken ct)
    {
        _rotating = true;
        try
        {
            _log.Info("Rotating translation session before expiry");
            var session = _factory();
            var next = new ActiveSession(session);
            await session.ConnectAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                FlushPaddingLocked();
                Install(next);
            }
            // Let the old session flush its last words in the background.
            _ = Task.Run(async () =>
            {
                await old.Session.CloseAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await old.Session.DisposeAsync().ConfigureAwait(false);
            });
            return next;
        }
        catch (Exception ex)
        {
            _log.Warn("Session rotation failed; keeping the current session", ex);
            return null;
        }
        finally
        {
            _rotating = false;
        }
    }

    private void FlushPaddingLocked()
    {
        if (_current == null || _chunk.Count == 0) return;
        int pad = TranslationProtocol.ChunkSamples - _chunk.Count;
        if (pad > 0) AppendToSessionLocked(new float[pad], new FrameTag(null, false));
    }

    private void OnServerEvent(ActiveSession session, TranslationServerEvent ev)
    {
        switch (ev.Type)
        {
            case "session.created":
            case "session.updated":
                if (ev.ExpiresAt != null) session.ExpiresAt = ev.ExpiresAt;
                break;
            case "session.output_transcript.delta":
            case "session.input_transcript.delta":
            {
                if (string.IsNullOrEmpty(ev.Delta)) return;
                double model;
                lock (_lock) model = session.Offset + (ev.ElapsedMs ?? session.SentMs);
                var stream = ev.Type == "session.output_transcript.delta" ? TranscriptStream.Translation : TranscriptStream.Original;
                DeltaReceived?.Invoke(new TranscriptDelta(stream, ev.Delta, model, ev.ElapsedMs != null, ev.ReceivedAt));
                break;
            }
            case "session.output_audio.delta":
                OutputAudioBytes?.Invoke(ev.AudioBytes);
                break;
            case "error":
                ServerError?.Invoke($"{ev.ErrorCode ?? ev.ErrorType}: {ev.ErrorMessage}");
                break;
        }
    }

    private void SetState(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(state, message);
    }

    private static string Short(Exception ex) => ex is TranslationConnectException ? ex.Message : ex.GetBaseException().Message;

    public async Task StopAsync()
    {
        ActiveSession? current;
        lock (_lock)
        {
            FlushPaddingLocked();
            current = _current;
            _current = null;
        }
        if (current != null)
        {
            await current.Session.CloseAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        _cts?.Cancel();
        if (_runTask != null)
        {
            try { await _runTask.ConfigureAwait(false); } catch { }
        }
        if (current != null) await current.Session.DisposeAsync().ConfigureAwait(false);
        _runTask = null;
        SetState(ConnectionState.Stopped, "Stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class ActiveSession
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveSession(ITranslationSession session)
        {
            Session = session;
            session.Ended += e => { End = e; _ended.TrySetResult(); };
        }

        public TranslationSessionEnd? End { get; private set; }

        public ITranslationSession Session { get; }
        public double Offset { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
        public double SentMs { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public Task EndedTask => _ended.Task;
    }
}
