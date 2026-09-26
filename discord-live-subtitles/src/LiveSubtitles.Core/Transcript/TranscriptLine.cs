namespace LiveSubtitles.Core.Transcript;

/// <summary>Immutable snapshot of one subtitle line (one speech segment). Updated copies are published as text streams in.</summary>
public sealed record TranscriptLine
{
    public required int SegmentId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>Position on the capture timeline (for .srt timestamps).</summary>
    public double StreamStartMs { get; init; }
    public double StreamEndMs { get; init; }
    public int? SpeakerId { get; init; }
    /// <summary>"Speaker 2", "Emre", "?" or "" when speaker identification is off.</summary>
    public string SpeakerLabel { get; init; } = "";
    public string SpeakerColor { get; init; } = "#FFFFFF";
    public float SpeakerConfidence { get; init; }
    /// <summary>The voice did not confidently match anyone; shown with a neutral "?" label.</summary>
    public bool SpeakerUncertain { get; init; }
    public string Translation { get; init; } = "";
    public string Original { get; init; } = "";
    public bool IsFinal { get; init; }
    /// <summary>The speech was already in the output language (no translation produced).</summary>
    public bool SameLanguage { get; init; }
    /// <summary>Hidden from the overlay (muted speaker, or empty/noise segment).</summary>
    public bool Hidden { get; init; }

    /// <summary>What the overlay shows as the main text.</summary>
    /// <remarks>A finished line is never left blank: if the model produced no translation, the original is shown.</remarks>
    public string DisplayText => Translation.Trim().Length > 0 ? Translation.Trim() : SameLanguage || IsFinal ? Original.Trim() : "";

    /// <summary>The main text is the original (no translation), so the separate original line is not shown again.</summary>
    public bool ShowsOriginalAsText => Translation.Trim().Length == 0 && DisplayText.Length > 0;
    public bool HasText => DisplayText.Length > 0;
}
