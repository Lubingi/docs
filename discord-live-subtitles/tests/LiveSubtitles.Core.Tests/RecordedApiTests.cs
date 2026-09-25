using System.Text.Json;
using LiveSubtitles.Core.Pipeline;

namespace LiveSubtitles.Core.Tests;

/// <summary>
/// Replays text deltas recorded from the real gpt-realtime-translate API (VPS test run 1: generated Norwegian,
/// Turkish and English dialogues) through the attribution logic, and checks each line gets its own sentences.
/// </summary>
public class RecordedApiTests
{
    private static (List<TimelineSegment> Segments, StreamAttributor Translation, StreamAttributor Original) Replay(string name)
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "recorded_deltas.json"))).RootElement.GetProperty(name);
        var segs = root.GetProperty("segments").EnumerateArray().Select(s => new TimelineSegment(s.GetProperty("id").GetInt32())
        {
            ModelStartMs = s.GetProperty("start").GetDouble(),
            ModelSpeechEndMs = s.GetProperty("end").GetDouble(),
            SpeechEnded = true,
            EndSentWall = DateTimeOffset.UtcNow,
        }).ToList();
        var options = new AttributionOptions();
        var (translation, original) = StreamAttributor.CreatePair(options, () => segs, withOriginal: true);
        foreach (var d in root.GetProperty("deltas").EnumerateArray())
        {
            var stream = d[0].GetString() == "EN" ? translation : original;
            stream.Add(d[2].GetString()!, d[1].GetDouble(), DateTimeOffset.UtcNow);
        }
        return (segs, translation, original);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("tr")]
    [InlineData("en")]
    public void NoLineStartsWithTheLastLinesPunctuation(string name)
    {
        var (segs, translation, original) = Replay(name);
        foreach (var s in segs)
        {
            var t = translation.TextFor(s.Id).Trim();
            Assert.False(t.Length > 0 && ".?!,;:".Contains(t[0]), $"segment {s.Id} starts with punctuation: \"{t}\"");
        }
    }

    [Fact]
    public void NorwegianLinesMatchTheirSpeech()
    {
        var (_, t, _) = Replay("no");
        // Before the fix these bled into the neighbouring line (e.g. "?" alone, "eight? Eight works…", "wild. This time…").
        Assert.Equal("Shall we play a bit tonight?", t.TextFor(5).Trim());
        Assert.Equal("how about we start at eight?", t.TextFor(9).Trim());
        Assert.Equal("Eight works for me,", t.TextFor(10).Trim());
        Assert.Equal("Yeah, that was totally wild.", t.TextFor(15).Trim());
        Assert.Equal("This time we need to talk more together.", t.TextFor(16).Trim());
        Assert.Equal("Perfect. Then we'll see each other at eight.", t.TextFor(19).Trim());
    }

    [Fact]
    public void TurkishLinesMatchTheirSpeech()
    {
        var (_, t, _) = Replay("tr");
        Assert.EndsWith("let's make a plan beforehand.", t.TextFor(11).Trim());
        Assert.StartsWith("Okay, when I get there", t.TextFor(12).Trim());
    }

    [Theory]
    [InlineData("Hello. How are you?", 2)]
    [InlineData("It costs 3.5 euros.", 1)]
    [InlineData("Wait... what?!", 2)]
    [InlineData("no end", 0)]
    public void CountsSentences(string text, int expected) => Assert.Equal(expected, StreamAttributor.CountSentences(text));
}

public class LanguageGuessTests
{
    [Theory]
    [InlineData("Selam millet, beni duyabiliyor musunuz?", false)]
    [InlineData("Ama geçen haftaki gibi yine kaybetmeyelim.", false)]
    [InlineData("Ja, vi hører deg fint. Hvordan gikk det på jobb i dag?", false)]
    [InlineData("Jeg er med, men jeg må spise middag først.", false)]
    [InlineData("Samme her.", false)]
    [InlineData("Enig.", false)]
    [InlineData("Perfekt. Da ses vi klokka åtte.", false)]
    [InlineData("Denne gangen må vi snakke mer sammen.", false)]
    [InlineData("Tamam, ben gelince destek oynarım.", false)]
    [InlineData("Yo Alex, you still down for gaming tonight?", true)]
    [InlineData("Eight works. Might be a bit late for me though.", true)]
    [InlineData("Who's hosting the server, you or me?", true)]
    [InlineData("I can host, if my upload holds up. Otherwise you can. No biggie.", true)]
    [InlineData("Let's mess around for the first couple matches.", true)]
    [InlineData("Sounds good.", true)]
    public void RecognisesEnglish(string text, bool english) => Assert.Equal(english, LiveSubtitles.Core.Text.LanguageGuess.LooksEnglish(text));
}
