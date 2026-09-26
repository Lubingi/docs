using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LiveSubtitles.App.Services;
using LiveSubtitles.App.ViewModels;

namespace LiveSubtitles.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _reallyClosing;
    private bool _settingsApplyQueued;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _vm.MinimizeToTray)
            {
                Hide();
                _vm.NotifyMinimizedToTray();
            }
        };
        vm.History.RowAdded += row =>
        {
            if (vm.History.AutoScroll && IsVisible)
                Dispatcher.BeginInvoke(() => HistoryGrid.ScrollIntoView(row), DispatcherPriority.Background);
        };
    }

    public MainViewModel ViewModel => _vm;

    public void StartHidden()
    {
        // Create the window handle (needed for hotkeys) without showing it.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public async Task ExitAsync()
    {
        _reallyClosing = true;
        await _vm.ShutdownAsync();
        Close();
        Application.Current.Shutdown();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_reallyClosing) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_vm.MinimizeToTray)
        {
            Hide();
            _vm.NotifyMinimizedToTray();
            return;
        }
        await ExitAsync();
    }

    // ----- settings tab: any edit → deferred apply (bindings have updated their source by then)
    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _settingsApplyQueued) return;
        _settingsApplyQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _settingsApplyQueued = false;
            _vm.ApplySettings();
        }, DispatcherPriority.Background);
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Back or Key.Delete)
        {
            ((TextBox)sender).Text = "";
            return;
        }
        var text = HotkeyManager.Format(Keyboard.Modifiers, key);
        if (text == null) return;
        if (Keyboard.Modifiers == ModifierKeys.None && key is < Key.F1 or > Key.F24) return; // plain letters would block typing everywhere
        ((TextBox)sender).Text = text;
    }

    // ----- glossary: save after an edit is committed
    private void Glossary_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(_vm.GlossaryEdited, DispatcherPriority.Background);

    private void Glossary_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(_vm.GlossaryEdited, DispatcherPriority.Background);

    // ----- history: click a speaker name to rename it (or pick who a "?" line was)
    private void HistorySpeaker_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row }) _vm.HistorySpeakerClicked(row);
    }

    private void HistoryMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = _vm.History.Selected;
        bool enabled = row != null && _vm.SpeakerIdEnabled;
        RenameMenu.IsEnabled = enabled && row!.SpeakerId != null;
        AssignMenu.Items.Clear();
        MergeMenu.Items.Clear();
        AssignMenu.IsEnabled = MergeMenu.IsEnabled = enabled;
        if (!enabled) return;
        foreach (var choice in _vm.SpeakerChoices())
        {
            var item = new MenuItem { Header = choice.Label, IsChecked = Equals(choice.Value, row!.SpeakerId) };
            item.Click += (_, _) => _vm.ReassignLine(row.SegmentId, choice.Value as int?);
            AssignMenu.Items.Add(item);
            if (choice.Value is int target && row.SpeakerId is int from && target != from)
            {
                var merge = new MenuItem { Header = choice.Label };
                merge.Click += (_, _) => _vm.MergeSpeaker(from, target);
                MergeMenu.Items.Add(merge);
            }
        }
        MergeMenu.IsEnabled = MergeMenu.Items.Count > 0;
    }

    private void HistoryRename_Click(object sender, RoutedEventArgs e) => _vm.RenameSpeaker(_vm.History.Selected?.SpeakerId);

    private void SpeakerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpeakerGrid.SelectedItem is SpeakerRow row) _vm.RenameSpeaker(row.Id);
    }
}
