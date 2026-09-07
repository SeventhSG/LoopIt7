using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LoopIt7.Services;

/// <summary>
/// A single system wide hotkey for muting the input. Musicians need that reachable while
/// the app is behind a DAW, which is exactly what a global hotkey is for.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;

    public event EventHandler? Pressed;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    /// <summary>Returns false when another application already owns the combination.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        if (_handle == IntPtr.Zero) return false;

        // MOD_NOREPEAT (0x4000) so holding the keys does not fire a stream of toggles.
        _registered = RegisterHotKey(_handle, HotkeyId, modifiers | 0x4000, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered || _handle == IntPtr.Zero) return;
        UnregisterHotKey(_handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
