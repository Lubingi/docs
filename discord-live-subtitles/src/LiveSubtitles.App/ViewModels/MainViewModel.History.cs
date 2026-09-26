using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.App.ViewModels;

public partial class MainViewModel
{
    private static string DefaultTranscriptFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveSubtitles transcripts");

    public bool AutoSaveTranscript
    {
        get => Settings.AutoSaveTranscript;
        set { Settings.AutoSaveTranscript = value; OnPropertyChanged(); SaveSettings(); }
    }

    public string AutoSaveFolder => string.IsNullOrWhiteSpace(Settings.AutoSaveFolder) ? DefaultTranscriptFolder : Settings.AutoSaveFolder!;

    [RelayCommand]
    private void ChooseAutoSaveFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for saved transcripts", InitialDirectory = AutoSaveFolder };
        if (dlg.ShowDialog() == true)
        {
            Settings.AutoSaveFolder = dlg.FolderName;
            OnPropertyChanged(nameof(AutoSaveFolder));
            SaveSettings();
        }
    }

    [RelayCommand]
    private void ExportTxt() => Export("Text file|*.txt", "txt", TranscriptExporter.ToText(Transcript.Snapshot().Where(l => !l.Hidden)));

    [RelayCommand]
    private void ExportSrt() => Export("SubRip subtitles|*.srt", "srt", TranscriptExporter.ToSrt(Transcript.Snapshot().Where(l => !l.Hidden)));

    private void Export(string filter, string ext, string content)
    {
        if (content.Length == 0)
        {
            LastError = "Nothing to export yet.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = $"transcript-{DateTime.Now:yyyyMMdd-HHmm}.{ext}",
            InitialDirectory = AutoSaveFolder is { } f && Directory.Exists(f) ? f : null,
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Debug.Log("Exported " + dlg.FileName);
    }

    [RelayCommand]
    private void CopySelectedLine()
    {
        if (History.Selected is not { } row) return;
        System.Windows.Clipboard.SetText($"{row.Time} {row.Speaker}: {row.Translation}\n{row.Original}".Trim());
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (IsRunning)
        {
            LastError = "Stop the session before clearing the history.";
            return;
        }
        Transcript.Clear();
        History.Clear();
        Overlay.Clear();
    }
}
