using System.Windows;

namespace LiveSubtitles.App.Views;

public sealed record ChoiceItem(object? Value, string Label);

/// <summary>Small modal used for renaming speakers and picking a target speaker.</summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
    }

    public static string? AskText(Window? owner, string title, string message, string initial)
    {
        var w = new PromptWindow(title, message) { Owner = owner is { IsVisible: true } ? owner : null };
        w.Input.Visibility = Visibility.Visible;
        w.Input.Text = initial;
        w.Loaded += (_, _) => { w.Input.Focus(); w.Input.SelectAll(); };
        return w.ShowDialog() == true ? w.Input.Text.Trim() : null;
    }

    public static ChoiceItem? AskChoice(Window? owner, string title, string message, IReadOnlyList<ChoiceItem> items)
    {
        if (items.Count == 0) return null;
        var w = new PromptWindow(title, message) { Owner = owner is { IsVisible: true } ? owner : null };
        w.Choice.Visibility = Visibility.Visible;
        w.Choice.ItemsSource = items;
        w.Choice.SelectedIndex = 0;
        return w.ShowDialog() == true ? w.Choice.SelectedItem as ChoiceItem : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
