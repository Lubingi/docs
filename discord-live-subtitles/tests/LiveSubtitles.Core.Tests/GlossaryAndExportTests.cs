using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Text;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.Core.Tests;

public class GlossaryTests
{
    [Fact]
    public void ReplacesWholeWordsOnlyAndKeepsSentenceCase()
    {
        var g = new GlossaryProcessor(new[]
        {
            new GlossaryEntry { Term = "brother", Replacement = "abi", Aliases = "big brother" },
            new GlossaryEntry { Term = "GG", Replacement = "good game" },
        });
        Assert.Equal("Abi, come here. Say hi to abi.", g.ProcessTranslation("Brother, come here. Say hi to big brother."));
        Assert.Equal("brotherhood", g.ProcessTranslation("brotherhood"));
        Assert.Equal("That was good game!", g.ProcessTranslation("That was gg!"));
    }

    [Fact]
    public void KeepAsIsFixesNearMissNames()
    {
        var g = new GlossaryProcessor(new[]
        {
            new GlossaryEntry { Mode = GlossaryMode.KeepAsIs, Term = "Emre", Aliases = "Emir, Emirhan" },
            new GlossaryEntry { Mode = GlossaryMode.KeepAsIs, Term = "Sindre" },
        });
        Assert.Equal("Emre and Sindre are here.", g.ProcessTranslation("Emir and Sindri are here."));
        Assert.Equal("Emre", g.ProcessTranslation("Emra"));   // one letter off
        Assert.Equal("Emma", g.ProcessTranslation("Emma"));   // two letters off: a different name, left alone
        Assert.Equal("Emre said", g.ProcessTranslation("Emirhan said"));
        // Ordinary lowercase words and different first letters are left alone.
        Assert.Equal("more emir Andre", g.ProcessTranslation("more emir Andre"));
    }

    [Fact]
    public void OriginalCanBeExcluded()
    {
        var g = new GlossaryProcessor(new[] { new GlossaryEntry { Term = "kanka", Replacement = "buddy", ApplyToOriginal = false } });
        Assert.Equal("selam kanka", g.ProcessOriginal("selam kanka"));
        Assert.Equal("hi buddy", g.ProcessTranslation("hi kanka"));
    }

    [Theory]
    [InlineData("emre", "emir", 2)]
    [InlineData("emre", "erme", 1)]
    [InlineData("sindre", "sindri", 1)]
    public void DistanceIsDamerau(string a, string b, int expected) => Assert.Equal(expected, GlossaryProcessor.Distance(a, b));
}

public class ExportTests
{
    private static TranscriptLine Line(int id, int startSec, string speaker, string text, string original, bool same = false) => new()
    {
        SegmentId = id,
        StartedAt = new DateTimeOffset(2026, 9, 25, 20, 0, startSec, TimeSpan.Zero),
        StreamStartMs = startSec * 1000,
        StreamEndMs = startSec * 1000 + 2500,
        SpeakerLabel = speaker,
        Translation = same ? "" : text,
        Original = original,
        SameLanguage = same,
        IsFinal = true,
    };

    [Fact]
    public void SrtHasSequentialNonOverlappingCues()
    {
        var lines = new[] { Line(1, 0, "Emre", "Hello.", "Merhaba."), Line(2, 2, "Speaker 2", "How are you?", "Nasılsın?"), Line(3, 10, "", "", "") };
        var srt = TranscriptExporter.ToSrt(lines);
        Assert.Equal("1\n00:00:00,000 --> 00:00:02,000\nEmre: Hello.\n\n2\n00:00:02,000 --> 00:00:05,200\nSpeaker 2: How are you?\n\n",
            srt.Replace("\r\n", "\n"));
    }

    [Fact]
    public void TextIncludesOriginalAndSameLanguageLines()
    {
        var txt = TranscriptExporter.ToText(new[] { Line(1, 0, "Emre", "Hello.", "Merhaba."), Line(2, 3, "Anna", "", "I'm fine.", same: true) });
        Assert.Contains("Emre: Hello.", txt);
        Assert.Contains("(Merhaba.)", txt);
        Assert.Contains("Anna: I'm fine.", txt);
        Assert.DoesNotContain("(I'm fine.)", txt);
    }
}
