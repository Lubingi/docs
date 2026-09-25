using LiveSubtitles.Core.Pipeline;

namespace LiveSubtitles.Core.Tests;

public class AttributionTests
{
    private static TimelineSegment Seg(int id, double start, double end) =>
        new(id) { ModelStartMs = start, ModelSpeechEndMs = end, SpeechEnded = true, EndSentWall = DateTimeOffset.UtcNow };

    [Fact]
    public void LaggingTextStaysWithSpeakerUntilTheirSentenceEnds()
    {
        // A speaks 0-5000, B starts right after at 5300. The model lags ~1.5 s behind.
        var segs = new List<TimelineSegment> { Seg(1, 0, 5000), Seg(2, 5300, 9000) };
        var attr = new StreamAttributor(new AttributionOptions(), () => segs);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, attr.Add("Hello", 1500, now));
        Assert.Equal(1, attr.Add(" there, how", 3000, now));
        Assert.Equal(1, attr.Add(" are you", 5600, now));   // after B started, but A's sentence isn't finished
        Assert.Equal(1, attr.Add(" today?", 6200, now));    // still within close lag of A's end
        Assert.Equal(2, attr.Add(" I'm", 6400, now));       // A's sentence ended → next text is B's
        Assert.Equal(2, attr.Add(" fine.", 8000, now));
        Assert.Equal("Hello there, how are you today?", attr.TextFor(1));
        Assert.Equal(" I'm fine.", attr.TextFor(2));
    }

    [Fact]
    public void MovesOnAfterCloseLagEvenWithoutPunctuation()
    {
        var segs = new List<TimelineSegment> { Seg(1, 0, 2000), Seg(2, 2300, 6000) };
        var attr = new StreamAttributor(new AttributionOptions { CloseLagMs = 1600 }, () => segs);
        var now = DateTimeOffset.UtcNow;
        attr.Add("so anyway", 1200, now);
        Assert.Equal(1, attr.Add(" we", 3500, now));
        Assert.Equal(2, attr.Add(" next", 3700, now));
    }

    [Fact]
    public void EmptySegmentIsSkippedWhenTheOriginalConfirmsIt()
    {
        // Segment 1 was a laugh: the original-language transcript has nothing for it and has moved on.
        var segs = new List<TimelineSegment> { Seg(1, 0, 1500), Seg(2, 2000, 5000) };
        var (attr, original) = StreamAttributor.CreatePair(new AttributionOptions(), () => segs, withOriginal: true);
        Assert.Equal(2, original.Add("Merhaba", 2500, DateTimeOffset.UtcNow));
        Assert.Equal(2, attr.Add("Hi", 2600, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LateFirstTextOfAShortLineIsNotSkipped()
    {
        // A 1.3 s line whose translation only starts after the next line began (seen with the real API).
        var segs = new List<TimelineSegment> { Seg(1, 0, 1300), Seg(2, 1800, 4000) };
        var attr = new StreamAttributor(new AttributionOptions(), () => segs);
        Assert.Equal(1, attr.Add("I'm in,", 2400, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TextNeverGoesToASegmentThatHasNotStarted()
    {
        var segs = new List<TimelineSegment> { Seg(1, 0, 3000), Seg(2, 3100, 5000) };
        var attr = new StreamAttributor(new AttributionOptions(), () => segs);
        attr.Add("One.", 2900, DateTimeOffset.UtcNow);
        Assert.Equal(1, attr.Add(" Two", 3200, DateTimeOffset.UtcNow)); // 3200 < 3100 + MinLag
    }

    [Fact]
    public void IsDoneAfterIdleOrWhenPassed()
    {
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Seg(1, 0, 2000); s1.EndSentWall = t0;
        var s2 = new TimelineSegment(2) { ModelStartMs = 2500 };
        var segs = new List<TimelineSegment> { s1, s2 };
        var attr = new StreamAttributor(new AttributionOptions { FinalizeIdleMs = 1000 }, () => segs);
        attr.Add("Done.", 1800, t0.AddMilliseconds(500));
        Assert.False(attr.IsDone(s1, 0, t0.AddMilliseconds(1000)));
        Assert.True(attr.IsDone(s1, 0, t0.AddMilliseconds(1600)));
        Assert.False(attr.IsDone(s2, 1, t0.AddMilliseconds(5000))); // still speaking
    }

    [Theory]
    [InlineData("Hello.", true)]
    [InlineData("Really?\"", true)]
    [InlineData("and then", false)]
    [InlineData("", false)]
    public void SentenceEndDetection(string text, bool expected) => Assert.Equal(expected, StreamAttributor.EndsSentence(text));
}
