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

    public ApiKeyWindow(string model)
    {
        InitializeComponent();
        _model = model;
        RemoveButton.IsEnabled = CredentialStore.HasApiKey();
        if (RemoveButton.IsEnabled) Result.Text = "A key is already saved. Paste a new one to replace it.";
        Loaded += (_, _) => KeyBox.Focus();
    }

    private string EnteredOrSavedKey => KeyBox.Password.Trim().Length > 0 ? KeyBox.Password.Trim() : CredentialStore.LoadApiKey() ?? "";

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var key = EnteredOrSavedKey;
        if (key.Length == 0) { Show("Paste a key first.", false); return; }
        TestButton.IsEnabled = false;
        Result.Text = "Checking…";
        var (ok, message) = await ApiKeyValidator.CheckAsync(key, _model);
        Show(message, ok);
        TestButton.IsEnabled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { DialogResult = false; return; }
        if (!key.StartsWith("sk-", StringComparison.Ordinal))
        {
            if (MessageBox.Show(this, "OpenAI keys normally start with \"sk-\". Save anyway?", "API key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        CredentialStore.SaveApiKey(key);
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        CredentialStore.DeleteApiKey();
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
