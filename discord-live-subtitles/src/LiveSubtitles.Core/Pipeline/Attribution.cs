using System.Text;

namespace LiveSubtitles.Core.Pipeline;

public sealed record AttributionOptions
{
    /// <summary>Text can't refer to speech the model hasn't heard yet: text at model time t belongs to segments that started before t - MinLagMs.</summary>
    public int MinLagMs { get; init; } = 300;
    /// <summary>Text arriving this long after a segment's speech ended (in model time) goes to the next segment.</summary>
    public int CloseLagMs { get; init; } = 1600;
    /// <summary>A segment is final once no new text has arrived for this long after its speech ended.</summary>
    public int FinalizeIdleMs { get; init; } = 1800;
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
    private int _cursor; // index into the sent-segment list

    public StreamAttributor(AttributionOptions options, Func<IReadOnlyList<TimelineSegment>> sentSegments)
    {
        _o = options;
        _segments = sentSegments;
    }

    public string TextFor(int segmentId) => _text.TryGetValue(segmentId, out var sb) ? sb.ToString() : "";
    public DateTimeOffset? LastDeltaFor(int segmentId) => _lastDelta.TryGetValue(segmentId, out var t) ? t : null;

    /// <summary>Index of the segment currently receiving text; segments before it are closed for this stream.</summary>
    public int Cursor => _cursor;

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
            bool pastClose = modelMs >= curEnd + _o.CloseLagMs;
            bool sentenceDone = modelMs >= curEnd && EndsSentence(TextFor(cur.Id));
            bool nothingYet = TextFor(cur.Id).Length == 0 && modelMs >= curEnd + _o.MinLagMs;
            if (!(pastClose || sentenceDone || nothingYet)) break;
            _cursor++;
        }

        var target = segs[_cursor];
        if (!_text.TryGetValue(target.Id, out var sb)) _text[target.Id] = sb = new StringBuilder();
        sb.Append(delta);
        _lastDelta[target.Id] = now;
        return target.Id;
    }

    /// <summary>True if this stream will not add more text to the segment.</summary>
    public bool IsDone(TimelineSegment seg, int index, DateTimeOffset now)
    {
        if (index < _cursor) return true;
        if (!seg.SpeechEnded || seg.EndSentWall is not { } endWall) return false;
        var last = _lastDelta.TryGetValue(seg.Id, out var t) && t > endWall ? t : endWall;
        return (now - last).TotalMilliseconds >= _o.FinalizeIdleMs;
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
