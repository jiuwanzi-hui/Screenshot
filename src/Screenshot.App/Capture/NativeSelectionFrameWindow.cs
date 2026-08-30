using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Capture;

/// <summary>
/// A native layered HWND used for the live selection border. During a drag it
/// is paired with the native region mask window; both are updated directly from
/// the same polling sample without a dispatcher hop.
/// </summary>
internal sealed class NativeSelectionFrameWindow : Form
{
    private const int FrameWidth = 3;
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleLayered = 0x00080000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;
    private const int ShowNormal = 5;
    private const int HideCommand = 0;
    private Color _borderStartColor = Color.FromArgb(91, 141, 239);
    private Color _borderEndColor = Color.FromArgb(91, 141, 239);
    private readonly object _renderLock = new();
    private Bitmap? _layerBitmap;
    private Rectangle _layerBitmapBounds;
    private bool _maskEnabled;
    private NativeSelectionMaskWindow? _maskWindow;
    private Rectangle _maskSurface;
    private Rectangle? _maskExcludedRegion;
    private bool _borderVisible = true;
    private NativeSelectionSizeWindow? _sizeWindow;
    private bool _sizeEnabled;
    private readonly object _boundsLock = new();
    private Rectangle _lastBounds;
    private long _boundsVersion;
    private readonly object _auxiliaryUpdateLock = new();
    private Rectangle _pendingAuxiliaryBounds;
    private long _pendingAuxiliaryVersion;
    private bool _auxiliaryUpdateScheduled;

    private bool _disposed;
    private CancellationTokenSource? _trackingCancellation;
    private Thread? _trackingThread;
    private Action? _trackingReleaseCallback;
    private bool _nativeMouseCaptured;
    private readonly object _nativeUpdateLock = new();

    public NativeSelectionFrameWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        ControlBox = false;
        // WinForms does not accept Color.Transparent as a control background.
        // The layered bitmap supplies the actual alpha surface, so this value
        // is only a fallback for the native window before its first update.
        BackColor = Color.Black;
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
                       ExtendedStyleNoActivate |
                       ExtendedStyleLayered));
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

    public void SetMaskExcludedRegion(Rectangle? region)
    {
        lock (_nativeUpdateLock)
        {
            _maskExcludedRegion = region;
        }
        RefreshMask();
    }

    public void SetMaskEnabled(bool enabled)
    {
        lock (_nativeUpdateLock)
        {
            _maskEnabled = enabled;
            if (!enabled)
            {
                _maskWindow?.HideMask();
            }
            _borderVisible = true;
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

    private long PublishBounds(Rectangle bounds)
    {
        lock (_boundsLock)
        {
            _lastBounds = bounds;
            return ++_boundsVersion;
        }
    }

    private bool IsCurrentBoundsVersion(long version)
    {
        lock (_boundsLock)
        {
            return _boundsVersion == version;
        }
    }

    private static Rectangle GetRenderWindowBounds(Rectangle selectionBounds)
    {
        // Keep one extra device pixel on the right/bottom edges. GDI region
        // rectangles are right/bottom exclusive, and a layered HWND whose
        // size exactly matches the selection can otherwise lose the final
        // border pixel during a resize commit.
        return new Rectangle(
            selectionBounds.X,
            selectionBounds.Y,
            Math.Max(1, selectionBounds.Width),
            Math.Max(1, selectionBounds.Height));
    }

    private bool TryGetLastBoundsVersion(out Rectangle bounds, out long version)
    {
        lock (_boundsLock)
        {
            bounds = _lastBounds;
            version = _boundsVersion;
        }

        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void PositionLayeredWindow(Rectangle windowBounds)
    {
        windowBounds = new Rectangle(
            windowBounds.X,
            windowBounds.Y,
            Math.Max(1, windowBounds.Width),
            Math.Max(1, windowBounds.Height));
        _ = SetWindowPos(
            Handle,
            new IntPtr(TopmostWindow),
            0,
            0,
            0,
            0,
            // UpdateLayeredWindow commits the actual position and size along
            // with the pixels. SetWindowPos only restores the native z-order,
            // avoiding a visible resize/clear intermediate state.
            DoNotActivate | DoNotChangeOwnerZOrder | NoMove | NoSize);
    }

    public void RefreshMask()
    {
        if (!TryGetLastBounds(out var bounds))
        {
            return;
        }

        var windowBounds = GetRenderWindowBounds(bounds);
        lock (_nativeUpdateLock)
        {
            if (IsHandleCreated)
            {
                PositionLayeredWindow(windowBounds);
                RenderLayered(windowBounds, bounds);
                if (_maskEnabled)
                {
                    _maskWindow?.UpdateNative(_maskSurface, bounds, _maskExcludedRegion, Handle);
                }
            }
        }
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

        lock (_renderLock)
        {
            _borderStartColor = Color.FromArgb(startColor.R, startColor.G, startColor.B);
            _borderEndColor = Color.FromArgb(endColor.R, endColor.G, endColor.B);
            if (IsHandleCreated && TryGetLastBounds(out var bounds))
            {
                RenderLayered(GetRenderWindowBounds(bounds), bounds);
            }
        }
    }

    public bool Update(Rectangle bounds)
    {
        if (_disposed || !IsHandleCreated)
        {
            return false;
        }

        lock (_nativeUpdateLock)
        {
            try
            {
                _borderVisible = true;
                var windowBounds = GetRenderWindowBounds(bounds);
                PositionLayeredWindow(windowBounds);
                _ = PublishBounds(bounds);
                RenderLayered(windowBounds, bounds);
                if (_maskEnabled)
                {
                    _maskWindow?.UpdateNative(_maskSurface, bounds, _maskExcludedRegion, Handle);
                }
                if (_sizeEnabled)
                {
                    _sizeWindow?.SetDimensions(bounds.Width, bounds.Height);
                    _sizeWindow?.Update(bounds);
                }
                PositionLayeredWindow(windowBounds);
                _ = ShowWindow(Handle, ShowNormal);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // The high-frequency tracker runs on a dedicated thread. Keep that path
    // limited to USER32/GDI operations on this HWND; the mask and size windows
    // are owned by the UI thread and must not be touched from the polling loop.
    private bool UpdateBorderOnly(Rectangle bounds)
    {
        if (_disposed || !IsHandleCreated)
        {
            return false;
        }

        lock (_nativeUpdateLock)
        {
            try
            {
                _borderVisible = true;
                var windowBounds = GetRenderWindowBounds(bounds);
                PositionLayeredWindow(windowBounds);
                var version = PublishBounds(bounds);
                RenderLayered(windowBounds, bounds);
                if (_maskEnabled)
                {
                    _maskWindow?.UpdateNative(_maskSurface, bounds, _maskExcludedRegion, Handle);
                }
                QueueAuxiliaryWindowUpdate(bounds, version);
                _ = ShowWindow(Handle, ShowNormal);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void QueueAuxiliaryWindowUpdate(Rectangle bounds, long version)
    {
        if (_disposed || !IsHandleCreated ||
            (!_maskEnabled && !_sizeEnabled))
        {
            return;
        }

        lock (_auxiliaryUpdateLock)
        {
            _pendingAuxiliaryBounds = bounds;
            _pendingAuxiliaryVersion = version;
            if (_auxiliaryUpdateScheduled)
            {
                return;
            }

            _auxiliaryUpdateScheduled = true;
        }

        try
        {
            BeginInvoke(new Action(ApplyQueuedAuxiliaryWindowUpdate));
        }
        catch (InvalidOperationException)
        {
            lock (_auxiliaryUpdateLock)
            {
                _auxiliaryUpdateScheduled = false;
            }
        }
    }

    private void ApplyQueuedAuxiliaryWindowUpdate()
    {
        Rectangle bounds;
        long version;
        lock (_auxiliaryUpdateLock)
        {
            bounds = _pendingAuxiliaryBounds;
            version = _pendingAuxiliaryVersion;
            _auxiliaryUpdateScheduled = false;
        }

        if (_disposed || !IsHandleCreated || bounds.Width <= 0 ||
            bounds.Height <= 0)
        {
            return;
        }

        // A release/final-commit can publish a newer rectangle while this
        // callback is waiting in the dispatcher queue. Never let that old
        // callback move the border back to the previous position.
        if (!IsCurrentBoundsVersion(version))
        {
            if (TryGetLastBoundsVersion(out var currentBounds, out var currentVersion))
            {
                QueueAuxiliaryWindowUpdate(currentBounds, currentVersion);
            }

            return;
        }

        if (_sizeEnabled)
        {
            _sizeWindow?.SetDimensions(bounds.Width, bounds.Height);
            _sizeWindow?.Update(bounds);
        }

        if (!IsCurrentBoundsVersion(version))
        {
            if (TryGetLastBoundsVersion(out var currentBounds, out var currentVersion))
            {
                QueueAuxiliaryWindowUpdate(currentBounds, currentVersion);
            }

            return;
        }

        Rectangle latestBounds = default;
        long latestVersion = 0;
        var hasNewerPendingUpdate = false;
        lock (_auxiliaryUpdateLock)
        {
            if (_pendingAuxiliaryVersion != version)
            {
                latestBounds = _pendingAuxiliaryBounds;
                latestVersion = _pendingAuxiliaryVersion;
                _auxiliaryUpdateScheduled = false;
                hasNewerPendingUpdate = true;
            }
        }

        if (hasNewerPendingUpdate)
        {
            QueueAuxiliaryWindowUpdate(latestBounds, latestVersion);
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

        lock (_nativeUpdateLock)
        {
            var windowBounds = GetRenderWindowBounds(bounds);
            PositionLayeredWindow(windowBounds);
            _borderVisible = true;
            RenderLayered(windowBounds, bounds);
            _ = ShowWindow(Handle, ShowNormal);
        }
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
            lock (_nativeUpdateLock)
            {
                _ = ShowWindow(Handle, HideCommand);
                _maskWindow?.HideMask();
                _sizeWindow?.HideSize();
            }
        }
    }

    public new void Hide()
    {
        if (!_disposed && IsHandleCreated)
        {
            lock (_nativeUpdateLock)
            {
                _ = ShowWindow(Handle, HideCommand);
                _maskWindow?.HideMask();
                _sizeWindow?.HideSize();
            }
        }
    }

    public void HideBorderKeepMask()
    {
        if (!_disposed && IsHandleCreated)
        {
            lock (_nativeUpdateLock)
            {
                // The border HWND must remain a selection-sized surface. Do
                // not reuse it for the full-screen mask while the toolbar is
                // being restored; changing its size/contents mid-gesture is
                // what caused the border to disappear at the largest bounds.
                _borderVisible = false;
                _ = ShowWindow(Handle, HideCommand);
                _sizeWindow?.HideSize();
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // This HWND is exclusively painted by UpdateLayeredWindow. A normal
        // WinForms paint can race the layered commit and briefly erase edges.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Intentionally empty; see OnPaintBackground.
    }

    private void PaintFrame(Graphics graphics, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            _borderStartColor,
            _borderEndColor,
            LinearGradientMode.Horizontal);
        graphics.FillRectangle(brush, 0, 0, width, Math.Min(FrameWidth, height));
        graphics.FillRectangle(brush, 0, Math.Max(0, height - FrameWidth), width, Math.Min(FrameWidth, height));
        graphics.FillRectangle(brush, 0, 0, Math.Min(FrameWidth, width), height);
        graphics.FillRectangle(brush, Math.Max(0, width - FrameWidth), 0, Math.Min(FrameWidth, width), height);
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
                using var maskRegion = new Region(new Rectangle(
                    0, 0, windowBounds.Width, windowBounds.Height));
                var clippedSelection = Rectangle.Intersect(
                    new Rectangle(0, 0, windowBounds.Width, windowBounds.Height),
                    selection);
                if (!clippedSelection.IsEmpty)
                {
                    maskRegion.Exclude(clippedSelection);
                }

                if (_maskExcludedRegion is { } excluded)
                {
                    var excludedLocal = new Rectangle(
                        excluded.Left - windowBounds.Left,
                        excluded.Top - windowBounds.Top,
                        excluded.Width,
                        excluded.Height);
                    var clippedExcluded = Rectangle.Intersect(
                        new Rectangle(0, 0, windowBounds.Width, windowBounds.Height),
                        excludedLocal);
                    if (!clippedExcluded.IsEmpty)
                    {
                        maskRegion.Exclude(clippedExcluded);
                    }
                }

                graphics.FillRegion(maskBrush, maskRegion);
            }

            if (_borderVisible && selection.Width > 0 && selection.Height > 0)
            {
                using var borderBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, selection.Width), Math.Max(1, selection.Height)),
                    _borderStartColor,
                    _borderEndColor,
                    LinearGradientMode.Horizontal);
                var outer = Rectangle.Intersect(
                    new Rectangle(0, 0, windowBounds.Width, windowBounds.Height),
                    selection);
                if (!outer.IsEmpty)
                {
                    using var borderRegion = new Region(outer);
                    var inner = outer;
                    inner.Inflate(-FrameWidth, -FrameWidth);
                    if (!inner.IsEmpty)
                    {
                        borderRegion.Exclude(inner);
                    }

                    // Fill one continuous ring instead of four independently
                    // painted bands. The gradient then has one coordinate
                    // system and cannot hard-cut at an edge or corner.
                    graphics.FillRegion(borderBrush, borderRegion);
                }
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
            if (screenDc == IntPtr.Zero || memoryDc == IntPtr.Zero)
            {
                if (memoryDc != IntPtr.Zero)
                {
                    _ = DeleteDC(memoryDc);
                }

                if (screenDc != IntPtr.Zero)
                {
                    _ = ReleaseDC(IntPtr.Zero, screenDc);
                }

                return;
            }
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
                var consecutiveButtonUpSamples = 0;
                while (!cancellation.IsCancellationRequested && !_disposed)
                {
                    var buttonIsDown = (GetAsyncKeyState(LeftButton) & unchecked((short)0x8000)) != 0;
                    if (buttonIsDown)
                    {
                        buttonWasDown = true;
                        consecutiveButtonUpSamples = 0;
                    }
                    else if (buttonWasDown)
                    {
                        // GetAsyncKeyState can briefly report an up state while
                        // a high-report-rate device is paused or crosses the
                        // native frame region. Do not finish the drag on one
                        // sample; require a stable release window instead.
                        consecutiveButtonUpSamples++;
                        if (consecutiveButtonUpSamples >= 8)
                        {
                            _trackingReleaseCallback?.Invoke();
                            break;
                        }
                    }

                    if (stopwatch.ElapsedMilliseconds >= 1 &&
                        GetCursorPos(out var cursor))
                    {
                        stopwatch.Restart();
                        var bounds = projection(new DrawingPoint(cursor.X, cursor.Y));
                        if (bounds != lastBounds)
                        {
                            _ = UpdateBorderOnly(bounds);
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
