using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.App.Services;
using LiveSubtitles.App.Views;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.App.ViewModels;

public partial class SpeakerRow : ObservableObject
{
    private readonly Action<int, bool> _setMuted;
    private bool _loading;

    public SpeakerRow(int id, Action<int, bool> setMuted)
    {
        Id = id;
        _setMuted = setMuted;
    }

    public int Id { get; }
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private Brush _brush = Brushes.Gray;
    [ObservableProperty] private bool _isNamed;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private string _stats = "";

    public string SavedText => IsNamed ? "remembered" : "this session";
    partial void OnIsNamedChanged(bool value) => OnPropertyChanged(nameof(SavedText));
    partial void OnMutedChanged(bool value) { if (!_loading) _setMuted(Id, value); }

    public void Update(SpeakerInfo s)
    {
        _loading = true;
        Label = s.Label;
        Brush = new SolidColorBrush(OverlayViewModel.ParseColor(s.Color));
        IsNamed = s.IsNamed;
        Muted = s.Muted;
        Stats = s.Segments == 0 ? "not heard yet" : $"{s.Segments} lines · {TimeSpan.FromSeconds(s.SpeechSeconds):m\\:ss} talking";
        _loading = false;
    }
}

public partial class MainViewModel
{
    private SpeakerRegistry? _registry;
    private SpeakerEmbedder? _embedder;
    private bool _embedderFailed;
    private readonly object _embedderLock = new();

    public ObservableCollection<SpeakerRow> Speakers { get; } = new();

    [ObservableProperty] private string _speakerModelStatus = "";

    public bool SpeakerIdEnabled
    {
        get => Settings.SpeakerIdEnabled;
        set { Settings.SpeakerIdEnabled = value; OnPropertyChanged(); SaveSettings(); }
    }

    public SpeakerRegistry Registry => _registry ??= CreateRegistry();

    private SpeakerRegistry CreateRegistry()
    {
        var registry = new SpeakerRegistry(Embed, Settings.Diarization, new VoiceProfileStore(AppPaths.VoicesFile));
        registry.SpeakersChanged += () =>
        {
            TranscriptRelabeler.RelabelAll(Transcript, registry, Settings.SameLanguage);
            UiThread.Post(RefreshSpeakers);
        };
        SpeakerModelStatus = File.Exists(AppPaths.SpeakerModel)
            ? "Voices are recognised locally with the CAM++ speaker model. Only voice embeddings (numbers) are stored, never audio."
            : "Speaker model missing — speaker labels are disabled. Rebuild or reinstall to restore models\\campplus_voxceleb_16k.onnx.";
        return registry;
    }

    /// <summary>Loads the 28 MB speaker model on first use (on the speaker worker thread).</summary>
    private float[]? Embed(float[] audio16k)
    {
        lock (_embedderLock)
        {
            if (_embedder == null && !_embedderFailed)
            {
                try { _embedder = new SpeakerEmbedder(AppPaths.SpeakerModel); }
                catch (Exception ex)
                {
                    _embedderFailed = true;
                    _log.Error("Could not load the speaker model; speakers will show as ?", ex);
                }
            }
        }
        return _embedder?.Embed(audio16k);
    }

    private ISpeakerIdentifier CreateSpeakerIdentifier()
    {
        if (!Settings.SpeakerIdEnabled || !File.Exists(AppPaths.SpeakerModel)) return new NoSpeakerIdentifier();
        Registry.Settings = Settings.Diarization;
        return Registry;
    }

    private void InitSpeakers()
    {
        _ = Registry; // load remembered voices so they're listed before the first session
        RefreshSpeakers();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (IsRunning) RefreshSpeakers(); };
        timer.Start();
    }

    public void RefreshSpeakers()
    {
        var snapshot = Registry.Snapshot();
        var ids = snapshot.Select(s => s.Id).ToHashSet();
        foreach (var gone in Speakers.Where(r => !ids.Contains(r.Id)).ToList()) Speakers.Remove(gone);
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = Speakers.FirstOrDefault(r => r.Id == snapshot[i].Id);
            if (row == null)
            {
                row = new SpeakerRow(snapshot[i].Id, (id, muted) => Registry.SetMuted(id, muted));
                Speakers.Insert(Math.Min(i, Speakers.Count), row);
            }
            else if (Speakers.IndexOf(row) != i) Speakers.Move(Speakers.IndexOf(row), Math.Min(i, Speakers.Count - 1));
            row.Update(snapshot[i]);
        }
    }

    private Window? Owner => _mainWindow is { IsVisible: true } ? _mainWindow : null;

    private bool Confirm(string text, string title) =>
        (Owner != null ? MessageBox.Show(Owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                       : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    [RelayCommand]
    public void RenameSpeaker(int? speakerId)
    {
        if (speakerId is not { } id) return;
        var current = Registry.Snapshot().FirstOrDefault(s => s.Id == id);
        if (current == null) return;
        var name = PromptWindow.AskText(Owner, "Rename speaker",
            $"New name for {current.Label}. Named voices are remembered and labelled automatically in future calls (only a voice fingerprint is saved, never audio). Leave empty to go back to a numbered label.",
            current.IsNamed ? current.Label : "");
        if (name != null) Registry.Rename(id, name);
    }

    [RelayCommand]
    private void RenameSpeakerRow(SpeakerRow? row) => RenameSpeaker(row?.Id);

    [RelayCommand]
    private void MergeSpeakerRow(SpeakerRow? row)
    {
        if (row == null) return;
        var targets = Registry.Snapshot().Where(s => s.Id != row.Id).Select(s => new ChoiceItem(s.Id, s.Label)).ToList();
        if (targets.Count == 0) return;
        var choice = PromptWindow.AskChoice(Owner, "Merge speakers", $"{row.Label} is really the same person as…", targets);
        if (choice?.Value is int into) Registry.Merge(row.Id, into);
    }

    [RelayCommand]
    private void ForgetSpeakerRow(SpeakerRow? row)
    {
        if (row == null) return;
        if (Confirm($"Forget {row.Label}? Their saved voice profile is deleted and their lines show “?”.", "Forget speaker"))
            Registry.Forget(row.Id);
    }

    [RelayCommand]
    private void ClearAllSpeakers()
    {
        if (Confirm("Forget all voices, including remembered names? This deletes voices.json.", "Clear all speakers"))
            Registry.ClearAll();
    }

    /// <summary>Choices for "assign this line to…" menus.</summary>
    public IReadOnlyList<ChoiceItem> SpeakerChoices() =>
        Registry.Snapshot().Select(s => new ChoiceItem(s.Id, s.Label)).Append(new ChoiceItem(null, "New speaker")).ToList();

    public void ReassignLine(int segmentId, int? speakerId)
    {
        if (Registry.Lookup(segmentId) == null)
        {
            LastError = "That line has no voice sample (too short or speaker labels were off), so it can't be moved.";
            return;
        }
        Registry.Reassign(segmentId, speakerId);
    }

    public void MergeSpeaker(int fromId, int intoId) => Registry.Merge(fromId, intoId);

    /// <summary>Overlay/history click on a name: rename it, or for "?" pick who it was.</summary>
    private void SpeakerLabelClicked(int segmentId, int? speakerId)
    {
        if (!Settings.SpeakerIdEnabled) return;
        var lookup = Registry.Lookup(segmentId);
        if (speakerId != null && lookup is { Uncertain: false }) { RenameSpeaker(speakerId); return; }
        if (lookup == null) return;
        var choice = PromptWindow.AskChoice(Owner, "Who said this?", "Pick the speaker for this line. The app learns from the correction.", SpeakerChoices());
        if (choice != null) ReassignLine(segmentId, choice.Value as int?);
    }

    public void HistorySpeakerClicked(HistoryRow row) => SpeakerLabelClicked(row.SegmentId, row.SpeakerId);
}
