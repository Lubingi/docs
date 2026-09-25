using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.App.ViewModels;

public partial class OverlayLineViewModel : ObservableObject
{
    public OverlayLineViewModel(int segmentId) => SegmentId = segmentId;

    public int SegmentId { get; }
    public int? SpeakerId { get; set; }
    /// <summary>Consecutive lines by the same speaker shown together (segment id → line).</summary>
    public SortedDictionary<int, TranscriptLine> Members { get; } = new();
    public int LastSegmentId => Members.Count == 0 ? SegmentId : Members.Keys.Max();
    public DateTime LastChange { get; set; } = DateTime.UtcNow;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _nameSeparator = "";
    [ObservableProperty] private Brush _nameBrush = Brushes.White;
    [ObservableProperty] private Brush _textBrush = Brushes.White;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _original = "";
    [ObservableProperty] private bool _showOriginal;
    [ObservableProperty] private bool _isFinal;
    [ObservableProperty] private double _opacity = 1;

    public System.Windows.FontStyle TextStyle => IsFinal ? System.Windows.FontStyles.Normal : System.Windows.FontStyles.Italic;
    partial void OnIsFinalChanged(bool value) => OnPropertyChanged(nameof(TextStyle));
}

/// <summary>Keeps the last few subtitle lines for the overlay: streams partial text, then final text, then fades out.</summary>
public partial class OverlayViewModel : ObservableObject
{
    private readonly OverlaySettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Action _saveSettings;

    public OverlayViewModel(OverlaySettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _clickThrough = settings.ClickThrough;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Fade();
        _timer.Start();
    }

    public ObservableCollection<OverlayLineViewModel> Lines { get; } = new();
    public OverlaySettings Placement => _settings;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMovable))] private bool _clickThrough;
    /// <summary>Small status pill shown on the overlay (reconnecting, paused, cap reached); empty when all is well.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string _statusText = "";
    public bool HasStatus => StatusText.Length > 0;
    public bool IsMovable => !ClickThrough;

    public string FontFamily => _settings.FontFamily;
    public double FontSize => _settings.FontSize;
    public double OriginalFontSize => Math.Max(9, _settings.FontSize * _settings.OriginalScale);
    public double BackgroundOpacity => _settings.BackgroundOpacity;

    /// <summary>Raised when the user clicks a speaker name (to rename it).</summary>
    public event Action<OverlayLineViewModel>? SpeakerClicked;

    [RelayCommand]
    private void OnSpeakerClicked(OverlayLineViewModel line) => SpeakerClicked?.Invoke(line);

    partial void OnClickThroughChanged(bool value)
    {
        _settings.ClickThrough = value;
        _saveSettings();
    }

    public void RefreshAppearance()
    {
        OnPropertyChanged(nameof(FontFamily));
        OnPropertyChanged(nameof(FontSize));
        OnPropertyChanged(nameof(OriginalFontSize));
        OnPropertyChanged(nameof(BackgroundOpacity));
        foreach (var l in Lines) l.ShowOriginal = _settings.ShowOriginal && l.Original.Length > 0;
        TrimToMax();
    }

    public void RememberPlacement(double left, double top, double width, double height)
    {
        _settings.Left = left;
        _settings.Top = top;
        _settings.Width = width;
        _settings.Height = height;
        _saveSettings();
    }

    public void Clear() => Lines.Clear();

    /// <summary>Apply a transcript update (call on the UI thread).</summary>
    private const int MaxMergedChars = 220;

    public void Apply(TranscriptLine line)
    {
        var group = Lines.FirstOrDefault(l => l.Members.ContainsKey(line.SegmentId));
        if (line.Hidden || !line.HasText)
        {
            if (group == null) return;
            group.Members.Remove(line.SegmentId);
            if (group.Members.Count == 0) Lines.Remove(group);
            else Render(group);
            return;
        }
        if (group == null)
        {
            // Pauses split one person's speech into several segments; show them as one subtitle line.
            var last = Lines.LastOrDefault();
            bool merge = last != null
                         && last.LastSegmentId < line.SegmentId
                         && line.SegmentId - last.LastSegmentId <= 2
                         && IsNamedSpeaker(line) && last.Members.Values.All(m => m.SpeakerLabel == line.SpeakerLabel)
                         && (DateTime.UtcNow - last.LastChange).TotalSeconds < _settings.FadeSeconds
                         && last.Text.Length + line.DisplayText.Length < MaxMergedChars;
            if (merge) group = last;
            else
            {
                group = new OverlayLineViewModel(line.SegmentId);
                int index = 0;
                while (index < Lines.Count && Lines[index].SegmentId < line.SegmentId) index++;
                Lines.Insert(index, group);
            }
        }
        group!.Members[line.SegmentId] = line;
        Render(group);
        TrimToMax();
    }

    private static bool IsNamedSpeaker(TranscriptLine l) => l.SpeakerLabel.Length > 0 && l.SpeakerLabel is not ("?" or "…") && !l.SpeakerUncertain;

    private void Render(OverlayLineViewModel group)
    {
        var members = group.Members.Values.ToList();
        var first = members[0];
        var color = ParseColor(first.SpeakerColor);
        group.SpeakerId = first.SpeakerId;
        group.Name = first.SpeakerLabel;
        group.NameSeparator = first.SpeakerLabel.Length > 0 ? ": " : "";
        group.NameBrush = new SolidColorBrush(color);
        group.TextBrush = new SolidColorBrush(Blend(color, Colors.White, 0.35));
        group.Text = string.Join(" ", members.Select(m => m.DisplayText).Where(t => t.Length > 0));
        group.Original = string.Join(" ", members.Where(m => !m.SameLanguage).Select(m => m.Original).Where(t => t.Length > 0));
        group.ShowOriginal = _settings.ShowOriginal && group.Original.Length > 0;
        bool isFinal = members.All(m => m.IsFinal);
        if (!isFinal || group.IsFinal != isFinal || group.Opacity < 1) group.LastChange = DateTime.UtcNow;
        group.IsFinal = isFinal;
        group.Opacity = 1;
    }

    private void TrimToMax()
    {
        int max = Math.Max(1, _settings.MaxLines);
        while (Lines.Count > max) Lines.RemoveAt(0);
    }

    private void Fade()
    {
        if (_settings.FadeSeconds <= 0) return;
        var now = DateTime.UtcNow;
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            var l = Lines[i];
            if (!l.IsFinal) continue;
            double age = (now - l.LastChange).TotalSeconds - _settings.FadeSeconds;
            if (age <= 0) continue;
            l.Opacity = Math.Max(0, 1 - age / 1.0); // one-second fade
            if (l.Opacity <= 0) Lines.RemoveAt(i);
        }
    }

    internal static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.White; }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
