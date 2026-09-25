using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Vad;

namespace LiveSubtitles.Core.Tests;

public class PipelineTests
{
    [Fact]
    public async Task RealSpeechProducesAttributedFinalLines()
    {
        var wav = TestPaths.Audio("two-speakers-en.wav");
        var model = TestPaths.Model("silero_vad.onnx");
        if (!TestPaths.Has(wav) || !TestPaths.Has(model)) return; // assets not downloaded

        var (samples, rate) = WavFile.ReadMono(wav);
        var store = new TranscriptStore();
        var debug = new ConcurrentDictionary<int, SegmentDebugInfo>();
        using var vad = new SileroVad(model);
        var options = new PipelineOptions
        {
            ShowOriginal = false,
            Attribution = new AttributionOptions { FinalizeIdleMs = 600 },
        };
        var pipeline = new SubtitlePipeline(options, vad, new NoSpeakerIdentifier(), () => new FakeTranslationSession(lagMs: 1000), store, NullLog.Instance)
        {
            IsLiveSource = false,
        };
        pipeline.SegmentDebug += d => debug[d.SegmentId] = d;
        pipeline.Start();
        await Task.Delay(200); // let the fake session connect

        // 48 kHz like a real capture device: exercise the resamplers too.
        var at48 = new StreamResampler(rate, 48000).Process(samples);
        for (int i = 0; i < at48.Length; i += 480) pipeline.PushAudio(at48.AsSpan(i, Math.Min(480, at48.Length - i)).ToArray(), 48000);
        pipeline.PushAudio(new float[48000 * 4], 48000);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (store.Snapshot().Count == 0 || store.Snapshot().Any(l => !l.IsFinal)))
            await Task.Delay(100);
        await pipeline.StopAsync();

        var lines = store.Snapshot().Where(l => l.HasText).ToList();
        Assert.True(lines.Count >= 2, $"expected several subtitle lines, got {lines.Count}");
        Assert.All(store.Snapshot(), l => Assert.True(l.IsFinal));

        int total = 0, misplaced = 0;
        foreach (var line in lines)
        {
            var info = debug[line.SegmentId];
            foreach (Match m in Regex.Matches(line.Translation, @"c(\d+)"))
            {
                total++;
                double heardAt = int.Parse(m.Groups[1].Value) * 200.0;
                if (heardAt < info.ModelStartMs - 250 || heardAt > info.ModelEndMs + 250) misplaced++;
            }
        }
        Assert.True(total > 20, $"too few tokens ({total})");
        Assert.True(misplaced <= total / 10, $"{misplaced}/{total} tokens attributed to the wrong segment");
    }
}
