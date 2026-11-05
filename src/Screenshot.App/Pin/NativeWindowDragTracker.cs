using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Infrastructure;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Pin;

internal sealed class NativeWindowDragTracker : IDisposable
{
    private readonly Window _window;
    private readonly Action _released;
    private readonly IWindowDragInput _input;
    private PointerFramePump? _frames;
    private HwndSource? _source;
    private bool _disposed;
    private IntPtr _handle;
    private DrawingPoint _startCursor;
    private DrawingPoint _lastCursor;
    private Rectangle _startBounds;

    public NativeWindowDragTracker(Window window, Action released, IWindowDragInput? input = null)
    {
        _window = window;
        _released = released;
        _input = input ?? new NativeDragInput();
    }

    public bool IsActive => _frames?.IsActive == true;

    public bool Start()
    {
        if (_disposed) return false;
        Stop(commitPosition: false);
        if (!MonitorGeometryService.TryGetWindowBounds(_window, out _startBounds)) return false;
        _handle = new WindowInteropHelper(_window).Handle;
        var source = HwndSource.FromHwnd(_handle);
        if (source is null || !_input.TryGetCursorPosition(out _startCursor) || !_input.Capture(_handle))
        {
            _handle = IntPtr.Zero;
            return false;
        }
        _lastCursor = _startCursor;
        _source = source;
        _source.AddHook(OnWindowMessage);
        // Moving an HWND does not need a WPF render pass. Under sustained
        // mouse input a transparent window's Rendering event can lag behind.
        // The native clock queues at most one dispatcher frame, at display cadence.
        _frames = new PointerFramePump(Present, nativeFrames: true);
        _frames.Start();
        return true;
    }

    public void Stop() => Stop(commitPosition: true);

    private void Stop(bool commitPosition)
    {
        if (!IsActive) return;
        var frames = _frames;
        _frames = null; // ReleaseCapture can synchronously reenter the hook.
        frames?.Dispose(); // Wake and retire the clock worker without joining it.
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnWindowMessage);
        _source = null;
        // A release can arrive between two render frames.
        if (commitPosition && _input.CapturedWindow == _handle) MoveToLatestCursor();
        if (_handle != IntPtr.Zero && _input.CapturedWindow == _handle) _input.Release();
        _handle = IntPtr.Zero;
    }

    private void Present()
    {
        if (_input.CapturedWindow != _handle)
        {
            Stop(commitPosition: false);
            _released();
        }
        else if (!_input.IsLeftButtonDown)
        {
            Stop();
            _released();
        }
        else MoveToLatestCursor();
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!IsActive || window != _handle) return IntPtr.Zero;
        switch (message)
        {
            case 0x0200: // WM_MOUSEMOVE
                // Consume before WPF's mouse provider does hit testing, input
                // staging and routed events for every packet. Only this HWND's
                // whole-window drag uses this hook; drawing/text selection do not.
                handled = true;
                break;
            case 0x0202: // WM_LBUTTONUP: keep WPF's button state in sync.
                Stop();
                _released();
                break;
            case 0x0215: // WM_CAPTURECHANGED
            case 0x001F: // WM_CANCELMODE
            case 0x0082: // WM_NCDESTROY
                Stop(commitPosition: false);
                _released();
                break;
        }
        return IntPtr.Zero;
    }

    private void MoveToLatestCursor()
    {
        if (_handle == IntPtr.Zero || !_input.TryGetCursorPosition(out var cursor) || cursor == _lastCursor) return;
        // The HWND belongs to this dispatcher. Cross-thread synchronous moves
        // used to contend with WPF layout and a 100 ms Join on mouse release.
        if (SetWindowPos(_handle, IntPtr.Zero,
            _startBounds.Left + cursor.X - _startCursor.X,
            _startBounds.Top + cursor.Y - _startCursor.Y,
            0, 0, 0x0015)) _lastCursor = cursor; // NOSIZE | NOZORDER | NOACTIVATE
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(commitPosition: false);
    }

    private sealed class NativeDragInput : IWindowDragInput
    {
        public IntPtr CapturedWindow => GetCapture();
        public bool IsLeftButtonDown => (GetAsyncKeyState(1) & 0x8000) != 0;
        public bool TryGetCursorPosition(out DrawingPoint point) => GetCursorPos(out point);
        public bool Capture(IntPtr window)
        {
            _ = SetCapture(window);
            return GetCapture() == window;
        }
        public void Release() => _ = ReleaseCapture();
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr windowHandle);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out DrawingPoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

// Keep native pointer reads replaceable for offscreen regression tests. Tests
// exercise the real HWND/message/move path without moving or capturing the user's mouse.
internal interface IWindowDragInput
{
    IntPtr CapturedWindow { get; }
    bool IsLeftButtonDown { get; }
    bool TryGetCursorPosition(out DrawingPoint point);
    bool Capture(IntPtr window);
    void Release();
}
