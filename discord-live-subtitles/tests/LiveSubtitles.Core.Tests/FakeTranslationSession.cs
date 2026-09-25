using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

/// <summary>
/// Simulates gpt-realtime-translate: for every 200 ms chunk that contains sound it "hears" a token and emits it
/// LagMs later (in model time) as "c{chunkIndex} ", ending sentences with "." after a pause.
/// Tokens carry the source chunk so tests can check the text landed on the right segment.
/// </summary>
internal sealed class FakeTranslationSession : ITranslationSession
{
    private readonly int _lagMs;
    private readonly List<(double DueMs, string Text)> _pending = new();
    private double _elapsed;
    private int _chunk;
    private int _silentRun;
    private bool _open;

    public FakeTranslationSession(int lagMs = 1000) => _lagMs = lagMs;

    public static readonly List<FakeTranslationSession> Created = new();
    public int ChunksReceived => _chunk;
    public bool FailNextConnect { get; set; }

    public event Action<TranslationServerEvent>? EventReceived;
    public event Action<TranslationSessionEnd>? Ended;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (FailNextConnect) throw new IOException("simulated network failure");
        _open = true;
        EventReceived?.Invoke(new TranslationServerEvent { Type = "session.created", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        return Task.CompletedTask;
    }

    public void SendAudio(byte[] pcm16)
    {
        if (!_open) return;
        var samples = Pcm16.ToFloat(pcm16);
        double rms = Math.Sqrt(samples.Select(s => (double)s * s).Average());
        bool sound = rms > 0.01;
        if (sound)
        {
            _pending.Add((_elapsed + _lagMs, $"c{_chunk} "));
            _silentRun = 0;
        }
        else if (++_silentRun == 2 && _pending.Count > 0)
        {
            var last = _pending[^1];
            _pending[^1] = (last.DueMs, last.Text.TrimEnd() + ". ");
        }
        _chunk++;
        _elapsed += TranslationProtocol.ChunkMs;
        foreach (var p in _pending.Where(p => p.DueMs <= _elapsed).ToList())
        {
            _pending.Remove(p);
            EventReceived?.Invoke(new TranslationServerEvent { Type = "session.output_transcript.delta", Delta = p.Text, ElapsedMs = _elapsed, ReceivedAt = DateTimeOffset.UtcNow });
        }
    }

    public void Drop() { _open = false; Ended?.Invoke(new TranslationSessionEnd("simulated drop", null, false)); }

    public Task CloseAsync(TimeSpan timeout)
    {
        foreach (var p in _pending)
            EventReceived?.Invoke(new TranslationServerEvent { Type = "session.output_transcript.delta", Delta = p.Text, ElapsedMs = _elapsed, ReceivedAt = DateTimeOffset.UtcNow });
        _pending.Clear();
        _open = false;
        Ended?.Invoke(new TranslationSessionEnd("closed", null, false));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
