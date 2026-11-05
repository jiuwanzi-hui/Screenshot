using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Screenshot.App.Presentation;

// A non-activating tray menu must still dismiss on an outside click/Esc.
// Hooks exist only while the menu is open; mouse movement does no work.
internal sealed class TrayMenuInputMonitor : IDisposable
{
    private readonly IntPtr _window;
    private readonly Dispatcher _dispatcher;
    private readonly Action _dismiss;
    private readonly Action<int> _navigate;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly ForegroundProc _foregroundProc;
    private readonly HashSet<int> _keys = [];
    private IntPtr _mouse, _keyboard, _foreground;
    private bool _disposed;

    internal TrayMenuInputMonitor(IntPtr window, Dispatcher dispatcher, Action dismiss, Action<int> navigate)
    {
        _window = window; _dispatcher = dispatcher; _dismiss = dismiss; _navigate = navigate;
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
        _foregroundProc = (_, _, active, _, _, _, _) => { if (active != _window) Queue(_dismiss); };
        var module = GetModuleHandleW(null);
        _mouse = SetWindowsHookExW(14, _mouseProc, module, 0);
        _keyboard = SetWindowsHookExW(13, _keyboardProc, module, 0);
        _foreground = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundProc, 0, 0, 0);
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<NativePoint>(data);
            if (GetWindowRect(_window, out var rect) &&
                (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom))
                Queue(_dismiss);
        }
        return CallNextHookEx(_mouse, code, message, data);
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !_disposed)
        {
            var key = Marshal.ReadInt32(data);
            var down = message.ToInt32() is 0x100 or 0x104;
            if (!down && _keys.Remove(key)) return new IntPtr(1);
            if (down && IsMenuKey(key) && !HasModifiers())
            {
                _keys.Add(key);
                Queue(() => _navigate(key));
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_keyboard, code, message, data);
    }

    internal static bool IsMenuKey(int key) => key is 0x1B or 0x0D or 0x20 or 0x26 or 0x28 or 0x24 or 0x23;
    private static bool HasModifiers() => (GetAsyncKeyState(0x10) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
    private void Queue(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mouse != IntPtr.Zero) _ = UnhookWindowsHookEx(_mouse);
        if (_keyboard != IntPtr.Zero) _ = UnhookWindowsHookEx(_keyboard);
        if (_foreground != IntPtr.Zero) _ = UnhookWinEvent(_foreground);
        _mouse = _keyboard = _foreground = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private delegate void ForegroundProc(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint thread, uint time);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, ForegroundProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
