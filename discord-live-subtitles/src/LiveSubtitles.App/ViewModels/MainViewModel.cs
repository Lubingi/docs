using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.App.Audio;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.App.Services;
using LiveSubtitles.App.Views;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Speakers;
using LiveSubtitles.Core.Text;
using LiveSubtitles.Core.Transcript;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private TranscriptAutoSaver? _autoSaver;
    private readonly SettingsStore _settingsStore;
    private readonly FileLog _log;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _fileTimer;
    private bool _updatingFilePosition;
    private OverlayWindow? _overlayWindow;
    private MainWindow? _mainWindow;

    public MainViewModel(SettingsStore settingsStore, FileLog log)
    {
        _settingsStore = settingsStore;
        _log = log;
        Settings = settingsStore.Load();
        Transcript = new TranscriptStore();
        Session = new SessionController(log, Transcript);
        Overlay = new OverlayViewModel(Settings.Overlay, SaveSettings);
        Debug = new DebugViewModel();
        History = new HistoryViewModel();
        InitGlossary();
        InitSettingsPage();
        InitSpeakers();
        Overlay.SpeakerClicked += line => SpeakerLabelClicked(line.SegmentId, line.SpeakerId);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _fileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fileTimer.Tick += (_, _) => UpdateFileTransport();
        _fileTimer.Start();

        _selectedSource = SourceOptions.First(o => o.Value == Settings.Source);
        _audioFilePath = Settings.LastAudioFile ?? "";
        _playFileToSpeakers = Settings.PlayFileToSpeakers;
        _callQuality = Settings.CallQualitySimulation;
        _showOriginal = Settings.ShowOriginal;
        _selectedSameLanguage = SameLanguageOptions.First(o => o.Value == Settings.SameLanguage);
        _selectedNoiseReduction = NoiseReductionOptions.FirstOrDefault(o => o.Value == Settings.NoiseReduction) ?? NoiseReductionOptions[0];
        _fallbackToDevice = Settings.FallbackToDeviceLoopback;
        _overlayVisible = Settings.Overlay.Visible;
        _hasApiKey = CredentialStore.HasApiKey();

        Transcript.LineAdded += l => UiThread.Post(() => OnLine(l));
        Transcript.LineUpdated += l => UiThread.Post(() => OnLine(l));
        Session.StatusChanged += s => UiThread.Post(() => OnStatus(s));
        Session.SegmentDebug += d => UiThread.Post(() => Debug.Apply(d));
        Session.RawEvent += e => UiThread.Post(() => Debug.Log(e));
        Session.SourceStatus += s => UiThread.Post(() => SourceStatus = s);
        Session.Level += (l, p) => UiThread.Post(() => { InputLevel = LevelToMeter(l); SpeechProbability = p; });
        Session.ServerError += e => UiThread.Post(() => LastError = "OpenAI: " + e);
        Session.AudioSent += ms => _usage?.AddAudio(ms);
        Session.SourceStopped += ex => UiThread.Post(() => { if (ex != null) LastError = "Audio source stopped: " + ex.Message; });
        log.Logged += (level, text) => { if (level >= LogLevel.Warning) UiThread.Post(() => Debug.Log(text)); };

        RefreshProcesses();
        RefreshDevices();
    }

    public AppSettings Settings { get; }
    public SessionController Session { get; }
    public TranscriptStore Transcript { get; }
    public OverlayViewModel Overlay { get; }
    public DebugViewModel Debug { get; }
    public HistoryViewModel History { get; }
    public ILog Log => _log;

    // ------------------------------------------------------------------ options

    public IReadOnlyList<Option<SourceKind>> SourceOptions { get; } = new[]
    {
        new Option<SourceKind>(SourceKind.Discord, "Discord (default)"),
        new Option<SourceKind>(SourceKind.App, "Any app (test mode: e.g. your browser)"),
        new Option<SourceKind>(SourceKind.File, "Audio file (test mode)"),
        new Option<SourceKind>(SourceKind.Device, "Whole output device (fallback)"),
    };

    public IReadOnlyList<Option<SameLanguageMode>> SameLanguageOptions { get; } = new[]
    {
        new Option<SameLanguageMode>(SameLanguageMode.ShowOriginal, "Show it as-is (original transcript)"),
        new Option<SameLanguageMode>(SameLanguageMode.Hide, "Hide it"),
    };

    public IReadOnlyList<Option<string>> NoiseReductionOptions { get; } = new[]
    {
        new Option<string>("", "Off (Discord already cleans voices)"),
        new Option<string>("near_field", "Near field (headset mics)"),
        new Option<string>("far_field", "Far field (laptop/room mics)"),
    };

    public ObservableCollection<ProcessChoice> Processes { get; } = new();
    public ObservableCollection<OutputDevice> Devices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppSource), nameof(IsFileSource), nameof(IsDeviceSource), nameof(IsDiscordSource), nameof(ShowDevicePicker))]
    private Option<SourceKind> _selectedSource;

    public bool IsDiscordSource => SelectedSource.Value == SourceKind.Discord;
    public bool IsAppSource => SelectedSource.Value == SourceKind.App;
    public bool IsFileSource => SelectedSource.Value == SourceKind.File;
    public bool IsDeviceSource => SelectedSource.Value == SourceKind.Device;
    public bool ShowDevicePicker => IsDeviceSource || ((IsDiscordSource || IsAppSource) && FallbackToDevice);

    [ObservableProperty] private ProcessChoice? _selectedProcess;
    [ObservableProperty] private OutputDevice? _selectedDevice;
    [ObservableProperty] private string _audioFilePath;
    [ObservableProperty] private bool _playFileToSpeakers;
    [ObservableProperty] private bool _callQuality;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowDevicePicker))] private bool _fallbackToDevice;
    [ObservableProperty] private bool _showOriginal;
    [ObservableProperty] private Option<SameLanguageMode> _selectedSameLanguage;
    [ObservableProperty] private Option<string> _selectedNoiseReduction;
    [ObservableProperty] private bool _overlayVisible;

    partial void OnSelectedSourceChanged(Option<SourceKind> value) { Settings.Source = value.Value; SaveSettings(); }
    partial void OnSelectedProcessChanged(ProcessChoice? value) { if (value != null) { Settings.AppProcessName = value.Name; SaveSettings(); } }
    partial void OnSelectedDeviceChanged(OutputDevice? value) { Settings.OutputDeviceId = value?.Id; SaveSettings(); }
    partial void OnAudioFilePathChanged(string value) { Settings.LastAudioFile = value; SaveSettings(); }
    partial void OnPlayFileToSpeakersChanged(bool value) { Settings.PlayFileToSpeakers = value; SaveSettings(); }
    partial void OnCallQualityChanged(bool value)
    {
        Settings.CallQualitySimulation = value;
        SaveSettings();
        if (Session.FileSource is { } f) f.Effect = CreateFileEffect(); // applies immediately while playing
    }
    partial void OnFallbackToDeviceChanged(bool value) { Settings.FallbackToDeviceLoopback = value; SaveSettings(); }
    partial void OnShowOriginalChanged(bool value) { Settings.ShowOriginal = value; SaveSettings(); }
    partial void OnSelectedSameLanguageChanged(Option<SameLanguageMode> value) { Settings.SameLanguage = value.Value; SaveSettings(); }
    partial void OnSelectedNoiseReductionChanged(Option<string> value) { Settings.NoiseReduction = value.Value; SaveSettings(); }
    partial void OnOverlayVisibleChanged(bool value)
    {
        Settings.Overlay.Visible = value;
        SaveSettings();
        UpdateTray();
        if (_overlayWindow == null) return;
        if (value) _overlayWindow.Show(); else _overlayWindow.Hide();
    }

    // ------------------------------------------------------------------ status

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StartStopLabel), nameof(CanEditSource))] private bool _isRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PauseLabel))] private bool _isPaused;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _connectionStatus = "Stopped";
    [ObservableProperty] private string _sourceStatus = "";
    [ObservableProperty] private string _latencyText = "";
    [ObservableProperty] private string _sentText = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private string _lastError = "";
    [ObservableProperty] private double _inputLevel;
    [ObservableProperty] private double _speechProbability;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ApiKeyStatus))] private bool _hasApiKey;

    public string StartStopLabel => IsRunning ? "■ Stop" : "▶ Start";
    public string PauseLabel => IsPaused ? "▶ Resume" : "❚❚ Pause";
    public bool CanEditSource => !IsRunning;
    public string ApiKeyStatus => HasApiKey ? "OpenAI API key saved in Windows Credential Manager." : "No OpenAI API key yet — click “Set API key”.";

    private void OnStatus(PipelineStatus s)
    {
        ConnectionStatus = s.Paused ? (_capPaused ? "Paused — spending cap reached" : "Paused") : s.ConnectionMessage;
        if (_usage != null)
            CostText = $"≈ ${_usage.CostUsd:0.000} this session ({_usage.Minutes:0.0} min)" + (_usage.CapUsd is { } cap ? $" · cap ${cap:0.00}" : "");
        Overlay.StatusText = s.Paused ? ConnectionStatus
            : s.Connection is ConnectionState.Reconnecting or ConnectionState.Connecting or ConnectionState.Failed ? s.ConnectionMessage
            : "";
        var parts = new List<string>();
        if (s.AvgTextLagMs is { } lag) parts.Add($"text lag {lag / 1000:0.0}s");
        if (s.LastFinalLatencyMs is { } fin) parts.Add($"final {fin / 1000:0.0}s after speech");
        LatencyText = string.Join(" · ", parts);
        SentText = $"{s.AudioSentSeconds / 60:0.0} min audio sent · {s.VadName}{(s.Reconnects > 0 ? $" · {s.Reconnects} reconnects" : "")}";
    }

    private static double LevelToMeter(float rms) => rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 60) / 60, 0, 1);

    // ------------------------------------------------------------------ file transport

    [ObservableProperty] private double _filePosition;
    [ObservableProperty] private double _fileDuration = 1;
    [ObservableProperty] private string _fileTimeText = "";
    [ObservableProperty] private bool _isFilePlaying;
    public bool HasFileSession => Session.FileSource != null;

    private void UpdateFileTransport()
    {
        var f = Session.FileSource;
        OnPropertyChanged(nameof(HasFileSession));
        if (f == null) return;
        _updatingFilePosition = true;
        FileDuration = Math.Max(1, f.Duration.TotalSeconds);
        FilePosition = f.Position.TotalSeconds;
        FileTimeText = $@"{f.Position:mm\:ss} / {f.Duration:mm\:ss}";
        IsFilePlaying = f.IsPlaying;
        _updatingFilePosition = false;
    }

    partial void OnFilePositionChanged(double value)
    {
        if (_updatingFilePosition) return;
        Session.FileSource?.Seek(TimeSpan.FromSeconds(value));
    }

    [RelayCommand]
    private void PlayPauseFile()
    {
        var f = Session.FileSource;
        if (f == null) return;
        if (f.IsPlaying) f.Pause(); else f.Play();
        UpdateFileTransport();
    }

    [RelayCommand]
    private void BrowseAudioFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|All files|*.*",
            Title = "Choose a test audio file",
        };
        if (dlg.ShowDialog() == true)
        {
            AudioFilePath = dlg.FileName;
            SelectedSource = SourceOptions.First(o => o.Value == SourceKind.File);
        }
    }

    // ------------------------------------------------------------------ sources

    [RelayCommand]
    public void RefreshProcesses()
    {
        var keep = SelectedProcess?.Name ?? Settings.AppProcessName;
        Processes.Clear();
        try
        {
            foreach (var p in ProcessTree.ListCandidates()) Processes.Add(p);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not list processes", ex);
        }
        SelectedProcess = Processes.FirstOrDefault(p => string.Equals(p.Name, keep, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    public void RefreshDevices()
    {
        Devices.Clear();
        Devices.Add(new OutputDevice("", "Default output device"));
        try
        {
            foreach (var d in DeviceLoopbackSource.ListDevices()) Devices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Warn("Could not list output devices", ex);
        }
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == (Settings.OutputDeviceId ?? "")) ?? Devices[0];
    }

    // ------------------------------------------------------------------ session

    [RelayCommand]
    public async Task StartStop()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (IsRunning) await StopSessionAsync();
            else await StartSessionAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IAudioEffect? CreateFileEffect() => Settings.CallQualitySimulation ? new CallQualitySimulator() : null;

    private async Task StartSessionAsync()
    {
        LastError = "";
        var key = CredentialStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetApiKey();
            key = CredentialStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(key)) return;
        }
        if (Settings.Source == SourceKind.Device && Settings.OutputDeviceId == "") Settings.OutputDeviceId = null;
        _capPaused = false;
        SaveNow();
        Overlay.Clear();
        _usage = new UsageTracker(Settings.TranslateUsdPerMinute, Settings.TranscribeUsdPerMinute, Settings.ShowOriginal, Settings.SpendingCapUsd);
        _usage.CapReached += () => UiThread.Post(OnCapReached);
        CostText = "";
        try
        {
            await Session.StartAsync(Settings, key, CreateSpeakerIdentifier(), CreateTextProcessor(), CreateFileEffect());
            IsRunning = true;
            IsPaused = false;
            if (Settings.AutoSaveTranscript)
            {
                var folder = string.IsNullOrWhiteSpace(Settings.AutoSaveFolder) ? DefaultTranscriptFolder : Settings.AutoSaveFolder!;
                _autoSaver ??= new TranscriptAutoSaver(Transcript, folder);
                Debug.Log("Saving transcript to " + _autoSaver.FilePath);
            }
            UpdateTray();
        }
        catch (Exception ex)
        {
            _log.Error("Could not start", ex);
            LastError = ex.Message;
            ConnectionStatus = "Stopped";
        }
    }


    private async Task StopSessionAsync()
    {
        ConnectionStatus = "Stopping…";
        await Session.StopAsync();
        IsRunning = false;
        IsPaused = false;
        ConnectionStatus = "Stopped";
        InputLevel = 0;
        SpeechProbability = 0;
        _autoSaver?.Dispose();
        _autoSaver = null;
        _registry?.SaveProfiles();
        UpdateTray();
    }

    private UsageTracker? _usage;
    private bool _capPaused;

    private void OnCapReached()
    {
        if (!IsRunning || IsPaused) return;
        _capPaused = true;
        IsPaused = true;
        Session.SetPaused(true);
        LastError = $"Spending cap of ${Settings.SpendingCapUsd:0.00} reached — paused. Raise or clear the cap in Settings to continue.";
        _log.Warn("Spending cap reached; paused");
        UpdateTray();
    }

    [RelayCommand]
    public void TogglePause()
    {
        if (!IsRunning) return;
        if (IsPaused && _usage != null)
        {
            _usage.CapUsd = Settings.SpendingCapUsd is > 0 ? Settings.SpendingCapUsd : null;
            if (_usage.IsOverCap)
            {
                LastError = "Still over the spending cap. Raise or clear it in Settings first.";
                return;
            }
            _usage.ResetCapNotification();
            _capPaused = false;
            LastError = "";
        }
        IsPaused = !IsPaused;
        Session.SetPaused(IsPaused);
        UpdateTray();
    }

    [RelayCommand]
    public void ToggleOverlay() => OverlayVisible = !OverlayVisible;

    [RelayCommand]
    public void ToggleClickThrough() => Overlay.ClickThrough = !Overlay.ClickThrough;

    [RelayCommand]
    private void ResetOverlayPosition() => _overlayWindow?.ResetPosition();

    [RelayCommand]
    private void SetApiKey()
    {
        var dlg = new ApiKeyWindow(Settings.Model) { Owner = _mainWindow };
        dlg.ShowDialog();
        HasApiKey = CredentialStore.HasApiKey();
    }

    [RelayCommand]
    private void ClearDebug() => Debug.Clear();

    [RelayCommand]
    private void OpenLogFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", _log.LogDirectory); } catch { }
    }

    // ------------------------------------------------------------------ transcript

    private void OnLine(TranscriptLine line)
    {
        Overlay.Apply(line);
        History.Apply(line);
    }

    // ------------------------------------------------------------------ windows & settings

    public void AttachWindows(MainWindow main)
    {
        _mainWindow = main;
        _overlayWindow = new OverlayWindow(Overlay);
        if (OverlayVisible) _overlayWindow.Show();
        _tray = new TrayIcon(main.ShowFromTray, () => StartStopCommand.Execute(null), TogglePause, ToggleOverlay, ToggleClickThrough,
            () => _ = main.ExitAsync());
        Overlay.PropertyChanged += (_, _) => UpdateTray();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
        _hotkeys = new HotkeyManager(hwnd);
        RegisterHotkeys();
        UpdateTray();
    }

    public void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        var h = Settings.Hotkeys;
        var problems = _hotkeys.Register(new (string, string, Action)[]
        {
            ("Start/stop", h.StartStop, () => StartStopCommand.Execute(null)),
            ("Pause", h.Pause, TogglePause),
            ("Show/hide overlay", h.ToggleOverlay, ToggleOverlay),
            ("Click-through", h.ToggleClickThrough, ToggleClickThrough),
        });
        HotkeyStatus = problems.Count == 0 ? "All hotkeys registered." : string.Join("\n", problems);
    }

    [ObservableProperty] private string _hotkeyStatus = "";

    private void UpdateTray() => _tray?.Update(IsRunning, IsPaused, OverlayVisible, Overlay.ClickThrough, ConnectionStatus);

    public bool MinimizeToTray => Settings.MinimizeToTray;
    public void NotifyMinimizedToTray() => _tray?.ShowMinimizedHint();

    public void SaveSettings()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception ex) { _log.Warn("Could not save settings", ex); }
    }

    public async Task ShutdownAsync()
    {
        if (IsRunning) await StopSessionAsync();
        SaveNow();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _overlayWindow?.Close();
    }
}
