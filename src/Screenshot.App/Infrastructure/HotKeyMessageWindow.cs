using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

// Input routing needs a message-only HWND, never a WPF composition target.
// Keep dispatcher ordering and the existing WM_HOTKEY/WM_INPUT handler while
// avoiding a renderer for this permanently hidden window in both processes.
internal sealed class HotKeyMessageWindow : NativeWindow, IDisposable
{
    private HwndSourceHook? _hook;
    private bool _disposed;

    internal Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;

    internal HotKeyMessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Screenshot.App.HotKeySink",
            Parent = new IntPtr(-3), // HWND_MESSAGE
            Style = 0,
        });
    }

    internal void AddHook(HwndSourceHook hook) => _hook += hook;
    internal void RemoveHook(HwndSourceHook hook) => _hook -= hook;

    protected override void WndProc(ref Message message)
    {
        var handled = false;
        var result = _hook?.Invoke(message.HWnd, message.Msg, message.WParam, message.LParam, ref handled);
        if (handled) message.Result = result ?? IntPtr.Zero;
        else base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Dispatcher.VerifyAccess();
        _disposed = true;
        _hook = null;
        DestroyHandle();
    }
}
