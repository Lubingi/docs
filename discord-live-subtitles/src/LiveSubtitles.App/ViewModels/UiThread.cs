using System.Windows;

namespace LiveSubtitles.App.ViewModels;

internal static class UiThread
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
