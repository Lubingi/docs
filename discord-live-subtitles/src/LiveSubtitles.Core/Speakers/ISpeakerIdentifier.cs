namespace LiveSubtitles.Core.Speakers;

/// <summary>Result of matching one speech segment against the known voices.</summary>
public sealed record SpeakerMatch(int? SpeakerId, float Similarity, float SecondBest, bool IsNew, bool Uncertain)
{
    public static readonly SpeakerMatch None = new(null, 0, 0, false, true);
}

/// <summary>Local speaker diarization. All calls happen on the pipeline's speaker worker thread.</summary>
public interface ISpeakerIdentifier
{
    bool Enabled { get; }
    bool AnyMuted { get; }
    /// <summary>Compare only (does not update voice profiles). Used to decide early whether to translate a muted speaker.</summary>
    SpeakerMatch Peek(float[] audio16k);
    /// <summary>Assign a finished segment to a speaker, creating/updating profiles.</summary>
    SpeakerMatch Assign(int segmentId, float[] audio16k);
    bool IsMuted(int speakerId);
    /// <summary>Maps a (possibly merged-away) speaker id to its current id.</summary>
    int? Resolve(int? speakerId);
    (string Label, string Color) Describe(int? speakerId, bool uncertain);
    event Action? SpeakersChanged;
}

/// <summary>Used when speaker identification is turned off.</summary>
public sealed class NoSpeakerIdentifier : ISpeakerIdentifier
{
    public bool Enabled => false;
    public bool AnyMuted => false;
    public SpeakerMatch Peek(float[] audio16k) => SpeakerMatch.None;
    public SpeakerMatch Assign(int segmentId, float[] audio16k) => SpeakerMatch.None;
    public bool IsMuted(int speakerId) => false;
    public int? Resolve(int? speakerId) => speakerId;
    public (string Label, string Color) Describe(int? speakerId, bool uncertain) => ("", "#FFFFFF");
    public event Action? SpeakersChanged { add { } remove { } }
}
