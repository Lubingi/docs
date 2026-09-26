using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using LiveSubtitles.App.Services;
using LiveSubtitles.Core.Translation;

namespace LiveSubtitles.App.Views;

public partial class ApiKeyWindow : Window
{
    private readonly string _model;
    private readonly TranslationEngine _engine;
    private readonly string _service;

    public ApiKeyWindow(TranslationEngine engine, string model)
    {
        InitializeComponent();
        _engine = engine;
        _model = model;
        _service = engine == TranslationEngine.Soniox ? "Soniox" : "OpenAI";
        Title = $"{_service} API key";
        Intro.Text = $"Paste your {_service} API key. It is stored in Windows Credential Manager (encrypted for your Windows account) and never written to a file.";
        var url = engine == TranslationEngine.Soniox ? "https://console.soniox.com" : "https://platform.openai.com/api-keys";
        KeyLink.NavigateUri = new Uri(url);
        KeyLinkText.Text = engine == TranslationEngine.Soniox ? "Create a key at console.soniox.com (API keys)" : "Create a key at platform.openai.com/api-keys";
        RemoveButton.IsEnabled = CredentialStore.HasApiKey(engine);
        if (RemoveButton.IsEnabled) Result.Text = "A key is already saved. Paste a new one to replace it.";
        Loaded += (_, _) => KeyBox.Focus();
    }

    private string EnteredOrSavedKey => KeyBox.Password.Trim().Length > 0 ? KeyBox.Password.Trim() : CredentialStore.LoadApiKey(_engine) ?? "";

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var key = EnteredOrSavedKey;
        if (key.Length == 0) { Show("Paste a key first.", false); return; }
        TestButton.IsEnabled = false;
        Result.Text = "Checking…";
        var (ok, message) = _engine == TranslationEngine.Soniox
            ? await ApiKeyValidator.CheckSonioxAsync(key, _model)
            : await ApiKeyValidator.CheckAsync(key, _model);
        Show(message, ok);
        TestButton.IsEnabled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { DialogResult = false; return; }
        if (_engine == TranslationEngine.OpenAI && !key.StartsWith("sk-", StringComparison.Ordinal))
        {
            if (MessageBox.Show(this, "OpenAI keys normally start with \"sk-\". Save anyway?", "API key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        CredentialStore.SaveApiKey(key, _engine);
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        CredentialStore.DeleteApiKey(_engine);
        RemoveButton.IsEnabled = false;
        Show("Saved key removed.", true);
    }

    private void Show(string text, bool ok)
    {
        Result.Text = text;
        Result.Foreground = ok ? Brushes.DarkGreen : Brushes.Firebrick;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
