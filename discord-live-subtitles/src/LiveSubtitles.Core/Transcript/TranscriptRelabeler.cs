using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Speakers;

namespace LiveSubtitles.Core.Transcript;

/// <summary>Recomputes speaker label, colour and visibility of lines after renames, merges, corrections, mutes or forgets.</summary>
public static class TranscriptRelabeler
{
    public static TranscriptLine Relabel(TranscriptLine l, ISpeakerIdentifier speakers, SameLanguageMode sameLanguage, bool suppressed = false)
    {
        if (!speakers.Enabled) return l;
        int? id;
        bool uncertain;
        float confidence;
        if (speakers.Lookup(l.SegmentId) is { } assigned)
        {
            id = assigned.SpeakerId;
            uncertain = assigned.Uncertain;
            confidence = assigned.Similarity;
        }
        else
        {
            id = speakers.Resolve(l.SpeakerId);
            uncertain = l.SpeakerUncertain || id == null;
            confidence = l.SpeakerConfidence;
        }
        var (label, color) = speakers.Describe(id, uncertain);
        bool pending = l.SpeakerId == null && l.SpeakerLabel == "…" && speakers.Lookup(l.SegmentId) == null;
        if (pending) (label, color) = ("…", SpeakerRegistry.UnknownColor);
        bool muted = suppressed || (id is { } sp && !uncertain && speakers.IsMuted(sp));
        bool hidden = muted || (l.SameLanguage && sameLanguage == SameLanguageMode.Hide);
        return l with
        {
            SpeakerId = id,
            SpeakerUncertain = uncertain,
            SpeakerConfidence = confidence,
            SpeakerLabel = label,
            SpeakerColor = color,
            Hidden = hidden,
        };
    }

    public static void RelabelAll(TranscriptStore store, ISpeakerIdentifier speakers, SameLanguageMode sameLanguage) =>
        store.UpdateWhere(_ => true, l => Relabel(l, speakers, sameLanguage));
}
