using System.Windows.Input;
using System.Windows.Interop;
using LiveSubtitles.App.Interop;

namespace LiveSubtitles.App.Services;

/// <summary>System-wide hotkeys (work while a game or Discord has focus) via RegisterHotKey.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers all hotkeys; returns a list of problems (invalid text or already used by another app).</summary>
    public List<string> Register(IEnumerable<(string Name, string Gesture, Action Action)> hotkeys)
    {
        UnregisterAll();
        var problems = new List<string>();
        foreach (var (name, gesture, action) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(gesture)) continue;
            if (!TryParse(gesture, out uint mods, out uint vk))
            {
                problems.Add($"{name}: can't understand \"{gesture}\"");
                continue;
            }
            int id = _nextId++;
            if (NativeMethods.RegisterHotKey(_hwnd, id, mods | NativeMethods.MOD_NOREPEAT, vk)) _actions[id] = action;
            else problems.Add($"{name}: {gesture} is already used by another app");
        }
        return problems;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win": case "windows": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    try { key = (Key)new KeyConverter().ConvertFromInvariantString(raw)!; }
                    catch { return false; }
                    break;
            }
        }
        if (key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    /// <summary>Formats a key press for the hotkey editor ("Ctrl+Alt+S").</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
