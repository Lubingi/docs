namespace LiveSubtitles.Core.Transcript;

/// <summary>Thread-safe in-memory transcript for the running session. Nothing is written to disk unless the user exports or enables auto-save.</summary>
public sealed class TranscriptStore
{
    private readonly object _lock = new();
    private readonly List<TranscriptLine> _lines = new();
    private readonly Dictionary<int, int> _index = new();

    public event Action<TranscriptLine>? LineAdded;
    public event Action<TranscriptLine>? LineUpdated;
    public event Action? Cleared;

    public TranscriptLine? Get(int segmentId)
    {
        lock (_lock) return _index.TryGetValue(segmentId, out var i) ? _lines[i] : null;
    }

    public void Upsert(TranscriptLine line)
    {
        bool added;
        lock (_lock)
        {
            if (_index.TryGetValue(line.SegmentId, out var i))
            {
                if (_lines[i] == line) return;
                _lines[i] = line;
                added = false;
            }
            else
            {
                _index[line.SegmentId] = _lines.Count;
                _lines.Add(line);
                added = true;
            }
        }
        if (added) LineAdded?.Invoke(line); else LineUpdated?.Invoke(line);
    }

    public void Update(int segmentId, Func<TranscriptLine, TranscriptLine> change)
    {
        TranscriptLine? updated = null;
        lock (_lock)
        {
            if (!_index.TryGetValue(segmentId, out var i)) return;
            var next = change(_lines[i]);
            if (next == _lines[i]) return;
            _lines[i] = next;
            updated = next;
        }
        LineUpdated?.Invoke(updated);
    }

    public void UpdateWhere(Func<TranscriptLine, bool> predicate, Func<TranscriptLine, TranscriptLine> change)
    {
        var changed = new List<TranscriptLine>();
        lock (_lock)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                if (!predicate(_lines[i])) continue;
                var next = change(_lines[i]);
                if (next == _lines[i]) continue;
                _lines[i] = next;
                changed.Add(next);
            }
        }
        foreach (var l in changed) LineUpdated?.Invoke(l);
    }

    public IReadOnlyList<TranscriptLine> Snapshot()
    {
        lock (_lock) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_lock) { _lines.Clear(); _index.Clear(); }
        Cleared?.Invoke();
    }
}
