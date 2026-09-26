using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.App.ViewModels;

public partial class HistoryRow : ObservableObject
{
    public HistoryRow(int segmentId) => SegmentId = segmentId;
    public int SegmentId { get; }
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private int? _speakerId;
    [ObservableProperty] private string _speaker = "";
    [ObservableProperty] private Brush _speakerBrush = Brushes.Black;
    [ObservableProperty] private string _translation = "";
    [ObservableProperty] private string _original = "";
    [ObservableProperty] private bool _isFinal;
    [ObservableProperty] private string _confidence = "";
}

/// <summary>Scrollable transcript log (timestamps, speaker, original, translation). Kept in memory only.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly Dictionary<int, HistoryRow> _rows = new();

    public ObservableCollection<HistoryRow> Rows { get; } = new();

    [ObservableProperty] private HistoryRow? _selected;
    [ObservableProperty] private bool _autoScroll = true;

    public event Action<HistoryRow>? RowAdded;

    public void Apply(TranscriptLine line)
    {
        bool show = !line.Hidden && (line.HasText || line.Original.Length > 0);
        if (!_rows.TryGetValue(line.SegmentId, out var row))
        {
            if (!show) return;
            row = new HistoryRow(line.SegmentId);
            _rows[line.SegmentId] = row;
            int index = Rows.Count;
            while (index > 0 && Rows[index - 1].SegmentId > line.SegmentId) index--;
            Rows.Insert(index, row);
            RowAdded?.Invoke(row);
        }
        else if (!show)
        {
            Rows.Remove(row);
            _rows.Remove(line.SegmentId);
            return;
        }
        row.Time = line.StartedAt.ToLocalTime().ToString("HH:mm:ss");
        row.SpeakerId = line.SpeakerId;
        row.Speaker = line.SpeakerLabel;
        var color = OverlayViewModel.ParseColor(line.SpeakerColor);
        // Darken light speaker colours so they're readable on the white history background.
        row.SpeakerBrush = new SolidColorBrush(Color.FromRgb((byte)(color.R * 0.7), (byte)(color.G * 0.7), (byte)(color.B * 0.7)));
        row.Translation = line.DisplayText + (line.IsFinal ? "" : " …");
        row.Original = line.ShowsOriginalAsText ? "" : line.Original;
        row.IsFinal = line.IsFinal;
        row.Confidence = line.SpeakerId == null ? "" : $"{line.SpeakerConfidence:0.00}";
    }

    public void Clear()
    {
        _rows.Clear();
        Rows.Clear();
    }
}
