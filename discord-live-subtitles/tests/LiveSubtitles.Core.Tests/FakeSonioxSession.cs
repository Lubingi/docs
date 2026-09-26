using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Tests;

/// <summary>
/// Simulates Soniox's token stream: every 200 ms chunk with sound becomes an original token "o{chunk} " stamped with
/// its audio time, final <see cref="LagMs"/> later. After a pause the chunk of originals ends with "." and is followed
/// by its translation, one token "t{chunk} " per original (without timing, like the real service). Messages go
/// through the real <see cref="SonioxTokenMapper"/>.
/// </summary>
internal sealed class FakeSonioxSession : ITranslationSession
{
    private readonly SonioxTokenMapper _mapper = new();
    private readonly List<(double DueMs, SonioxToken Token)> _pending = new();
    private readonly List<int> _openChunk = new();
    private double _elapsed;
    private int _chunk;
    private int _silentRun;
    private bool _open;

    public int LagMs { get; init; } = 600;
    /// <summary>Chunks spoken by this "speaker" come back with translation_status "none" (already in the target language).</summary>
    public Func<int, bool> SameLanguage { get; init; } = _ => false;
    /// <summary>End each translated chunk with "." like real translations (false: test the chunk-boundary signal alone).</summary>
    public bool PunctuateTranslation { get; init; } = true;

    public event Action<TranslationServerEvent>? EventReceived;
    public event Action<TranslationSessionEnd>? Ended;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _open = true;
        EventReceived?.Invoke(new TranslationServerEvent { Type = "session.created" });
        return Task.CompletedTask;
    }

    public void SendAudio(byte[] pcm16)
    {
        if (!_open) return;
        var samples = Pcm16.ToFloat(pcm16);
        double rms = Math.Sqrt(samples.Select(s => (double)s * s).Average());
        if (rms > 0.01)
        {
            _pending.Add((_elapsed + LagMs, new SonioxToken { Text = $"o{_chunk} ", StartMs = _elapsed, EndMs = _elapsed + 200, IsFinal = true }));
            _openChunk.Add(_chunk);
            _silentRun = 0;
        }
        else if (++_silentRun == 2) CloseChunk();
        _chunk++;
        _elapsed += TranslationProtocol.ChunkMs;
        Deliver();
    }

    private void CloseChunk()
    {
        if (_openChunk.Count == 0) return;
        double due = _elapsed + LagMs;
        double lastEnd = (_openChunk[^1] + 1) * 200.0;
        _pending.Add((due, new SonioxToken { Text = ".", StartMs = lastEnd, EndMs = lastEnd, IsFinal = true }));
        if (!SameLanguage(_openChunk[0]))
            foreach (var c in _openChunk)
                _pending.Add((due + 200, new SonioxToken
                {
                    Text = PunctuateTranslation && c == _openChunk[^1] ? $"t{c}. " : $"t{c} ", IsFinal = true, IsTranslation = true,
                }));
        _openChunk.Clear();
    }

    private void Deliver(bool all = false)
    {
        var message = new SonioxMessage();
        foreach (var p in _pending.Where(p => all || p.DueMs <= _elapsed).ToList())
        {
            _pending.Remove(p);
            message.Tokens.Add(p.Token);
        }
        if (message.Tokens.Count == 0) return;
        foreach (var ev in _mapper.Map(message, DateTimeOffset.UtcNow)) EventReceived?.Invoke(ev);
    }

    public Task CloseAsync(TimeSpan timeout)
    {
        CloseChunk();
        Deliver(all: true);
        _open = false;
        Ended?.Invoke(new TranslationSessionEnd("closed", null, false));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
