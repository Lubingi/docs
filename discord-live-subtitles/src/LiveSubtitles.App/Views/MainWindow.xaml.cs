using System.ComponentModel;
using System.Windows;
using LiveSubtitles.App.ViewModels;

namespace LiveSubtitles.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _reallyClosing;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
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
        await ExitAsync();
    }
}
