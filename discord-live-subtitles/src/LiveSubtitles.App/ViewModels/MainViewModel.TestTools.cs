using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveSubtitles.App.Services;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Settings;
using LiveSubtitles.Core.Testing;

namespace LiveSubtitles.App.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _generateCts;

    public IReadOnlyList<string> DialogueLanguages { get; } = DialogueGenerator.Languages;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBuiltInScript))] private string _dialogueLanguage = "Turkish";
    [ObservableProperty] private int _dialogueSpeakers = 3;
    [ObservableProperty] private int _dialogueLines = 10;
    [ObservableProperty] private string _dialogueTopic = "planning tonight's game session";
    [ObservableProperty] private bool _dialogueWriteScript;
    [ObservableProperty] private string _dialogueScriptModel = "gpt-5-mini";
    [ObservableProperty] private string _dialogueStatus = "";
    [ObservableProperty] private string _dialogueScript = "";
    [ObservableProperty] private string? _lastDialogueFile;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GenerateLabel))] private bool _isGenerating;

    public bool HasBuiltInScript => DialogueGenerator.BuiltInScripts.ContainsKey(DialogueLanguage);
    public string GenerateLabel => IsGenerating ? "Cancel" : "Generate test dialogue";

    [RelayCommand]
    private async Task GenerateDialogue()
    {
        if (IsGenerating)
        {
            _generateCts?.Cancel();
            return;
        }
        var key = CredentialStore.LoadApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetApiKey();
            key = CredentialStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(key)) return;
        }
        IsGenerating = true;
        _generateCts = new CancellationTokenSource();
        var progress = new Progress<string>(s => DialogueStatus = s);
        try
        {
            var result = await new DialogueGenerator(key).GenerateAsync(new DialogueRequest
            {
                Language = DialogueLanguage,
                Speakers = DialogueSpeakers,
                Lines = DialogueLines,
                Topic = DialogueTopic,
                GenerateScript = DialogueWriteScript || !HasBuiltInScript,
                ScriptModel = DialogueScriptModel,
            }, AppPaths.TestAudioDir, progress, _generateCts.Token);
            LastDialogueFile = result.AudioPath;
            DialogueScript = File.ReadAllText(result.ScriptPath);
            DialogueStatus = $"Saved to {result.AudioPath}. Press “Play through the pipeline”.";
            _log.Info($"Generated test dialogue {Path.GetFileName(result.AudioPath)}");
        }
        catch (OperationCanceledException)
        {
            DialogueStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _log.Warn("Dialogue generation failed", ex);
            DialogueStatus = ex.Message;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private async Task PlayDialogue()
    {
        if (LastDialogueFile == null || !File.Exists(LastDialogueFile)) return;
        if (IsRunning) await StopSessionAsync();
        AudioFilePath = LastDialogueFile;
        SelectedSource = SourceOptions.First(o => o.Value == SourceKind.File);
        await StartSessionAsync();
    }

    [RelayCommand]
    private void OpenTestAudioFolder()
    {
        Directory.CreateDirectory(AppPaths.TestAudioDir);
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.TestAudioDir); } catch { }
    }
}
