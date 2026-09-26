namespace LiveSubtitles.Core.Translation;

/// <summary>
/// Converts Soniox token messages into the same transcript events the OpenAI session produces, so the rest of the
/// pipeline does not care which engine runs.
/// Only final tokens are used (the pipeline's text is append-only). Original tokens carry their own audio time.
/// Translated tokens have no timing, but Soniox sends them in strict order: an original chunk, then its translation,
/// then the next original chunk. Each translated token is therefore given the time of the original word at the same
/// relative position in the chunk it translates, so a translation that spans two lines is split between them.
/// </summary>
public sealed class SonioxTokenMapper
{
    private readonly List<ChunkToken> _chunk = new();
    private readonly Dictionary<string, double> _ratio = new(); // translated chars per original char, by source language
    private bool _inTranslation;
    private int _translatedChars;
    private string? _chunkLanguage;
    private double _lastOriginalEnd;

    private readonly record struct ChunkToken(double Start, double End, int Chars, string? Speaker, string? Language);

    public IEnumerable<TranslationServerEvent> Map(SonioxMessage message, DateTimeOffset receivedAt)
    {
        foreach (var t in message.Tokens)
        {
            if (!t.IsFinal || t.Text.Length == 0 || t.Text is SonioxProtocol.EndToken or SonioxProtocol.FinalizeToken) continue;
            if (!t.IsTranslation)
            {
                if (_inTranslation) FinishChunk();
                double start = t.StartMs ?? _lastOriginalEnd;
                double end = Math.Max(start, t.EndMs ?? start);
                _chunk.Add(new ChunkToken(start, end, t.Text.Length, t.Speaker, t.Language));
                _lastOriginalEnd = end;
                yield return new TranslationServerEvent
                {
                    Type = "session.input_transcript.delta", Delta = t.Text, ElapsedMs = start, Timed = true, ReceivedAt = receivedAt,
                };
            }
            else
            {
                bool newChunk = !_inTranslation;
                if (newChunk) StartTranslation(t.Speaker);
                double at = TimeForTranslatedChar(_translatedChars);
                _translatedChars += t.Text.Length;
                yield return new TranslationServerEvent
                {
                    Type = "session.output_transcript.delta", Delta = t.Text, ElapsedMs = at, Timed = true, NewChunk = newChunk, ReceivedAt = receivedAt,
                };
            }
        }
    }

    private void StartTranslation(string? speaker)
    {
        _inTranslation = true;
        _translatedChars = 0;
        // The translation belongs to the trailing run of the same speaker's words.
        if (speaker != null)
        {
            int i = _chunk.Count;
            while (i > 0 && _chunk[i - 1].Speaker == speaker) i--;
            if (i < _chunk.Count && i > 0) _chunk.RemoveRange(0, i);
        }
        _chunkLanguage = _chunk.Select(c => c.Language).FirstOrDefault(l => l != null);
    }

    private void FinishChunk()
    {
        int originalChars = _chunk.Sum(c => c.Chars);
        if (originalChars >= 8 && _translatedChars > 0)
        {
            var key = _chunkLanguage ?? "";
            double observed = Math.Clamp((double)_translatedChars / originalChars, 0.3, 6);
            _ratio[key] = _ratio.TryGetValue(key, out var r) ? 0.7 * r + 0.3 * observed : observed;
        }
        _chunk.Clear();
        _inTranslation = false;
    }

    /// <summary>Audio time of the original word at the same relative position as translated character <paramref name="c"/>.</summary>
    private double TimeForTranslatedChar(int c)
    {
        if (_chunk.Count == 0) return _lastOriginalEnd;
        int originalChars = _chunk.Sum(x => x.Chars);
        double expected = originalChars * RatioFor(_chunkLanguage);
        double target = Math.Min(1.0, c / Math.Max(1.0, expected)) * originalChars;
        int seen = 0;
        foreach (var tok in _chunk)
        {
            seen += tok.Chars;
            if (seen > target) return tok.End;
        }
        return _chunk[^1].End;
    }

    private double RatioFor(string? language) =>
        _ratio.TryGetValue(language ?? "", out var r) ? r : language switch
        {
            // Rough English-characters-per-source-character starting points; refined from each finished chunk.
            "zh" or "ja" => 3.0,
            "ko" => 2.2,
            _ => 1.1,
        };
}
