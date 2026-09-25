using System.Windows;
using System.Windows.Threading;
using LiveSubtitles.App.Services;
using LiveSubtitles.App.ViewModels;
using LiveSubtitles.App.Views;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Settings;

namespace LiveSubtitles.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private FileLog? _log;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\LiveSubtitlesForDiscord", out bool first);
        if (!first)
        {
            MessageBox.Show("Live Subtitles is already running (check the system tray).", "Live Subtitles", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppPaths.DataDir);
        _log = new FileLog(AppPaths.LogDir);
        _log.Info($"Live Subtitles {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => _log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { _log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        var settingsStore = new SettingsStore(AppPaths.DataDir);
        _main = new MainViewModel(settingsStore, _log);
        var window = new MainWindow(_main);
        _main.AttachWindows(window);
        if (_main.Settings.StartMinimized && _main.Settings.MinimizeToTray) window.StartHidden();
        else window.Show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Live Subtitles — unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("Exiting");
        _log?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
