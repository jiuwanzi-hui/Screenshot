using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Infrastructure;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Pin;

internal sealed class NativeWindowDragTracker
{
    private const int LeftButton = 0x01;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private readonly Window _window;
    private readonly Action _released;
    private readonly TimeSpan _frameInterval;
    private Thread? _thread;
    private IntPtr _handle;
    private DrawingPoint _startCursor;
    private Rectangle _startBounds;
    private int _active;

    public NativeWindowDragTracker(Window window, Action released)
    {
        _window = window;
        _released = released;
        _frameInterval = DisplayRefreshRateService.GetInteractionFrameInterval(
            System.Windows.Forms.Screen.PrimaryScreen?.Bounds ??
            System.Windows.Forms.SystemInformation.VirtualScreen);
    }

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public bool Start()
    {
        Stop();
        if (!MonitorGeometryService.TryGetWindowBounds(_window, out _startBounds))
        {
            return false;
        }

        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            return false;
        }

        SetCapture(_handle);
        if (GetCapture() != _handle)
        {
            _handle = IntPtr.Zero;
            return false;
        }

        _startCursor = Cursor.Position;
        Interlocked.Exchange(ref _active, 1);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SnapCut native pinned window drag",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        var thread = _thread;
        _thread = null;
        if (_handle != IntPtr.Zero && GetCapture() == _handle)
        {
            ReleaseCapture();
        }
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            _ = thread.Join(100);
        }
        _handle = IntPtr.Zero;
    }

    private void Run()
    {
        _ = timeBeginPeriod(1);
        try
        {
            while (IsActive)
            {
                if ((GetAsyncKeyState(LeftButton) & unchecked((short)0x8000)) == 0)
                {
                    if (Interlocked.Exchange(ref _active, 0) != 0)
                    {
                        if (_handle != IntPtr.Zero && GetCapture() == _handle)
                        {
                            ReleaseCapture();
                        }

                        _ = _window.Dispatcher.BeginInvoke(_released);
                    }

                    return;
                }

                if (GetCursorPos(out var cursor))
                {
                    _ = SetWindowPos(
                        _handle,
                        IntPtr.Zero,
                        _startBounds.Left + cursor.X - _startCursor.X,
                        _startBounds.Top + cursor.Y - _startCursor.Y,
                        0,
                        0,
                        NoSize | NoZOrder | NoActivate);
                }

                var delay = _frameInterval;
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
            }
        }
        finally
        {
            _ = timeEndPeriod(1);
            if (ReferenceEquals(_thread, Thread.CurrentThread))
            {
                _thread = null;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out DrawingPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
