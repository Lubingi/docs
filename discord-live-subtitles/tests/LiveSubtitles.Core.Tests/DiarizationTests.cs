using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Tests;

public class DiarizationTests
{
    /// <summary>Runs VAD + segmenter + online clustering over a WAV like the live pipeline does.</summary>
    private static List<(SegmentEnded Segment, SpeakerMatch Match)> Diarize(string wav, SpeakerRegistry registry)
    {
        var (samples, rate) = WavFile.ReadMono(wav);
        using var vad = new SileroVad(TestPaths.Model("silero_vad.onnx"));
        var to16 = new StreamResampler(rate, 16000).Process(samples);
        var to24 = new StreamResampler(rate, 24000).Process(samples);
        var seg = new SpeechSegmenter(new SegmenterOptions());
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
        var ids = r.Select(x => reg.Lookup(x.Segment.SegmentId)!.SpeakerId).ToList();
        Assert.Equal(8, ids.Count);
        // Voices that are the same person (by the model's offline similarity structure) share a label ...
        Assert.Equal(ids[0], ids[2]);
        Assert.Equal(ids[2], ids[7]);
        Assert.Equal(ids[4], ids[5]);
        // ... and different people never do.
        var groups = new[] { new[] { 0, 2, 7 }, new[] { 1, 6 }, new[] { 4, 5 } };
        foreach (var g1 in groups)
        foreach (var g2 in groups.Where(g => g != g1))
            Assert.DoesNotContain(g1.Select(i => ids[i]).Where(i => i != null), id => g2.Select(i => ids[i]).Contains(id));
        Assert.InRange(reg.Snapshot().Count, 3, 5);
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
