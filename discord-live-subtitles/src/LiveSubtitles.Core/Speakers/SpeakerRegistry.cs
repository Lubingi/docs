using LiveSubtitles.Core.Settings;

namespace LiveSubtitles.Core.Speakers;

public sealed record SpeakerInfo(int Id, string Label, string Color, bool IsNamed, bool Muted, int Segments, double SpeechSeconds, DateTimeOffset? LastHeard);

/// <summary>
/// Online speaker diarization, entirely local:
/// every finished speech segment gets a voice embedding (CAM++), which is compared with each known voice's
/// profile (the normalised mean of its embeddings, weighted by speech length).
/// <list type="bullet">
/// <item>Confident match → that speaker; the profile is updated, so it improves as the person talks.</item>
/// <item>Clearly different from everyone (and long enough) → a new "Speaker N" with its own colour.</item>
/// <item>In between → "?" (never guess). Several similar "?" segments together form a new speaker.</item>
/// <item>Profiles that converge on the same voice are merged automatically.</item>
/// </list>
/// Speakers the user renames are remembered across sessions as embeddings only (no audio) in voices.json.
/// Thread-safe.
/// </summary>
public sealed class SpeakerRegistry : ISpeakerIdentifier
{
    public const string UnknownColor = "#BDBDBD";
    private static readonly string[] Palette =
    {
        "#4FC3F7", "#FFB74D", "#81C784", "#F06292", "#BA68C8", "#FFD54F",
        "#4DB6AC", "#E57373", "#9FA8DA", "#AED581", "#FF8A65", "#F48FB1",
    };
    private const int MaxSegmentRecords = 3000;
    private const int MaxPending = 30;
    private const double MaxProfileWeightSeconds = 180;

    private readonly Func<float[], float[]?> _embed;
    private readonly VoiceProfileStore? _store;
    private readonly object _lock = new();
    private readonly List<Speaker> _speakers = new();
    private readonly Dictionary<int, int> _mergedInto = new();
    private readonly Dictionary<int, SegmentRecord> _segments = new();
    private readonly Queue<int> _segmentOrder = new();
    private readonly List<int> _pending = new();
    private int _nextId = 1;
    private int _nextNumber = 1;

    public SpeakerRegistry(Func<float[], float[]?> embed, DiarizationSettings settings, VoiceProfileStore? store = null)
    {
        _embed = embed;
        Settings = settings;
        _store = store;
        if (store != null)
        {
            foreach (var p in store.Load())
            {
                var s = new Speaker(_nextId++, 0, p.Color)
                {
                    Name = p.Name,
                    ProfileKey = p.Key,
                    Muted = p.Muted,
                    Saved = true,
                };
                double w = Math.Clamp(p.WeightSeconds, 1, MaxProfileWeightSeconds);
                for (int i = 0; i < s.Sum.Length && i < p.Centroid.Length; i++) s.Sum[i] = p.Centroid[i] * w;
                s.Weight = w;
                _speakers.Add(s);
            }
        }
    }

    public DiarizationSettings Settings { get; set; }
    public bool Enabled => true;
    public bool AnyMuted { get { lock (_lock) return _speakers.Any(s => s.Muted); } }

    public event Action? SpeakersChanged;

    // ------------------------------------------------------------------ identification

    public SpeakerMatch Peek(float[] audio16k)
    {
        var emb = _embed(audio16k);
        if (emb == null) return SpeakerMatch.None;
        lock (_lock)
        {
            var (best, bestSim, second) = Rank(emb);
            bool confident = best != null && bestSim >= Settings.MatchThreshold && (bestSim - second >= 0.04f || second < Settings.MatchThreshold);
            return new SpeakerMatch(confident ? best!.Id : null, bestSim, second, false, !confident);
        }
    }

    public SpeakerMatch Assign(int segmentId, float[] audio16k)
    {
        double ms = audio16k.Length / 16.0;
        var emb = _embed(audio16k);
        SpeakerMatch result;
        bool structural = false;
        lock (_lock)
        {
            var record = new SegmentRecord(segmentId, emb, ms);
            AddRecord(record);
            if (emb == null)
            {
                return SpeakerMatch.None;
            }
            var (best, bestSim, second) = Rank(emb);
            bool isShort = ms < 1000;
            float matchThreshold = Settings.MatchThreshold + (isShort ? 0.08f : 0f);

            if (best != null && bestSim >= matchThreshold && (bestSim - second >= 0.04f || second < matchThreshold))
            {
                AddToSpeaker(best, record, bestSim);
                structural = TryAutoMerge(best);
                result = new SpeakerMatch(Resolve(best.Id), bestSim, second, false, false);
            }
            else if ((best == null || bestSim < Settings.NewSpeakerThreshold) && ms >= Settings.MinNewSpeakerMs && ActiveCount < Settings.MaxSpeakers)
            {
                var s = CreateSpeaker();
                AddToSpeaker(s, record, 1f);
                AbsorbPending(s);
                structural = true;
                result = new SpeakerMatch(s.Id, bestSim, second, true, false);
            }
            else
            {
                record.Similarity = bestSim;
                record.Uncertain = true;
                _pending.Add(segmentId);
                while (_pending.Count > MaxPending) _pending.RemoveAt(0);
                var formed = TryFormFromPending(record);
                if (formed != null)
                {
                    structural = true;
                    result = new SpeakerMatch(formed.Id, SpeakerEmbedder.Cosine(emb, formed.Centroid), bestSim, true, false);
                }
                else result = new SpeakerMatch(null, bestSim, second, false, true);
            }
        }
        if (structural) SpeakersChanged?.Invoke();
        return result;
    }

    private (Speaker? Best, float BestSim, float Second) Rank(float[] emb)
    {
        Speaker? best = null;
        float bestSim = -1, second = -1;
        foreach (var s in _speakers)
        {
            float sim = SpeakerEmbedder.Cosine(emb, s.Centroid);
            if (sim > bestSim) { second = bestSim; bestSim = sim; best = s; }
            else if (sim > second) second = sim;
        }
        return (best, Math.Max(bestSim, 0), Math.Max(second, 0));
    }

    private int ActiveCount => _speakers.Count;

    private Speaker CreateSpeaker()
    {
        var used = _speakers.Select(s => s.Color).ToHashSet();
        var color = Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[(_nextNumber - 1) % Palette.Length];
        var s = new Speaker(_nextId++, _nextNumber++, color);
        _speakers.Add(s);
        return s;
    }

    private void AddToSpeaker(Speaker s, SegmentRecord r, float similarity)
    {
        r.SpeakerId = s.Id;
        r.Uncertain = false;
        r.Similarity = similarity;
        _pending.Remove(r.SegmentId);
        s.Segments++;
        s.SpeechMs += r.DurationMs;
        s.LastHeard = DateTimeOffset.UtcNow;
        if (r.Embedding == null) return;
        double w = Math.Min(r.DurationMs / 1000.0, 10);
        // Cap the total weight so long-known voices still adapt to today's microphone/connection.
        if (s.Weight + w > MaxProfileWeightSeconds)
        {
            double scale = (MaxProfileWeightSeconds - w) / s.Weight;
            for (int i = 0; i < s.Sum.Length; i++) s.Sum[i] *= scale;
            s.Weight = MaxProfileWeightSeconds - w;
        }
        for (int i = 0; i < s.Sum.Length; i++) s.Sum[i] += r.Embedding[i] * w;
        s.Weight += w;
        r.Contribution = w;
        s.Invalidate();
    }

    private void RemoveFromSpeaker(Speaker s, SegmentRecord r)
    {
        s.Segments = Math.Max(0, s.Segments - 1);
        s.SpeechMs = Math.Max(0, s.SpeechMs - r.DurationMs);
        if (r.Embedding != null && r.Contribution > 0)
        {
            for (int i = 0; i < s.Sum.Length; i++) s.Sum[i] -= r.Embedding[i] * r.Contribution;
            s.Weight = Math.Max(0, s.Weight - r.Contribution);
            s.Invalidate();
        }
        r.Contribution = 0;
    }

    /// <summary>Several similar unmatched segments (≥3, ≥2.5 s total) are probably a new person.</summary>
    private Speaker? TryFormFromPending(SegmentRecord latest)
    {
        if (latest.Embedding == null || ActiveCount >= Settings.MaxSpeakers) return null;
        var group = _pending
            .Select(id => _segments.GetValueOrDefault(id))
            .Where(r => r?.Embedding != null && r.SpeakerId == null)
            .Where(r => r!.SegmentId == latest.SegmentId || SpeakerEmbedder.Cosine(r.Embedding!, latest.Embedding) >= Settings.MatchThreshold)
            .ToList();
        double total = group.Sum(r => r!.DurationMs);
        bool enough = (group.Count >= 3 && total >= 2500)
                      || (group.Count == 2 && total >= 4000 && SpeakerEmbedder.Cosine(group[0]!.Embedding!, group[1]!.Embedding!) >= Settings.MatchThreshold + 0.05f);
        if (!enough) return null;
        // They must not fit an existing speaker better.
        var centroid = SpeakerEmbedder.Normalize(Mean(group.Select(r => r!.Embedding!)));
        if (_speakers.Any(s => SpeakerEmbedder.Cosine(centroid, s.Centroid) >= Settings.MatchThreshold)) return null;
        var speaker = CreateSpeaker();
        foreach (var r in group) AddToSpeaker(speaker, r!, SpeakerEmbedder.Cosine(r!.Embedding!, centroid));
        AbsorbPending(speaker);
        return speaker;
    }

    /// <summary>Earlier "?" segments that clearly belong to a newly created voice are relabelled retroactively.</summary>
    private void AbsorbPending(Speaker speaker)
    {
        foreach (var id in _pending.ToList())
        {
            if (!_segments.TryGetValue(id, out var r) || r.Embedding == null || r.SpeakerId != null) continue;
            float sim = SpeakerEmbedder.Cosine(r.Embedding, speaker.Centroid);
            if (sim < Settings.MatchThreshold) continue;
            if (_speakers.Any(o => o != speaker && SpeakerEmbedder.Cosine(r.Embedding, o.Centroid) >= sim)) continue;
            AddToSpeaker(speaker, r, sim);
        }
    }

    private bool TryAutoMerge(Speaker updated)
    {
        if (updated.Segments < 2) return false;
        foreach (var other in _speakers.ToList())
        {
            if (other == updated || other.Segments < 2 && !other.Saved) continue;
            if (updated.IsNamed && other.IsNamed) continue; // two names the user chose are never merged automatically
            if (SpeakerEmbedder.Cosine(updated.Centroid, other.Centroid) < Settings.MergeThreshold) continue;
            var (from, into) = PickMergeDirection(updated, other);
            MergeLocked(from, into);
            return true;
        }
        return false;
    }

    private static (Speaker From, Speaker Into) PickMergeDirection(Speaker a, Speaker b)
    {
        if (a.IsNamed != b.IsNamed) return a.IsNamed ? (b, a) : (a, b);
        if (a.Saved != b.Saved) return a.Saved ? (b, a) : (a, b);
        return a.Weight >= b.Weight ? (b, a) : (a, b);
    }

    private void MergeLocked(Speaker from, Speaker into)
    {
        for (int i = 0; i < into.Sum.Length; i++) into.Sum[i] += from.Sum[i];
        into.Weight += from.Weight;
        into.Segments += from.Segments;
        into.SpeechMs += from.SpeechMs;
        into.Muted |= from.Muted && !into.IsNamed;
        if (into.LastHeard == null || from.LastHeard > into.LastHeard) into.LastHeard = from.LastHeard;
        into.Invalidate();
        foreach (var r in _segments.Values.Where(r => r.SpeakerId == from.Id)) r.SpeakerId = into.Id;
        _mergedInto[from.Id] = into.Id;
        _speakers.Remove(from);
        if (from.Saved && from.ProfileKey != null) _store?.Delete(from.ProfileKey);
        if (into.Saved) PersistLocked(into);
    }

    private static float[] Mean(IEnumerable<float[]> vectors)
    {
        float[]? sum = null;
        int n = 0;
        foreach (var v in vectors)
        {
            sum ??= new float[v.Length];
            for (int i = 0; i < v.Length; i++) sum[i] += v[i];
            n++;
        }
        return sum ?? Array.Empty<float>();
    }

    private void AddRecord(SegmentRecord r)
    {
        _segments[r.SegmentId] = r;
        _segmentOrder.Enqueue(r.SegmentId);
        while (_segmentOrder.Count > MaxSegmentRecords) _segments.Remove(_segmentOrder.Dequeue());
    }

    // ------------------------------------------------------------------ queries

    public int? Resolve(int? speakerId)
    {
        if (speakerId is not { } id) return null;
        lock (_lock) return ResolveLocked(id);
    }

    private int? ResolveLocked(int id)
    {
        for (int guard = 0; guard < 32 && _mergedInto.TryGetValue(id, out var next); guard++) id = next;
        return _speakers.Any(s => s.Id == id) ? id : null;
    }

    public SegmentSpeaker? Lookup(int segmentId)
    {
        lock (_lock)
        {
            if (!_segments.TryGetValue(segmentId, out var r)) return null;
            int? id = r.SpeakerId is { } sid ? ResolveLocked(sid) : null;
            return new SegmentSpeaker(id, r.Uncertain || id == null, r.Similarity);
        }
    }

    public bool IsMuted(int speakerId)
    {
        lock (_lock) return ResolveLocked(speakerId) is { } id && _speakers.First(s => s.Id == id).Muted;
    }

    public (string Label, string Color) Describe(int? speakerId, bool uncertain)
    {
        if (uncertain || speakerId is not { } raw) return ("?", UnknownColor);
        lock (_lock)
        {
            var id = ResolveLocked(raw);
            var s = _speakers.FirstOrDefault(x => x.Id == id);
            return s == null ? ("?", UnknownColor) : (s.Label, s.Color);
        }
    }

    public IReadOnlyList<SpeakerInfo> Snapshot()
    {
        lock (_lock)
            return _speakers
                .OrderByDescending(s => s.LastHeard ?? DateTimeOffset.MinValue).ThenBy(s => s.Id)
                .Select(s => new SpeakerInfo(s.Id, s.Label, s.Color, s.IsNamed, s.Muted, s.Segments, s.SpeechMs / 1000, s.LastHeard))
                .ToList();
    }

    // ------------------------------------------------------------------ user actions

    /// <summary>Renames a speaker; named voices are saved and recognised automatically in future calls.</summary>
    public void Rename(int speakerId, string name)
    {
        name = name.Trim();
        lock (_lock)
        {
            var s = Find(speakerId);
            if (s == null) return;
            s.Name = name.Length == 0 ? null : name;
            if (s.Name != null)
            {
                s.Saved = true;
                PersistLocked(s);
            }
            else if (s.Saved && s.ProfileKey != null)
            {
                _store?.Delete(s.ProfileKey);
                s.Saved = false;
                s.ProfileKey = null;
            }
        }
        SpeakersChanged?.Invoke();
    }

    public void SetMuted(int speakerId, bool muted)
    {
        lock (_lock)
        {
            var s = Find(speakerId);
            if (s == null || s.Muted == muted) return;
            s.Muted = muted;
            if (s.Saved) PersistLocked(s);
        }
        SpeakersChanged?.Invoke();
    }

    public void Merge(int fromId, int intoId)
    {
        lock (_lock)
        {
            var from = Find(fromId);
            var into = Find(intoId);
            if (from == null || into == null || from == into) return;
            MergeLocked(from, into);
        }
        SpeakersChanged?.Invoke();
    }

    /// <summary>Moves one line to another speaker (or a new one when <paramref name="toSpeakerId"/> is null), correcting both profiles.</summary>
    public int? Reassign(int segmentId, int? toSpeakerId)
    {
        int? result;
        lock (_lock)
        {
            if (!_segments.TryGetValue(segmentId, out var r)) return null;
            if (r.SpeakerId is { } old && ResolveLocked(old) is { } oldId && Find(oldId) is { } oldSpeaker)
                RemoveFromSpeaker(oldSpeaker, r);
            var target = toSpeakerId is { } t ? Find(t) : null;
            target ??= CreateSpeaker();
            AddToSpeaker(target, r, r.Embedding != null ? SpeakerEmbedder.Cosine(r.Embedding, target.Centroid) : 1f);
            r.UserAssigned = true;
            // A speaker left with nothing (created by mistake) disappears.
            foreach (var empty in _speakers.Where(s => s.Segments == 0 && !s.Saved && s != target).ToList()) _speakers.Remove(empty);
            if (target.Saved) PersistLocked(target);
            result = target.Id;
        }
        SpeakersChanged?.Invoke();
        return result;
    }

    /// <summary>Deletes a voice profile; their past lines show "?" and they'll be treated as a new voice next time.</summary>
    public void Forget(int speakerId)
    {
        lock (_lock)
        {
            var s = Find(speakerId);
            if (s == null) return;
            _speakers.Remove(s);
            if (s.ProfileKey != null) _store?.Delete(s.ProfileKey);
            foreach (var r in _segments.Values.Where(r => r.SpeakerId == s.Id)) { r.SpeakerId = null; r.Uncertain = true; r.Contribution = 0; }
        }
        SpeakersChanged?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _speakers.Clear();
            _mergedInto.Clear();
            _pending.Clear();
            foreach (var r in _segments.Values) { r.SpeakerId = null; r.Uncertain = true; r.Contribution = 0; }
            _store?.DeleteAll();
            _nextNumber = 1;
        }
        SpeakersChanged?.Invoke();
    }

    /// <summary>Writes the latest profiles of named speakers (called when a session stops).</summary>
    public void SaveProfiles()
    {
        lock (_lock)
            foreach (var s in _speakers.Where(s => s.Saved)) PersistLocked(s);
    }

    private Speaker? Find(int id) => ResolveLocked(id) is { } rid ? _speakers.FirstOrDefault(s => s.Id == rid) : null;

    private void PersistLocked(Speaker s)
    {
        if (_store == null || s.Name == null) return;
        s.ProfileKey ??= Guid.NewGuid().ToString("N");
        _store.Upsert(new VoiceProfile
        {
            Key = s.ProfileKey,
            Name = s.Name,
            Color = s.Color,
            Muted = s.Muted,
            Centroid = s.Centroid,
            WeightSeconds = s.Weight,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    // ------------------------------------------------------------------ types

    private sealed class Speaker(int id, int number, string color)
    {
        private float[]? _centroid;
        public int Id { get; } = id;
        public int Number { get; } = number;
        public string Color { get; } = color;
        public string? Name { get; set; }
        public string? ProfileKey { get; set; }
        public bool Saved { get; set; }
        public bool Muted { get; set; }
        public double[] Sum { get; } = new double[512];
        public double Weight { get; set; }
        public int Segments { get; set; }
        public double SpeechMs { get; set; }
        public DateTimeOffset? LastHeard { get; set; }
        public bool IsNamed => Name != null;
        public string Label => Name ?? $"Speaker {Number}";

        public float[] Centroid => _centroid ??= SpeakerEmbedder.Normalize(Sum.Select(x => (float)x).ToArray());
        public void Invalidate() => _centroid = null;
    }

    private sealed class SegmentRecord(int segmentId, float[]? embedding, double durationMs)
    {
        public int SegmentId { get; } = segmentId;
        public float[]? Embedding { get; } = embedding;
        public double DurationMs { get; } = durationMs;
        public int? SpeakerId { get; set; }
        public bool Uncertain { get; set; } = true;
        public float Similarity { get; set; }
        public double Contribution { get; set; }
        public bool UserAssigned { get; set; }
    }
}
