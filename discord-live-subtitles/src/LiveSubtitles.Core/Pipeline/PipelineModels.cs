using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Pipeline;

public enum SameLanguageMode { ShowOriginal, Hide }

public sealed record PipelineOptions
{
    public SegmenterOptions Segmenter { get; init; } = new();
    public AttributionOptions Attribution { get; init; } = new();
    public bool ShowOriginal { get; init; } = true;
    /// <summary>Subtitle language code ("en"); used to recognise speech that is already in it.</summary>
    public string OutputLanguage { get; init; } = "en";
    /// <summary>What to do with speech that is already in the output language (the model does not translate it).</summary>
    public SameLanguageMode SameLanguage { get; init; } = SameLanguageMode.ShowOriginal;
    /// <summary>Speaker is "muted" only if matched at least this confidently during early identification.</summary>
    public float MuteMinSimilarity { get; init; } = 0.5f;
    /// <summary>Name of the translation service, for status messages.</summary>
    public string ServiceName { get; init; } = "OpenAI";
    /// <summary>The engine stamps original-language words with the time they were spoken (Soniox), so they are placed
    /// on lines by time instead of by arrival order.</summary>
    public bool OriginalTimed { get; init; }
}

public enum SegmentDecision { Pending, Allowed, Suppressed }

public sealed record SegmentDebugInfo
{
    public int SegmentId { get; init; }
    public double StreamStartMs { get; init; }
    public double StreamEndMs { get; init; }
    public double DurationMs => Math.Max(0, StreamEndMs - StreamStartMs);
    public string Status { get; init; } = "";
    public string SpeakerLabel { get; init; } = "";
    public float Similarity { get; init; }
    public float SecondBest { get; init; }
    public bool Uncertain { get; init; }
    public double? ModelStartMs { get; init; }
    public double? ModelEndMs { get; init; }
    public double? FirstTextLatencyMs { get; init; }
    public double? FinalLatencyMs { get; init; }
    public double? TextLagMs { get; init; }
    public string RawTranslation { get; init; } = "";
    public string RawOriginal { get; init; } = "";
}

public sealed record PipelineStatus
{
    public ConnectionState Connection { get; init; }
    public string ConnectionMessage { get; init; } = "";
    public bool Paused { get; init; }
    public bool Speaking { get; init; }
    public bool Sending { get; init; }
    public double AudioSentSeconds { get; init; }
    public double? AvgTextLagMs { get; init; }
    public double? LastFinalLatencyMs { get; init; }
    public int Segments { get; init; }
    public int Reconnects { get; init; }
    public string VadName { get; init; } = "";
}
