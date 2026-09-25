using System.Globalization;
using System.Text;

namespace LiveSubtitles.Core.Transcript;

public static class TranscriptExporter
{
    public static IReadOnlyList<TranscriptLine> Exportable(IEnumerable<TranscriptLine> lines) =>
        lines.Where(l => l.HasText || l.Original.Length > 0).OrderBy(l => l.StartedAt).ToList();

    /// <summary>Plain text: "[14:03:22] Emre: translation" plus the original on the next line.</summary>
    public static string ToText(IEnumerable<TranscriptLine> lines, bool includeOriginal = true)
    {
        var sb = new StringBuilder();
        foreach (var l in Exportable(lines))
        {
            sb.Append('[').Append(l.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ");
            if (l.SpeakerLabel.Length > 0) sb.Append(l.SpeakerLabel).Append(": ");
            sb.AppendLine(l.DisplayText.Length > 0 ? l.DisplayText : l.Original);
            if (includeOriginal && l.Original.Length > 0 && !l.SameLanguage && l.DisplayText.Length > 0)
                sb.Append("           (").Append(l.Original).AppendLine(")");
        }
        return sb.ToString();
    }

    /// <summary>SubRip subtitles. Times are relative to the first exported line, so files from long sessions line up with a recording started at the same moment.</summary>
    public static string ToSrt(IEnumerable<TranscriptLine> lines, bool includeOriginal = false)
    {
        var list = Exportable(lines);
        var sb = new StringBuilder();
        if (list.Count == 0) return "";
        var origin = list[0].StartedAt;
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            var start = l.StartedAt - origin;
            var duration = TimeSpan.FromMilliseconds(Math.Max(1500, l.StreamEndMs - l.StreamStartMs + 700));
            var end = start + duration;
            if (i + 1 < list.Count)
            {
                var next = list[i + 1].StartedAt - origin;
                if (end > next && next > start + TimeSpan.FromMilliseconds(300)) end = next;
            }
            sb.Append(i + 1).AppendLine();
            sb.Append(Srt(start)).Append(" --> ").Append(Srt(end)).AppendLine();
            var text = (l.SpeakerLabel.Length > 0 ? l.SpeakerLabel + ": " : "") + (l.DisplayText.Length > 0 ? l.DisplayText : l.Original);
            sb.AppendLine(text);
            if (includeOriginal && l.Original.Length > 0 && !l.SameLanguage && l.DisplayText.Length > 0) sb.AppendLine(l.Original);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Srt(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
}
