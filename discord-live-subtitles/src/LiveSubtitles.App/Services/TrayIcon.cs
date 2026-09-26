using System.Windows;
using Forms = System.Windows.Forms;

namespace LiveSubtitles.App.Services;

/// <summary>Notification-area icon with a small menu. Uses WinForms' NotifyIcon (WPF has none).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startStop, _pause, _overlay, _clickThrough;
    private bool _balloonShown;

    public TrayIcon(Action show, Action startStop, Action pause, Action toggleOverlay, Action toggleClickThrough, Action exit)
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
            Text = "Live Subtitles",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Live Subtitles", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _startStop = new Forms.ToolStripMenuItem("Start", null, (_, _) => startStop());
        _pause = new Forms.ToolStripMenuItem("Pause", null, (_, _) => pause()) { Enabled = false };
        _overlay = new Forms.ToolStripMenuItem("Show overlay", null, (_, _) => toggleOverlay());
        _clickThrough = new Forms.ToolStripMenuItem("Click-through overlay", null, (_, _) => toggleClickThrough());
        menu.Items.AddRange(new Forms.ToolStripItem[] { _startStop, _pause, _overlay, _clickThrough, new Forms.ToolStripSeparator() });
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => show();
    }

    public void Update(bool running, bool paused, bool overlayVisible, bool clickThrough, string status)
    {
        _startStop.Text = running ? "Stop" : "Start";
        _pause.Enabled = running;
        _pause.Text = paused ? "Resume" : "Pause";
        _overlay.Checked = overlayVisible;
        _clickThrough.Checked = clickThrough;
        var text = "Live Subtitles — " + (running ? (paused ? "paused" : status) : "stopped");
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowMinimizedHint()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "Live Subtitles is still running", "It's in the notification area. Right-click the icon to exit.", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
