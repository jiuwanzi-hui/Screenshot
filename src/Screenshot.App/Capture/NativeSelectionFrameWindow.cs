using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Capture;

/// <summary>
/// A small WinForms HWND used for the live selection border. The form is
/// color-key transparent and paints only four border bands with GDI. This
/// keeps the high-frequency drag path out of the full-screen WPF surface.
/// </summary>
internal sealed class NativeSelectionFrameWindow : Form
{
    private const int FrameWidth = 3;
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint InvalidateErase = 0x0001;
    private const int ShowNormal = 5;
    private const int HideCommand = 0;
    private Color _borderStartColor = Color.FromArgb(91, 141, 239);
    private Color _borderEndColor = Color.FromArgb(91, 141, 239);
    // Retained for compatibility with the previous layered implementation;
    // the active path below uses the lighter color-key paint window.
    private readonly object _renderLock = new();
    private Bitmap? _layerBitmap;
    private Rectangle _layerBitmapBounds;
    private bool _maskEnabled;
    private NativeSelectionMaskWindow? _maskWindow;
    private Rectangle _maskSurface;
    private NativeSelectionSizeWindow? _sizeWindow;
    private bool _sizeEnabled;
    private readonly object _boundsLock = new();
    private Rectangle _lastBounds;

    private bool _disposed;
    private CancellationTokenSource? _trackingCancellation;
    private Thread? _trackingThread;
    private Action? _trackingReleaseCallback;
    private bool _nativeMouseCaptured;

    public NativeSelectionFrameWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        ControlBox = false;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        _maskEnabled = false;
        Width = 1;
        Height = 1;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        CreateControl();

        var style = GetWindowLongPtr(Handle, ExtendedWindowStyleIndex).ToInt64();
        _ = SetWindowLongPtr(
            Handle,
            ExtendedWindowStyleIndex,
            new IntPtr(style |
                       ExtendedStyleTransparent |
                       ExtendedStyleToolWindow |
                       ExtendedStyleNoActivate));
    }

    public void SetBorderColor(Color color)
    {
        SetBorderColors(color, color);
    }

    public void AttachMaskWindow(
        NativeSelectionMaskWindow maskWindow,
        Rectangle surface)
    {
        _maskWindow = maskWindow;
        _maskSurface = surface;
    }

    public void SetMaskEnabled(bool enabled)
    {
        _maskEnabled = enabled;
        if (!enabled)
        {
            _maskWindow?.HideMask();
            return;
        }

        RefreshMask();
    }

    public bool IsMaskEnabled => _maskEnabled;

    public bool IsSizeEnabled => _sizeEnabled;

    public bool IsTracking => _trackingCancellation is not null;

    public bool TryGetLastBounds(out Rectangle bounds)
    {
        lock (_boundsLock)
        {
            bounds = _lastBounds;
        }

        return bounds.Width > 0 && bounds.Height > 0;
    }

    public void RefreshMask()
    {
        if (!_maskEnabled || _maskWindow is null ||
            !TryGetLastBounds(out var bounds))
        {
            return;
        }

        _maskWindow.Update(_maskSurface, bounds);
    }

    public void AttachSizeWindow(NativeSelectionSizeWindow sizeWindow)
    {
        _sizeWindow = sizeWindow;
    }

    public void SetOwner(IntPtr owner)
    {
        if (_disposed || !IsHandleCreated || owner == IntPtr.Zero)
        {
            return;
        }

        // Keep the border above the WPF editor surface in the same owned
        // topmost window group as the native mask and size badge.
        _ = SetWindowLongPtr(Handle, OwnerIndex, owner);
    }

    public void SetSizeEnabled(bool enabled)
    {
        _sizeEnabled = enabled;
        if (!enabled)
        {
            _sizeWindow?.HideSize();
        }
    }

    public void SetBorderColors(Color startColor, Color endColor)
    {
        if (startColor.A == 0 || endColor.A == 0)
        {
            return;
        }

        _borderStartColor = Color.FromArgb(startColor.R, startColor.G, startColor.B);
        _borderEndColor = Color.FromArgb(endColor.R, endColor.G, endColor.B);
        if (IsHandleCreated)
        {
            _ = InvalidateRect(Handle, IntPtr.Zero, false);
        }
    }

    public bool Update(Rectangle bounds)
    {
        if (_disposed || !IsHandleCreated)
        {
            return false;
        }

        try
        {
            _ = SetWindowPos(
                Handle,
                new IntPtr(TopmostWindow),
                bounds.X,
                bounds.Y,
                Math.Max(1, bounds.Width),
                Math.Max(1, bounds.Height),
                 DoNotActivate | DoNotChangeOwnerZOrder);
            // Restrict the HWND to the painted border bands. The selection
            // interior must remain owned by the WPF input surface so a click
            // inside the region can start the move gesture.
            UpdateInputRegion(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
            lock (_boundsLock)
            {
                _lastBounds = bounds;
            }
            if (_maskEnabled)
            {
                _maskWindow?.Update(_maskSurface, bounds);
            }
            if (_sizeEnabled)
            {
                _sizeWindow?.SetDimensions(bounds.Width, bounds.Height);
                _sizeWindow?.Update(bounds);
            }
            _ = InvalidateRect(Handle, IntPtr.Zero, false);
            _ = ShowWindow(Handle, ShowNormal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flushes the final native visual state synchronously after the WPF owner
    /// has been activated or re-ordered. Drag updates stay asynchronous, while
    /// completion and recovery paths need an immediate z-order and paint commit.
    /// </summary>
    public void EnsureVisible()
    {
        if (_disposed || !IsHandleCreated ||
            !TryGetLastBounds(out var bounds))
        {
            return;
        }

        _ = SetWindowPos(
            Handle,
            new IntPtr(TopmostWindow),
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height),
            DoNotActivate | DoNotChangeOwnerZOrder);
        _ = ShowWindow(Handle, ShowNormal);
        _ = UpdateWindow(Handle);
        if (_sizeEnabled)
        {
            _sizeWindow?.EnsureVisible(bounds);
        }
    }

    public void StartSelectionTracking(
        DrawingPoint start,
        Rectangle surface,
        Action? releaseCallback = null,
        bool captureMouse = true)
    {
        StartTracking(
            cursor => NormalizeSelection(start, cursor, surface),
            releaseCallback,
            captureMouse);
    }

    public void StartMoveTracking(
        DrawingPoint startCursor,
        Rectangle initialBounds,
        Rectangle surface,
        Action? releaseCallback = null)
    {
        StartTracking(cursor =>
        {
            var dx = cursor.X - startCursor.X;
            var dy = cursor.Y - startCursor.Y;
            var moved = new Rectangle(
                initialBounds.X + dx,
                initialBounds.Y + dy,
                initialBounds.Width,
                initialBounds.Height);
            var maxX = surface.Right - moved.Width;
            var maxY = surface.Bottom - moved.Height;
            return new Rectangle(
                Math.Clamp(moved.X, surface.Left, Math.Max(surface.Left, maxX)),
                Math.Clamp(moved.Y, surface.Top, Math.Max(surface.Top, maxY)),
                moved.Width,
                moved.Height);
        }, releaseCallback);
    }

    public void StartCustomTracking(
        Func<DrawingPoint, Rectangle> projection,
        Action? releaseCallback = null,
        bool captureMouse = true)
    {
        StartTracking(projection, releaseCallback, captureMouse);
    }

    public void StopTracking(bool hide = true)
    {
        ReleaseNativeMouseCapture();
        var cancellation = _trackingCancellation;
        var trackingThread = _trackingThread;
        _trackingThread = null;
        _trackingCancellation = null;
        _trackingReleaseCallback = null;
        cancellation?.Cancel();
        // A new drag must not race an older polling loop. The loop only calls
        // the small native positioning/paint functions, so a short join here
        // is preferable to allowing two loops to move the same HWND.
        if (trackingThread is not null &&
            trackingThread != Thread.CurrentThread &&
            trackingThread.IsAlive)
        {
            _ = trackingThread.Join(100);
        }
        cancellation?.Dispose();
        if (hide && !_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
            _maskWindow?.HideMask();
            _sizeWindow?.HideSize();
        }
    }

    public new void Hide()
    {
        if (!_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
            _maskWindow?.HideMask();
            _sizeWindow?.HideSize();
        }
    }

    public void HideBorderKeepMask()
    {
        if (!_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
            if (_maskEnabled && _maskWindow is not null &&
                TryGetLastBounds(out var bounds))
            {
                _maskWindow.Update(_maskSurface, bounds);
            }
            _sizeWindow?.HideSize();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Magenta);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            _borderStartColor,
            _borderEndColor,
            LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(brush, 0, 0, width, Math.Min(FrameWidth, height));
        e.Graphics.FillRectangle(brush, 0, Math.Max(0, height - FrameWidth), width, Math.Min(FrameWidth, height));
        e.Graphics.FillRectangle(brush, 0, 0, Math.Min(FrameWidth, width), height);
        e.Graphics.FillRectangle(brush, Math.Max(0, width - FrameWidth), 0, Math.Min(FrameWidth, width), height);
    }

    protected override void WndProc(ref Message message)
    {
        // Keep the full-screen mask click-through so the WPF capture surface
        // remains the input owner while the native layer only supplies pixels.
        if (message.Msg == 0x0084) // WM_NCHITTEST
        {
            message.Result = new IntPtr(-1); // HTTRANSPARENT
            return;
        }

        base.WndProc(ref message);
    }

    private void RenderLayered(Rectangle windowBounds, Rectangle selectionBounds)
    {
        if (!IsHandleCreated || windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_layerBitmap is null || _layerBitmapBounds != windowBounds)
            {
                _layerBitmap?.Dispose();
                _layerBitmap = new Bitmap(
                    windowBounds.Width,
                    windowBounds.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                _layerBitmapBounds = windowBounds;
            }

            using var graphics = Graphics.FromImage(_layerBitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;

            var selection = new Rectangle(
                selectionBounds.Left - windowBounds.Left,
                selectionBounds.Top - windowBounds.Top,
                selectionBounds.Width,
                selectionBounds.Height);
            if (_maskEnabled)
            {
                using var maskBrush = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
                graphics.FillRectangle(
                    maskBrush,
                    0,
                    0,
                    windowBounds.Width,
                    Math.Max(0, selection.Top));
                graphics.FillRectangle(
                    maskBrush,
                    0,
                    Math.Min(windowBounds.Height, selection.Bottom),
                    windowBounds.Width,
                    Math.Max(0, windowBounds.Height - selection.Bottom));
                graphics.FillRectangle(
                    maskBrush,
                    0,
                    Math.Max(0, selection.Top),
                    Math.Max(0, selection.Left),
                    Math.Max(0, selection.Height));
                graphics.FillRectangle(
                    maskBrush,
                    Math.Min(windowBounds.Width, selection.Right),
                    Math.Max(0, selection.Top),
                    Math.Max(0, windowBounds.Width - selection.Right),
                    Math.Max(0, selection.Height));
            }

            if (selection.Width > 0 && selection.Height > 0)
            {
                using var borderBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, selection.Width), Math.Max(1, selection.Height)),
                    _borderStartColor,
                    _borderEndColor,
                    LinearGradientMode.Horizontal);
                var left = selection.Left;
                var top = selection.Top;
                var right = Math.Min(windowBounds.Width, selection.Right);
                var bottom = Math.Min(windowBounds.Height, selection.Bottom);
                graphics.FillRectangle(borderBrush, left, top, Math.Min(FrameWidth, right - left), Math.Max(0, bottom - top));
                graphics.FillRectangle(borderBrush, Math.Max(left, right - FrameWidth), top, Math.Min(FrameWidth, right - left), Math.Max(0, bottom - top));
                graphics.FillRectangle(borderBrush, left, top, Math.Max(0, right - left), Math.Min(FrameWidth, bottom - top));
                graphics.FillRectangle(borderBrush, left, Math.Max(top, bottom - FrameWidth), Math.Max(0, right - left), Math.Min(FrameWidth, bottom - top));
            }

            var screenPoint = new NativePoint { X = windowBounds.X, Y = windowBounds.Y };
            var sourcePoint = new NativePoint();
            var size = new NativeSize { Width = windowBounds.Width, Height = windowBounds.Height };
            var blend = new BlendFunction
            {
                Operation = 0,
                Flags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,
            };
            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmapHandle = _layerBitmap!.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
            var previous = SelectObject(memoryDc, bitmapHandle);
            try
            {
                _ = UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref screenPoint,
                    ref size,
                    memoryDc,
                    ref sourcePoint,
                    0,
                    ref blend,
                    2);
            }
            finally
            {
                _ = SelectObject(memoryDc, previous);
                _ = DeleteObject(bitmapHandle);
                _ = DeleteDC(memoryDc);
                _ = ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopTracking();
            // Hide the HWNDs before marking this object disposed. The hide
            // helpers intentionally ignore already-disposed instances, so
            // reversing this order could leave stale border windows on screen.
            _disposed = true;
            lock (_renderLock)
            {
                _layerBitmap?.Dispose();
                _layerBitmap = null;
            }
        }

        base.Dispose(disposing);
    }

    private void StartTracking(
        Func<DrawingPoint, Rectangle> projection,
        Action? releaseCallback = null,
        bool captureMouse = true)
    {
        // Keep the current native frame/mask visible while replacing the
        // polling loop. Hiding here creates a one-frame flash on every
        // resize/move gesture before the first cursor sample arrives.
        StopTracking(hide: false);
        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        // The frame HWND owns normal drags. A mouse-shortcut continuation is
        // different: the global input hook has already replayed the trigger
        // button and must deliver its release notification to the overlay.
        // Keep WPF capture for that one path while the native tracker still
        // supplies the live cursor position.
        if (captureMouse)
        {
            BeginNativeMouseCapture();
        }

        var cancellation = new CancellationTokenSource();
        _trackingCancellation = cancellation;
        _trackingReleaseCallback = releaseCallback;
        _trackingThread = new Thread(() =>
        {
            // Sleep(1) is otherwise rounded up to the system timer quantum
            // on machines that do not already request a 1 ms timer. Scope the
            // higher resolution to an active drag so idle CPU/power usage is
            // unchanged and mouse polling stays independent of report rate.
            _ = timeBeginPeriod(1);
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lastBounds = Rectangle.Empty;
                var buttonWasDown = false;
                while (!cancellation.IsCancellationRequested && !_disposed)
                {
                    var buttonIsDown = (GetAsyncKeyState(LeftButton) & unchecked((short)0x8000)) != 0;
                    if (buttonIsDown)
                    {
                        buttonWasDown = true;
                    }
                    else if (buttonWasDown)
                    {
                        _trackingReleaseCallback?.Invoke();
                        break;
                    }

                    if (stopwatch.ElapsedMilliseconds >= 1 &&
                        GetCursorPos(out var cursor))
                    {
                        stopwatch.Restart();
                        var bounds = projection(new DrawingPoint(cursor.X, cursor.Y));
                        if (bounds != lastBounds)
                        {
                            _ = Update(bounds);
                            lastBounds = bounds;
                        }
                    }

                    Thread.Sleep(1);
                }
            }
            finally
            {
                _ = timeEndPeriod(1);
            }
        })
        {
            IsBackground = true,
            Name = "SnapCut native selection frame",
        };
        _trackingThread.Start();
    }

    private void UpdateInputRegion(int width, int height)
    {
        if (!IsHandleCreated || width <= 0 || height <= 0)
        {
            return;
        }

        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            AddRegion(region, new Rectangle(0, 0, width, Math.Min(FrameWidth, height)));
            AddRegion(region, new Rectangle(
                0,
                Math.Max(0, height - FrameWidth),
                width,
                Math.Min(FrameWidth, height)));
            AddRegion(region, new Rectangle(
                0,
                0,
                Math.Min(FrameWidth, width),
                height));
            AddRegion(region, new Rectangle(
                Math.Max(0, width - FrameWidth),
                0,
                Math.Min(FrameWidth, width),
                height));
            _ = SetWindowRgn(Handle, region, false);
            region = IntPtr.Zero;
        }
        finally
        {
            if (region != IntPtr.Zero)
            {
                _ = DeleteObject(region);
            }
        }
    }

    private static void AddRegion(IntPtr destination, Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        var source = CreateRectRgn(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
        try
        {
            _ = CombineRgn(destination, destination, source, 2);
        }
        finally
        {
            _ = DeleteObject(source);
        }
    }

    private void BeginNativeMouseCapture()
    {
        if (_disposed || !IsHandleCreated)
        {
            return;
        }

        _ = SetCapture(Handle);
        _nativeMouseCaptured = GetCapture() == Handle;
    }

    private void ReleaseNativeMouseCapture()
    {
        if (!_nativeMouseCaptured)
        {
            return;
        }

        if (GetCapture() == Handle)
        {
            _ = ReleaseCapture();
        }

        _nativeMouseCaptured = false;
    }

    private static Rectangle NormalizeSelection(
        DrawingPoint start,
        DrawingPoint end,
        Rectangle surface)
    {
        var left = Math.Clamp(Math.Min(start.X, end.X), surface.Left, surface.Right);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), surface.Top, surface.Bottom);
        var right = Math.Clamp(Math.Max(start.X, end.X), surface.Left, surface.Right);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), surface.Top, surface.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(
        IntPtr window,
        IntPtr updateRectangle,
        bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(
        IntPtr window,
        IntPtr region,
        bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(
        int left,
        int top,
        int right,
        int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(
        IntPtr destination,
        IntPtr source1,
        IntPtr source2,
        int mode);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destinationDeviceContext,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDeviceContext,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    private const int LeftButton = 0x01;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte Operation;
        public byte Flags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    private const int OwnerIndex = -8;

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        GetWindowLongPtr64(window, index);

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        SetWindowLongPtr64(window, index, value);
}
