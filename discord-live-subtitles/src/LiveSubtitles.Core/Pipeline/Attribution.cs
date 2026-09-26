using System.Text;

namespace LiveSubtitles.Core.Pipeline;

public sealed record AttributionOptions
{
    /// <summary>Text can't refer to speech the model hasn't heard yet: text at model time t belongs to segments that started before t - MinLagMs.</summary>
    public int MinLagMs { get; init; } = 300;
    /// <summary>Text arriving this long after a segment's speech ended (in model time) always goes to the next segment.
    /// Measured: sentence ends arrive a median 1.05 s (up to 2.7 s) after the speech ends.</summary>
    public int CloseLagMs { get; init; } = 2500;
    /// <summary>After a finished sentence, move on only if the line has as many sentences as its original-language
    /// transcript (which aligns better), or once this long has passed since the speech ended.</summary>
    public int SentenceCloseLagMs { get; init; } = 1200;
    /// <summary>A line with no text at all is skipped this long after its speech ended (null = never early).
    /// Used for the original-language stream, which lags only 0.2–0.8 s.</summary>
    public int? EmptySkipLagMs { get; init; }
    /// <summary>A segment is final once no new text has arrived for this long after its speech ended.</summary>
    public int FinalizeIdleMs { get; init; } = 2200;
}

/// <summary>Where a speech segment sits on the translation model's audio timeline.</summary>
public sealed class TimelineSegment
{
    public TimelineSegment(int id) => Id = id;
    public int Id { get; }
    public double? ModelStartMs { get; set; }
    public double? ModelSpeechEndMs { get; set; }
    /// <summary>Segmenter has ended the segment and all its audio has been sent.</summary>
    public bool SpeechEnded { get; set; }
    public DateTimeOffset? EndSentWall { get; set; }
    public DateTimeOffset? FirstSentWall { get; set; }
}

/// <summary>
/// Assigns an append-only transcript stream (translation or original) to speech segments.
/// The Realtime Translation API streams text deltas stamped with <c>elapsed_ms</c> on the input-audio timeline but
/// has no per-utterance boundaries, so we assign each delta to the earliest still-open segment and move on to the
/// next segment once the model time is clearly past the current segment (or its sentence has ended).
/// </summary>
public sealed class StreamAttributor
{
    private readonly AttributionOptions _o;
    private readonly Func<IReadOnlyList<TimelineSegment>> _segments;
    private readonly Dictionary<int, StringBuilder> _text = new();
    private readonly Dictionary<int, DateTimeOffset> _lastDelta = new();
    private readonly StreamAttributor? _anchor;
    private int _cursor; // index into the sent-segment list

    /// <param name="anchor">Optional better-aligned stream (the original-language transcript) whose sentence count per
    /// segment tells this stream when a line is complete.</param>
    public StreamAttributor(AttributionOptions options, Func<IReadOnlyList<TimelineSegment>> sentSegments, StreamAttributor? anchor = null)
    {
        _o = options;
        _segments = sentSegments;
        _anchor = anchor;
    }

    /// <summary>Creates the original-language attributor and the translation attributor anchored to it.</summary>
    public static (StreamAttributor Translation, StreamAttributor Original) CreatePair(
        AttributionOptions options, Func<IReadOnlyList<TimelineSegment>> sentSegments, bool withOriginal)
    {
        var original = new StreamAttributor(options with { EmptySkipLagMs = options.EmptySkipLagMs ?? 1000 }, sentSegments);
        var translation = new StreamAttributor(options, sentSegments, withOriginal ? original : null);
        return (translation, original);
    }

    public string TextFor(int segmentId) => _text.TryGetValue(segmentId, out var sb) ? sb.ToString() : "";
    public DateTimeOffset? LastDeltaFor(int segmentId) => _lastDelta.TryGetValue(segmentId, out var t) ? t : null;

    /// <summary>Index of the segment currently receiving text; segments before it are closed for this stream.</summary>
    public int Cursor => _cursor;

    // ---- joining text across a reconnect
    private bool _joining;
    private readonly System.Text.StringBuilder _joinBuffer = new();
    private int _joinTarget = -1;
    private DateTimeOffset _joinStarted;

    /// <summary>
    /// Call when text starts coming from a new session (after a reconnect). The replayed audio makes the model repeat
    /// words that were already shown, and the new session's first delta has no leading space. The first few words are
    /// held back, de-duplicated against the end of the existing line and joined with a proper space.
    /// </summary>
    public void BeginJoin(DateTimeOffset now)
    {
        _joining = true;
        _joinStarted = now;
        _joinBuffer.Clear();
        _joinTarget = -1;
    }

    /// <summary>Releases held-back join text once enough has arrived or it has waited long enough (called periodically).</summary>
    public void FlushJoin(DateTimeOffset now, bool force = false)
    {
        if (!_joining || _joinTarget < 0) return;
        if (!force && WordCount(_joinBuffer.ToString()) < 4 && (now - _joinStarted).TotalMilliseconds < 1500) return;
        var target = _joinTarget;
        var text = _joinBuffer.ToString();
        _joining = false;
        _joinBuffer.Clear();
        _joinTarget = -1;
        var segs = _segments();
        int idx = -1;
        for (int i = 0; i < segs.Count; i++) if (segs[i].Id == target) idx = i;
        var existing = TextFor(target);
        if (existing.Trim().Length == 0 && idx > 0) existing = TextFor(segs[idx - 1].Id);
        text = RemoveOverlap(existing, text);
        if (text.Trim().Length == 0) return;
        var current = TextFor(target);
        if (current.Length > 0 && !char.IsWhiteSpace(current[^1]) && !char.IsWhiteSpace(text[0]) && char.IsLetterOrDigit(text[0]))
            text = " " + text;
        Append(target, text, now);
    }

    /// <summary>Drops words at the start of <paramref name="incoming"/> that repeat the end of <paramref name="existing"/>.</summary>
    internal static string RemoveOverlap(string existing, string incoming)
    {
        static string Norm(string w) => new string(w.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var have = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(w => w.Length > 0).ToList();
        var matches = System.Text.RegularExpressions.Regex.Matches(incoming, @"\S+");
        var words = matches.Select(m => Norm(m.Value)).ToList();
        int best = 0;
        for (int k = Math.Min(8, Math.Min(have.Count, words.Count)); k >= 1; k--)
        {
            if (words.Take(k).Where(w => w.Length > 0).SequenceEqual(have.Skip(have.Count - k).Take(k)) && words.Take(k).All(w => w.Length > 0))
            {
                best = k;
                break;
            }
        }
        if (best == 0) return incoming;
        var cut = matches[best - 1];
        int lastLetter = cut.Value.Length - 1;
        while (lastLetter > 0 && !char.IsLetterOrDigit(cut.Value[lastLetter])) lastLetter--;
        return incoming[(cut.Index + lastLetter + 1)..]; // keep punctuation after the repeated word ("first," → ",")
    }

    private static int WordCount(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Returns the segment id the delta was attached to, or null if no segment has been sent yet.</summary>
    public int? Add(string delta, double modelMs, DateTimeOffset now)
    {
        var segs = _segments();
        if (segs.Count == 0) return null;
        if (_cursor >= segs.Count) _cursor = segs.Count - 1;

        while (_cursor + 1 < segs.Count)
        {
            var cur = segs[_cursor];
            var next = segs[_cursor + 1];
            if (next.ModelStartMs is not { } nextStart || modelMs < nextStart + _o.MinLagMs) break;
            if (cur.ModelSpeechEndMs is not { } curEnd || !cur.SpeechEnded) break;
            var curText = TextFor(cur.Id);
            bool pastClose = modelMs >= curEnd + _o.CloseLagMs;
            bool sentenceDone = modelMs >= curEnd && EndsSentence(curText) && SentencesComplete(cur.Id, curText, modelMs - curEnd);
            // Only when the original transcript confirms there were no words (a laugh, a cough) is an empty line skipped early.
            bool empty = curText.Trim().Length == 0;
            bool confirmedEmpty = empty && _anchor != null && _anchor.Cursor > _cursor
                                  && _anchor.TextFor(cur.Id).Trim().Length == 0 && modelMs >= curEnd + _o.MinLagMs;
            if (empty && _o.EmptySkipLagMs is { } skip && modelMs >= curEnd + skip) confirmedEmpty = true;
            if (!(pastClose || sentenceDone || confirmedEmpty)) break;
            _cursor++;
        }

        // A sentence's final punctuation often arrives as its own delta after the next person started talking.
        // If the new line has no text yet, the punctuation belongs to the previous line when that sentence is still open.
        if (_cursor > 0 && TextFor(segs[_cursor].Id).Trim().Length == 0 && LeadingPunctuation(delta) is { Length: > 0 } punct)
        {
            var prev = segs[_cursor - 1];
            var prevText = TextFor(prev.Id);
            if (prevText.Trim().Length > 0 && !EndsSentence(prevText) && prev.ModelSpeechEndMs is { } prevEnd && modelMs <= prevEnd + _o.CloseLagMs + 1500)
            {
                Append(prev.Id, punct, now);
                delta = delta[punct.Length..];
                if (delta.Trim().Length == 0) return prev.Id;
            }
        }

        var target = segs[_cursor];
        if (_joining)
        {
            if (_joinTarget >= 0 && _joinTarget != target.Id) FlushJoin(now, force: true);
            if (_joining)
            {
                _joinTarget = target.Id;
                _joinBuffer.Append(delta);
                FlushJoin(now);
                return target.Id;
            }
        }
        Append(target.Id, delta, now);
        return target.Id;
    }

    private void Append(int segmentId, string text, DateTimeOffset now)
    {
        if (!_text.TryGetValue(segmentId, out var sb)) _text[segmentId] = sb = new StringBuilder();
        sb.Append(text);
        _lastDelta[segmentId] = now;
    }

    /// <summary>True unless the anchor (original transcript) shows this line should have more sentences and there's still time for them.</summary>
    private bool SentencesComplete(int segmentId, string text, double msSinceEnd)
    {
        if (_anchor == null || msSinceEnd >= _o.SentenceCloseLagMs) return true;
        var anchorText = _anchor.TextFor(segmentId);
        if (anchorText.Trim().Length == 0) return true;
        return CountSentences(text) >= CountSentences(anchorText);
    }

    internal static int CountSentences(string text)
    {
        static bool IsEnd(char c) => c is '.' or '!' or '?' or '…' or '。' or '！' or '？';
        int count = 0;
        var t = text.Trim();
        for (int i = 0; i < t.Length; i++)
        {
            if (!IsEnd(t[i]) || (i + 1 < t.Length && IsEnd(t[i + 1]))) continue; // count each run ("?!", "...") once, at its end
            bool atBoundary = i == t.Length - 1 || char.IsWhiteSpace(t[i + 1]) || t[i + 1] is '"' or '\'' or '”' or '’' or ')';
            if (atBoundary) count++;
        }
        return count;
    }

    /// <summary>The run of sentence/clause punctuation a delta starts with (after optional spaces), e.g. "." from ". Okay".</summary>
    internal static string LeadingPunctuation(string delta)
    {
        int i = 0;
        while (i < delta.Length && delta[i] == ' ') i++;
        int start = i;
        while (i < delta.Length && delta[i] is '.' or '?' or '!' or ',' or ';' or ':' or '…' or '"' or '”' or '’') i++;
        return i > start ? delta[..i] : "";
    }

    /// <summary>True if this stream will not add more text to the segment.</summary>
    public bool IsDone(TimelineSegment seg, int index, DateTimeOffset now)
    {
        if (index < _cursor) return true;
        if (!seg.SpeechEnded || seg.EndSentWall is not { } endWall) return false;
        if (_joining && _joinTarget == seg.Id) return false;
        var last = _lastDelta.TryGetValue(seg.Id, out var t) && t > endWall ? t : endWall;
        // A line whose sentence is visibly complete (and has as many sentences as its original) finishes sooner.
        var text = TextFor(seg.Id);
        double idle = EndsSentence(text) && SentencesComplete(seg.Id, text, 0) ? Math.Min(_o.FinalizeIdleMs, 1000) : _o.FinalizeIdleMs;
        return (now - last).TotalMilliseconds >= idle;
    }

    internal static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        if (t.Length == 0) return false;
        char c = t[^1];
        if (c is '"' or '\'' or '”' or '’' or ')' && t.Length > 1) c = t[^2];
        return c is '.' or '!' or '?' or '…' or '。' or '！' or '？';
    }
}
