using LiveSubtitles.Core.Pipeline;

namespace LiveSubtitles.Core.Tests;

public class SegmenterTests
{
    private static (List<SegmenterEvent> Events, SpeechSegmenter Seg) Run(SegmenterOptions o, IEnumerable<float> probs)
    {
        var seg = new SpeechSegmenter(o);
        var all = new List<SegmenterEvent>();
        var buf = new List<SegmenterEvent>();
        foreach (var p in probs)
        {
            buf.Clear();
            seg.Process(new float[512], new float[768], p, buf);
            all.AddRange(buf);
        }
        return (all, seg);
    }

    private static IEnumerable<float> Frames(params (float Prob, int Ms)[] parts) =>
        parts.SelectMany(p => Enumerable.Repeat(p.Prob, (int)Math.Round(p.Ms / 32.0)));

    [Fact]
    public void SplitsOnPausesAndSendsPreRollAndTail()
    {
        var o = new SegmenterOptions();
        var (ev, _) = Run(o, Frames((0f, 2000), (0.9f, 1600), (0.1f, 700), (0.9f, 1000), (0f, 5000)));

        var ended = ev.OfType<SegmentEnded>().ToList();
        Assert.Equal(2, ended.Count);
        Assert.InRange(ended[0].StreamStartMs, 2000 - o.PreRollMs - 100, 2000 - o.PreRollMs + 40);
        Assert.InRange(ended[0].StreamEndMs, 3550, 3700);
        Assert.InRange(ended[1].StreamStartMs, 4300 - o.PreRollMs - 100, 4300);

        var sent = ev.OfType<SendFrame>().ToList();
        double sentMs = sent.Count * 32;
        // speech (2.6 s) + preroll + short pause + tail ≈ 5 s; the long leading/trailing silence is not sent
        Assert.InRange(sentMs, 4000, 5600);
        Assert.Single(ev.OfType<GateClosed>());
        // frames are in chronological order
        Assert.True(sent.Zip(sent.Skip(1)).All(p => p.Second.StreamMs > p.First.StreamMs));
    }

    [Fact]
    public void ShortPausesDoNotSplit()
    {
        var (ev, _) = Run(new SegmenterOptions(), Frames((0f, 500), (0.9f, 1000), (0.1f, 200), (0.9f, 1000), (0f, 3000)));
        Assert.Single(ev.OfType<SegmentEnded>());
    }

    [Fact]
    public void ClicksAreIgnored()
    {
        var (ev, _) = Run(new SegmenterOptions(), Frames((0f, 500), (0.9f, 96), (0f, 3000)));
        Assert.Empty(ev.OfType<SegmentStarted>());
        Assert.Empty(ev.OfType<SendFrame>());
    }

    [Fact]
    public void LongSpeechIsSplitAtMaxLength()
    {
        var o = new SegmenterOptions { MaxSegmentMs = 5000 };
        var (ev, _) = Run(o, Frames((0f, 500), (0.9f, 12000), (0f, 3000)));
        var ended = ev.OfType<SegmentEnded>().ToList();
        Assert.Equal(3, ended.Count);
        Assert.True(ended[0].Split);
        Assert.False(ended[^1].Split);
    }

    [Fact]
    public void EarlyEventFiresOncePerSegment()
    {
        var (ev, _) = Run(new SegmenterOptions(), Frames((0f, 500), (0.9f, 3000), (0f, 2000), (0.9f, 500), (0f, 2000)));
        var early = ev.OfType<SegmentEarly>().ToList();
        Assert.Equal(2, early.Count);
        Assert.Equal(early.Select(e => e.SegmentId).Distinct().Count(), early.Count);
    }

    [Fact]
    public void ContinuousModeSendsEverything()
    {
        var o = new SegmenterOptions { SendContinuously = true };
        var (ev, _) = Run(o, Frames((0f, 3200), (0.9f, 1000), (0f, 3200)));
        var sent = ev.OfType<SendFrame>().Count();
        Assert.InRange(sent * 32, 6800, 7400 + 1); // everything except the lookback still buffered
        Assert.Empty(ev.OfType<GateClosed>());
    }
}
