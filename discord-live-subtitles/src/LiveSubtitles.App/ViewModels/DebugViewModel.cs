using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveSubtitles.Core.Pipeline;

namespace LiveSubtitles.App.ViewModels;

public partial class SegmentRow : ObservableObject
{
    public SegmentRow(int id) => Id = id;
    public int Id { get; }
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _speaker = "";
    [ObservableProperty] private string _confidence = "";
    [ObservableProperty] private string _firstText = "";
    [ObservableProperty] private string _final = "";
    [ObservableProperty] private string _lag = "";
    [ObservableProperty] private string _translation = "";
    [ObservableProperty] private string _original = "";
}

/// <summary>Debug panel: every detected speech segment, speaker assignment + confidence, latency and raw text.</summary>
public partial class DebugViewModel : ObservableObject
{
    private const int MaxRows = 500;
    private const int MaxLog = 1000;
    private readonly Dictionary<int, SegmentRow> _rows = new();

    public ObservableCollection<SegmentRow> Segments { get; } = new();
    public ObservableCollection<string> RawLog { get; } = new();

    [ObservableProperty] private bool _logRawEvents = true;

    public void Apply(SegmentDebugInfo d)
    {
        if (!_rows.TryGetValue(d.SegmentId, out var row))
        {
            row = new SegmentRow(d.SegmentId);
            _rows[d.SegmentId] = row;
            Segments.Insert(0, row);
            if (Segments.Count > MaxRows)
            {
                var old = Segments[^1];
                Segments.RemoveAt(Segments.Count - 1);
                _rows.Remove(old.Id);
            }
        }
        row.Start = TimeSpan.FromMilliseconds(d.StreamStartMs).ToString(@"mm\:ss\.f");
        row.Duration = $"{d.DurationMs / 1000:0.0}s";
        row.Status = d.Status;
        row.Speaker = d.SpeakerLabel;
        row.Confidence = d.Similarity > 0 ? $"{d.Similarity:0.00} (next {d.SecondBest:0.00}){(d.Uncertain ? " ?" : "")}" : "";
        row.FirstText = d.FirstTextLatencyMs is { } f ? $"{f:0} ms" : "";
        row.Final = d.FinalLatencyMs is { } fl ? $"{fl:0} ms" : "";
        row.Lag = d.TextLagMs is { } l ? $"{l:0} ms" : "";
        row.Translation = d.RawTranslation;
        row.Original = d.RawOriginal;
    }

    public void Log(string line)
    {
        if (!LogRawEvents) return;
        RawLog.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (RawLog.Count > MaxLog) RawLog.RemoveAt(RawLog.Count - 1);
    }

    public void Clear()
    {
        _rows.Clear();
        Segments.Clear();
        RawLog.Clear();
    }
}
