using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Used only while a resident picker is visible but Windows denied foreground
// activation. Mouse input still goes through normal HWND hit testing. Preserve
// Esc / C without letting them dismiss or type into the covered application.
internal sealed class CapturePickerKeyboardFallback : IDisposable
{
    internal const int ForwardedKeyMessage = 0x8043;
    private readonly IntPtr _window;
    private readonly Func<bool> _canCopy;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private bool _escapeDown, _copyDown;

    internal CapturePickerKeyboardFallback(IntPtr window, Func<bool> canCopy)
    {
        _window = window;
        _canCopy = canCopy;
        _callback = OnKeyboard;
        _hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _callback, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = (uint)Marshal.ReadInt32(data);
            var down = message.ToInt32() is 0x100 or 0x104;
            var up = message.ToInt32() is 0x101 or 0x105;
            if (up && (key == 0x1B && _escapeDown || key == 0x43 && _copyDown))
            {
                if (key == 0x1B) _escapeDown = false; else _copyDown = false;
                return new IntPtr(1);
            }
            if (down && IsWindowVisible(_window) && !CaptureInputReadiness.OwnsForeground(_window) &&
                (key == 0x1B || key == 0x43 && _canCopy()))
            {
                // Only enqueue; no rendering, IPC, clipboard or disk work in a hook.
                // A private message avoids WinForms/WPF dialog preprocessing
                // swallowing a synthetic WM_KEYDOWN (especially Escape).
                if (PostMessageW(_window, ForwardedKeyMessage, new IntPtr(key), IntPtr.Zero))
                {
                    if (key == 0x1B) _escapeDown = true; else _copyDown = true;
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (hook != IntPtr.Zero) _ = UnhookWindowsHookEx(hook);
        GC.KeepAlive(_callback);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
