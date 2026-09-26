using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LiveSubtitles.App.Interop;
using LiveSubtitles.App.ViewModels;

namespace LiveSubtitles.App.Views;

/// <summary>Transparent, borderless, always-on-top subtitle window. Works over borderless-windowed games
/// (exclusive fullscreen games draw over every normal window, so use borderless/windowed mode).</summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private IntPtr _hwnd;

    public OverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyExtendedStyle();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.ClickThrough)) ApplyExtendedStyle();
        };
        LocationChanged += (_, _) => vm.RememberPlacement(Left, Top, Width, Height);
        SizeChanged += (_, _) => vm.RememberPlacement(Left, Top, Width, Height);
        Loaded += (_, _) => PlaceWindow();
        // Re-assert topmost periodically: some games/launchers push themselves above other topmost windows.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && IsVisible)
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
        timer.Start();
    }

    private void PlaceWindow()
    {
        var s = _vm.Placement;
        Width = s.Width;
        Height = s.Height;
        var area = SystemParameters.WorkArea;
        if (s.Left is { } l && s.Top is { } t && l > SystemParameters.VirtualScreenLeft - 50 && t > SystemParameters.VirtualScreenTop - 50
            && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
            && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - Height - 60;
        }
    }

    public void ResetPosition()
    {
        _vm.Placement.Left = null;
        _vm.Placement.Top = null;
        PlaceWindow();
    }

    private void ApplyExtendedStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        long style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
        if (_vm.ClickThrough) style |= NativeMethods.WS_EX_TRANSPARENT;
        else style &= ~(long)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        ResizeMode = _vm.ClickThrough ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
