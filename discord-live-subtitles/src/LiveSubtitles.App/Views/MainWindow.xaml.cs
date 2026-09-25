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

    // ----- history: click a speaker name (rename is added with speaker identification)
    private void HistorySpeaker_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row }) _vm.HistorySpeakerClicked(row);
    }
}
