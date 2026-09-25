using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.App.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<string> FontFamilies { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<Option<string>> OutputLanguages { get; } =
        TranslationProtocol.OutputLanguages.Select(kv => new Option<string>(kv.Key, kv.Value)).OrderBy(o => o.Value == "en" ? "" : o.Label).ToList();

    private void InitSettingsPage()
    {
        FontFamilies = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
    }

    /// <summary>Called (deferred) whenever something on the Settings tab changes.</summary>
    public void ApplySettings()
    {
        Overlay.RefreshAppearance();
        RegisterHotkeys();
        SaveSettings();
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        Settings.Hotkeys = new Core.Settings.HotkeySettings();
        OnPropertyChanged(nameof(Settings));
        RegisterHotkeys();
        SaveSettings();
    }

    [RelayCommand]
    private void ResetAdvanced()
    {
        Settings.Advanced = new Core.Settings.AdvancedSettings();
        Settings.Diarization = new Core.Settings.DiarizationSettings();
        OnPropertyChanged(nameof(Settings));
        SaveSettings();
    }
}
