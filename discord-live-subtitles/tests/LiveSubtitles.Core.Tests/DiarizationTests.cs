using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Tests;

public partial class DiarizationTests
{
    private static List<(SegmentEnded Segment, SpeakerMatch Match)> Diarize(string wav, SpeakerRegistry registry) => DiarizationHelper.Diarize(wav, registry);
}

internal static class DiarizationHelper
{
    /// <summary>Runs VAD + segmenter + online clustering over a WAV like the live pipeline does.</summary>
    public static List<(SegmentEnded Segment, SpeakerMatch Match)> Diarize(string wav, SpeakerRegistry registry, SegmenterOptions? options = null)
    {
        var (samples, rate) = WavFile.ReadMono(wav);
        using var vad = new SileroVad(TestPaths.Model("silero_vad.onnx"));
        var to16 = new StreamResampler(rate, 16000).Process(samples);
        var to24 = new StreamResampler(rate, 24000).Process(samples);
        var seg = new SpeechSegmenter(options ?? new SegmenterOptions());
        var events = new List<SegmenterEvent>();
        var result = new List<(SegmentEnded, SpeakerMatch)>();
        int frames = Math.Min(to16.Length / 512, to24.Length / 768);
        for (int i = 0; i <= frames; i++)
        {
            events.Clear();
            if (i == frames) seg.Flush(events);
            else seg.Process(to16[(i * 512)..((i + 1) * 512)], to24[(i * 768)..((i + 1) * 768)], vad.Process(to16.AsSpan(i * 512, 512)), events);
            foreach (var e in events.OfType<SegmentEnded>()) result.Add((e, registry.Assign(e.SegmentId, e.Audio16)));
        }
        return result;
    }
}

public partial class DiarizationTests
{
    private static SpeakerRegistry NewRegistry(out SpeakerEmbedder embedder, VoiceProfileStore? store = null)
    {
        var emb = new SpeakerEmbedder(TestPaths.Model("campplus_voxceleb_16k.onnx"));
        embedder = emb;
        return new SpeakerRegistry(a => emb.Embed(a), new DiarizationSettings(), store);
    }

    private static bool Ready(string wav) => TestPaths.Has(TestPaths.Audio(wav)) && TestPaths.Has(TestPaths.Model("campplus_voxceleb_16k.onnx"));

    [Fact]
    public void TwoSpeakersAreSeparated()
    {
        if (!Ready("two-speakers-en.wav")) return;
        var reg = NewRegistry(out var emb);
        using var _ = emb;
        var r = Diarize(TestPaths.Audio("two-speakers-en.wav"), reg);
        var ids = r.Select(x => reg.Lookup(x.Segment.SegmentId)!.SpeakerId).ToList();
        Assert.Equal(4, ids.Count);
        Assert.All(ids, id => Assert.NotNull(id));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[2], ids[3]);
        Assert.NotEqual(ids[0], ids[2]);
        Assert.Equal(2, reg.Snapshot().Count);
    }

    [Fact]
    public void FourSpeakerConversationIsConsistent()
    {
        if (!Ready("four-speakers-zh.wav")) return;
        var reg = NewRegistry(out var emb);
        using var _ = emb;
        var r = Diarize(TestPaths.Audio("four-speakers-zh.wav"), reg);
        int? At(double seconds)
        {
            var hit = r.First(x => x.Segment.StreamStartMs / 1000 <= seconds && x.Segment.StreamEndMs / 1000 >= seconds);
            var l = reg.Lookup(hit.Segment.SegmentId)!;
            return l.Uncertain ? null : l.SpeakerId;
        }
        // Facts the model's similarity structure supports strongly (0.56–0.75 same voice, ≤ 0.34 different).
        Assert.NotNull(At(2));
        Assert.Equal(At(2), At(23));
        Assert.Equal(At(2), At(53));
        Assert.NotEqual(At(2), At(14));
        Assert.NotEqual(At(2), At(49));
        Assert.InRange(reg.Snapshot().Count, 3, 6);
    }

    [Fact]
    public void RenamedVoiceIsRecognisedInALaterSession()
    {
        if (!Ready("two-speakers-en.wav")) return;
        var file = Path.Combine(Path.GetTempPath(), $"voices-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VoiceProfileStore(file);
            var reg = NewRegistry(out var emb, store);
            using var _ = emb;
            var r = Diarize(TestPaths.Audio("two-speakers-en.wav"), reg);
            int second = reg.Lookup(r[2].Segment.SegmentId)!.SpeakerId!.Value;
            reg.Rename(second, "Emre");
            reg.SetMuted(second, true);
            Assert.Single(store.Load());
            Assert.Equal("Emre", store.Load()[0].Name);
            Assert.DoesNotContain(File.ReadAllText(file), "RIFF"); // embeddings only

            // New app run: only the named voice is known.
            var reg2 = new SpeakerRegistry(a => emb.Embed(a), new DiarizationSettings(), store);
            Assert.True(reg2.AnyMuted);
            var r2 = Diarize(TestPaths.Audio("two-speakers-en.wav"), reg2);
            var (label, _) = reg2.Describe(reg2.Lookup(r2[3].Segment.SegmentId)!.SpeakerId, false);
            Assert.Equal("Emre", label);
            Assert.True(reg2.IsMuted(reg2.Lookup(r2[3].Segment.SegmentId)!.SpeakerId!.Value));
            Assert.NotEqual("Emre", reg2.Describe(reg2.Lookup(r2[0].Segment.SegmentId)!.SpeakerId, false).Label);

            reg2.ClearAll();
            Assert.Empty(store.Load());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MergeAndReassignUpdateLabels()
    {
        if (!Ready("two-speakers-en.wav")) return;
        var reg = NewRegistry(out var emb);
        using var _ = emb;
        var r = Diarize(TestPaths.Audio("two-speakers-en.wav"), reg);
        int a = reg.Lookup(r[0].Segment.SegmentId)!.SpeakerId!.Value;
        int b = reg.Lookup(r[2].Segment.SegmentId)!.SpeakerId!.Value;

        int changes = 0;
        reg.SpeakersChanged += () => changes++;
        reg.Reassign(r[1].Segment.SegmentId, b);
        Assert.Equal(b, reg.Lookup(r[1].Segment.SegmentId)!.SpeakerId);
        Assert.False(reg.Lookup(r[1].Segment.SegmentId)!.Uncertain);

        reg.Merge(b, a);
        Assert.Single(reg.Snapshot());
        Assert.All(r, x => Assert.Equal(a, reg.Lookup(x.Segment.SegmentId)!.SpeakerId));
        Assert.Equal(a, reg.Resolve(b));

        reg.Forget(a);
        Assert.Empty(reg.Snapshot());
        Assert.Equal(("?", SpeakerRegistry.UnknownColor), reg.Describe(reg.Lookup(r[0].Segment.SegmentId)!.SpeakerId, reg.Lookup(r[0].Segment.SegmentId)!.Uncertain));
        Assert.True(changes >= 3);
    }

    [Fact]
    public void UnrelatedNoiseIsNotForcedOntoASpeaker()
    {
        if (!Ready("two-speakers-en.wav")) return;
        var reg = NewRegistry(out var emb);
        using var _ = emb;
        Diarize(TestPaths.Audio("two-speakers-en.wav"), reg);
        var rng = new Random(1);
        var noise = Enumerable.Range(0, 16000).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.2f).ToArray(); // 1 s: too short to create a voice
        var m = reg.Assign(9999, noise);
        Assert.True(m.Uncertain);
        Assert.Null(m.SpeakerId);
    }
}

public class DialogueScriptTests
{
    [Fact]
    public void ParsesModelJsonAndClampsSpeakers()
    {
        var lines = LiveSubtitles.Core.Testing.DialogueGenerator.ParseScript(
            "```json\n{\"lines\":[{\"speaker\":1,\"text\":\"Selam!\"},{\"speaker\":5,\"text\":\"Naber?\"},{\"speaker\":2,\"text\":\"\"}]}\n```", 3);
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].Speaker);
        Assert.Equal(2, lines[1].Speaker);
    }

    [Fact]
    public void BuiltInScriptsUseAllFourVoices()
    {
        foreach (var script in LiveSubtitles.Core.Testing.DialogueGenerator.BuiltInScripts.Values)
            Assert.Equal(4, script.Select(l => l.Speaker).Distinct().Count());
    }
}

/// <summary>
/// Reproduces VPS bug 3: fast turn-taking (0.3–0.4 s gaps) puts two people into one segment; the blended voice
/// profile then matched everyone and the conversation collapsed into a single speaker.
/// </summary>
public class FastTurnTakingTests
{
    [Theory]
    [InlineData(300)]   // default: pauses of 0.3 s split turns
    [InlineData(480)]   // older default: turns merge into one segment, voice-change detection must catch it
    public void QuickRepliesDoNotCollapseIntoOneSpeaker(int minSilenceMs)
    {
        var wav = TestPaths.Audio("two-speakers-en.wav");
        if (!TestPaths.Has(wav) || !TestPaths.Has(TestPaths.Model("campplus_voxceleb_16k.onnx"))) return;
        var (samples, _) = WavFile.ReadMono(wav);
        // Utterances of this file: A = 1.70–3.55 s and 4.45–6.53 s, B = 9.41–11.49 s and 12.22–14.69 s.
        float[] Cut(double s, double e) => samples[(int)(s * 16000)..(int)(e * 16000)];
        var order = new[] { Cut(1.70, 3.55), Cut(9.41, 11.49), Cut(4.45, 6.53), Cut(12.22, 14.69), Cut(1.70, 3.55), Cut(9.41, 11.49) };
        bool[] isA = { true, false, true, false, true, false };
        var gaps = new[] { 0.30, 0.36, 0.41, 0.33, 0.38, 1.0 };
        var parts = new List<float>(new float[8000]);
        var starts = new List<double>();
        for (int i = 0; i < order.Length; i++)
        {
            starts.Add(parts.Count / 16000.0);
            parts.AddRange(order[i]);
            parts.AddRange(new float[(int)(gaps[i] * 16000)]);
        }
        var file = Path.Combine(Path.GetTempPath(), $"fast-{Guid.NewGuid():N}.wav");
        WavFile.WriteMono16(file, parts.ToArray(), 16000);
        try
        {
            using var emb = new SpeakerEmbedder(TestPaths.Model("campplus_voxceleb_16k.onnx"));
            var reg = new SpeakerRegistry(x => emb.Embed(x), new DiarizationSettings());
            var result = DiarizationHelper.Diarize(file, reg, new SegmenterOptions { MinSilenceMs = minSilenceMs });
            // Collapsing both people into one voice is the bug. With 300 ms pauses both voices must be found;
            // with 480 ms (turns merged) showing "?" instead of guessing is acceptable.
            Assert.NotEqual(1, reg.Snapshot().Count);
            if (minSilenceMs == 300) Assert.True(reg.Snapshot().Count >= 2, $"found {reg.Snapshot().Count} speaker(s)");
            // Labels of segments that start inside an utterance of A vs of B must never be the same person.
            var aIds = new HashSet<int>();
            var bIds = new HashSet<int>();
            foreach (var (seg, _) in result)
            {
                var l = reg.Lookup(seg.SegmentId)!;
                if (l.Uncertain || l.SpeakerId is not { } id) continue;
                int u = starts.FindLastIndex(t => t <= seg.StreamStartMs / 1000 + 0.35);
                if (u < 0) continue;
                (isA[u] ? aIds : bIds).Add(id);
            }
            if (minSilenceMs == 300)
            {
                Assert.NotEmpty(aIds);
                Assert.NotEmpty(bIds);
            }
            Assert.Empty(aIds.Intersect(bIds));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
