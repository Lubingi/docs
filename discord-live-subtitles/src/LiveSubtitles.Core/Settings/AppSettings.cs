using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSubtitles.Core.Pipeline;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.Core.Settings;

public enum SourceKind { Discord, App, Device, File }

/// <summary>All user settings. Stored as JSON in %APPDATA%\LiveSubtitles\settings.json. The API key is NOT stored here.</summary>
public sealed class AppSettings
{
    // --- audio source
    public SourceKind Source { get; set; } = SourceKind.Discord;
    /// <summary>Executable name (without .exe) for "Any app" mode, e.g. "chrome".</summary>
    public string? AppProcessName { get; set; }
    /// <summary>Render device id for output-device loopback, null = default device.</summary>
    public string? OutputDeviceId { get; set; }
    /// <summary>If per-process capture fails, capture the whole default output device instead.</summary>
    public bool FallbackToDeviceLoopback { get; set; } = true;
    public string? LastAudioFile { get; set; }
    public bool PlayFileToSpeakers { get; set; } = true;
    public bool CallQualitySimulation { get; set; }

    // --- translation
    public string OutputLanguage { get; set; } = "en";
    public string Model { get; set; } = TranslationProtocol.DefaultModel;
    public string TranscriptionModel { get; set; } = TranslationProtocol.DefaultTranscriptionModel;
    public bool ShowOriginal { get; set; } = true;
    public SameLanguageMode SameLanguage { get; set; } = SameLanguageMode.ShowOriginal;
    /// <summary>"near_field", "far_field" or "" (off).</summary>
    public string NoiseReduction { get; set; } = "";
    public bool SendContinuously { get; set; }

    // --- speakers
    public bool SpeakerIdEnabled { get; set; } = true;
    public DiarizationSettings Diarization { get; set; } = new();

    // --- overlay
    public OverlaySettings Overlay { get; set; } = new();

    // --- hotkeys
    public HotkeySettings Hotkeys { get; set; } = new();

    // --- glossary
    public List<GlossaryEntry> Glossary { get; set; } = new();

    // --- transcript
    public bool AutoSaveTranscript { get; set; }
    public string? AutoSaveFolder { get; set; }

    // --- cost
    public double TranslateUsdPerMinute { get; set; } = 0.034;
    public double TranscribeUsdPerMinute { get; set; } = 0.017;
    public double? SpendingCapUsd { get; set; }

    // --- app
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    // --- advanced tuning
    public AdvancedSettings Advanced { get; set; } = new();

    public PipelineOptions ToPipelineOptions() => new()
    {
        ShowOriginal = ShowOriginal,
        SameLanguage = SameLanguage,
        Segmenter = new SegmenterOptions
        {
            Threshold = Advanced.VadThreshold,
            NegativeThreshold = Math.Max(0.05f, Advanced.VadThreshold - 0.15f),
            MinSilenceMs = Advanced.MinSilenceMs,
            TailMs = Advanced.TailMs,
            MaxSegmentMs = Advanced.MaxSegmentMs,
            SendContinuously = SendContinuously,
        },
        Attribution = new AttributionOptions
        {
            CloseLagMs = Advanced.CloseLagMs,
            FinalizeIdleMs = Advanced.FinalizeIdleMs,
        },
    };

    public TranslationSessionConfig ToSessionConfig() => new()
    {
        Model = string.IsNullOrWhiteSpace(Model) ? TranslationProtocol.DefaultModel : Model,
        OutputLanguage = OutputLanguage,
        TranscriptionModel = ShowOriginal ? TranscriptionModel : null,
        NoiseReduction = string.IsNullOrWhiteSpace(NoiseReduction) ? null : NoiseReduction,
    };
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public bool ClickThrough { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 26;
    public double BackgroundOpacity { get; set; } = 0.55;
    public bool ShowOriginal { get; set; } = true;
    public double OriginalScale { get; set; } = 0.68;
    public int MaxLines { get; set; } = 3;
    public double FadeSeconds { get; set; } = 8;
    /// <summary>Window placement in device-independent pixels; null = bottom centre of the primary screen.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 260;
}

public sealed class HotkeySettings
{
    public string StartStop { get; set; } = "Ctrl+Alt+S";
    public string Pause { get; set; } = "Ctrl+Alt+P";
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+O";
    public string ToggleClickThrough { get; set; } = "Ctrl+Alt+T";
}

public enum GlossaryMode { Replace, KeepAsIs }

/// <summary>
/// A glossary rule applied locally to subtitle text (the translation API has no prompt/glossary input).
/// Replace: every match of <see cref="Term"/> becomes <see cref="Replacement"/>.
/// KeepAsIs: <see cref="Term"/> is a name/word that must appear unchanged; near-miss spellings in the output
/// (e.g. "Emir" for "Emre") and any listed <see cref="Aliases"/> are corrected to it.
/// </summary>
public sealed class GlossaryEntry
{
    public GlossaryMode Mode { get; set; } = GlossaryMode.Replace;
    public string Term { get; set; } = "";
    public string Replacement { get; set; } = "";
    /// <summary>Comma-separated other spellings/translations to map onto the term.</summary>
    public string Aliases { get; set; } = "";
    public bool ApplyToOriginal { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class DiarizationSettings
{
    /// <summary>Cosine similarity needed to assign a segment to an existing voice.</summary>
    public float MatchThreshold { get; set; } = 0.50f;
    /// <summary>Below this similarity to everyone, a segment may start a new voice.</summary>
    public float NewSpeakerThreshold { get; set; } = 0.32f;
    /// <summary>Two voices whose profiles are this similar are merged automatically.</summary>
    public float MergeThreshold { get; set; } = 0.62f;
    /// <summary>Segments shorter than this are never used to create a new voice.</summary>
    public int MinNewSpeakerMs { get; set; } = 1500;
    public int MaxSpeakers { get; set; } = 12;
}

public sealed class AdvancedSettings
{
    public float VadThreshold { get; set; } = 0.5f;
    public int MinSilenceMs { get; set; } = 480;
    public int TailMs { get; set; } = 1500;
    public int MaxSegmentMs { get; set; } = 15000;
    public int CloseLagMs { get; set; } = 1600;
    public int FinalizeIdleMs { get; set; } = 1800;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // A corrupt file must not stop the app from starting; keep a copy for inspection.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json)!;
}
