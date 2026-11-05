using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Infrastructure;
using DrawingPoint = System.Drawing.Point;

namespace Screenshot.App.Capture;

// All gesture visuals are committed on their owning UI thread from the same
// physical cursor sample. No cross-thread HWND calls, queued stale masks, or
// per-packet bitmap uploads are involved.
internal sealed class NativeSelectionFrameWindow : Form
{
    // Form recreates/reapplies CreateParams during native teardown and warm
    // pool construction. Setting NOACTIVATE only after Handle creation lets
    // that invisible helper steal the foreground until the worker exits.
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x080800A0; // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        OverlayShellBehavior.Apply(Handle);
    }

    private readonly PointerFramePump _frames;
    private readonly NativeSelectionBorder _border;
    private Color _startColor = Color.FromArgb(91, 141, 239);
    private Color _endColor = Color.FromArgb(91, 141, 239);
    private NativeSelectionMaskWindow? _maskWindow;
    private NativeSelectionSizeWindow? _sizeWindow;
    private Rectangle _surface;
    private Rectangle[] _exclusions = [];
    private Rectangle _lastBounds;
    private bool _maskEnabled;
    private bool _sizeEnabled;
    private bool _exclusionsSuspended;
    private bool _borderVisible = true;
    private bool _disposed;
    private bool _nativeMouseCaptured;
    private bool _sawButtonDown;
    private Func<DrawingPoint, Rectangle>? _projection;
    private Action? _released;
    private int _presentationUpdateDepth;
    internal bool IsPresentationDeferred => _presentationUpdateDepth > 0;

    internal void PrepareHiddenBorderWindows() => _ = _border.GetWindowHandles();

    // Only used for initial ordinary-editor setup beneath the resident cover.
    // Coalesce identical border/mask/size submissions; live dragging and long
    // capture retain their existing immediate update path.
    internal PresentationUpdate DeferPresentation(Func<bool> canPresent)
    {
        _presentationUpdateDepth++;
        return new(this, canPresent);
    }

    internal readonly struct PresentationUpdate(NativeSelectionFrameWindow owner, Func<bool> canPresent) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._presentationUpdateDepth == 0 && !owner._disposed && canPresent())
            {
                owner._sizeWindow?.SetAvoidRegions(owner._exclusions);
                owner.Update(owner._lastBounds);
            }
        }
    }

    public NativeSelectionFrameWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        ControlBox = false;
        BackColor = Color.Black;
        Width = Height = 1;
        CreateControl();
        var style = GetWindowLongPtrW(Handle, -20).ToInt64();
        _ = SetWindowLongPtrW(Handle, -20, new IntPtr(style | 0x080800A0));
        _border = new NativeSelectionBorder(Handle);
        _frames = new PointerFramePump(OnPointerFrame, nativeFrames: true);
    }

    public bool IsMaskEnabled => _maskEnabled;
    public bool IsSizeEnabled => _sizeEnabled;
    public bool IsTracking => _projection is not null;

    internal bool ExcludeFromScreenCapture() => _border.ExcludeFromScreenCapture();
    internal IntPtr[] GetBorderWindowHandles() => _border.GetWindowHandles();

    public void AttachMaskWindow(NativeSelectionMaskWindow maskWindow, Rectangle surface)
    {
        _maskWindow = maskWindow;
        _surface = surface;
    }

    public void AttachSizeWindow(NativeSelectionSizeWindow sizeWindow) => _sizeWindow = sizeWindow;

    public void SetOwner(IntPtr owner)
    {
        if (!_disposed && owner != IntPtr.Zero) _border.SetOwner(owner);
    }

    public void SetBorderColor(Color color) => SetBorderColors(color, color);

    public void SetBorderColors(Color startColor, Color endColor)
    {
        if (_disposed || startColor.A == 0 || endColor.A == 0) return;
        startColor = Color.FromArgb(startColor.R, startColor.G, startColor.B);
        endColor = Color.FromArgb(endColor.R, endColor.G, endColor.B);
        if (_startColor == startColor && _endColor == endColor) return;
        _startColor = startColor;
        _endColor = endColor;
        if (!IsPresentationDeferred && _borderVisible && TryGetLastBounds(out var bounds))
            _border.Update(bounds, _startColor, _endColor);
    }

    public void SetMaskExcludedRegion(Rectangle? region) =>
        SetMaskExcludedRegions(region is { } value ? [value] : null);

    public void SetMaskExcludedRegions(IReadOnlyList<Rectangle>? regions)
    {
        _exclusions = regions?.Where(r => r.Width > 0 && r.Height > 0).ToArray() ?? [];
        if (!IsPresentationDeferred) _sizeWindow?.SetAvoidRegions(_exclusions);
        RefreshMask();
    }

    public void SetMaskExclusionsSuspended(bool suspended)
    {
        _exclusionsSuspended = suspended;
        if (suspended) _exclusions = [];
        if (!IsPresentationDeferred) _sizeWindow?.SetAvoidRegions(_exclusions);
        RefreshMask();
    }

    public void SetMaskEnabled(bool enabled)
    {
        _maskEnabled = enabled;
        if (!enabled) _maskWindow?.HideMask();
        RefreshMask();
    }

    public void SetSizeEnabled(bool enabled)
    {
        _sizeEnabled = enabled;
        if (!enabled) _sizeWindow?.HideSize();
    }

    public void ShowIdleMask(Rectangle surface)
    {
        if (_disposed || surface.Width <= 0 || surface.Height <= 0) return;
        _surface = surface;
        _maskEnabled = true;
        _exclusions = [];
        _sizeWindow?.SetAvoidRegions(_exclusions);
        _maskWindow?.UpdateNative(surface, Rectangle.Empty, _exclusions, Handle);
    }

    public bool TryGetLastBounds(out Rectangle bounds)
    {
        bounds = _lastBounds;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public void RefreshMask()
    {
        if (_disposed || !_maskEnabled || IsPresentationDeferred) return;
        _maskWindow?.UpdateNative(_surface, _lastBounds,
            _exclusionsSuspended ? Array.Empty<Rectangle>() : _exclusions, Handle);
    }

    public bool Update(Rectangle bounds)
    {
        if (_disposed || !IsHandleCreated) return false;
        _lastBounds = bounds;
        _borderVisible = bounds.Width > 0 && bounds.Height > 0;
        if (IsPresentationDeferred) return true;
        if (_borderVisible)
        {
            _border.Update(bounds, _startColor, _endColor);
            if (!IsTracking) _border.EnsureTopmost();
        }
        else _border.Hide();
        RefreshMask();
        if (_sizeEnabled && _borderVisible)
        {
            _sizeWindow?.SetDimensions(bounds.Width, bounds.Height);
            _sizeWindow?.Update(bounds);
        }
        else _sizeWindow?.HideSize();
        return true;
    }

    public void EnsureVisible()
    {
        if (_disposed || !TryGetLastBounds(out var bounds)) return;
        if (IsPresentationDeferred) return;
        Update(bounds);
        if (_sizeEnabled) _sizeWindow?.EnsureVisible(bounds);
    }

    public void StartSelectionTracking(DrawingPoint start, Rectangle surface,
        Action? releaseCallback = null, bool captureMouse = true, double? aspectRatio = null) =>
        StartTracking(cursor => NormalizeSelection(start, cursor, surface, aspectRatio),
            releaseCallback, captureMouse);

    public void StartMoveTracking(DrawingPoint startCursor, Rectangle initialBounds,
        Rectangle surface, Action? releaseCallback = null)
    {
        StartTracking(cursor => new Rectangle(
            Math.Clamp(initialBounds.X + cursor.X - startCursor.X, surface.Left,
                Math.Max(surface.Left, surface.Right - initialBounds.Width)),
            Math.Clamp(initialBounds.Y + cursor.Y - startCursor.Y, surface.Top,
                Math.Max(surface.Top, surface.Bottom - initialBounds.Height)),
            initialBounds.Width, initialBounds.Height), releaseCallback);
    }

    public void StartCustomTracking(Func<DrawingPoint, Rectangle> projection,
        Action? releaseCallback = null, bool captureMouse = true) =>
        StartTracking(projection, releaseCallback, captureMouse);

    private void StartTracking(Func<DrawingPoint, Rectangle> projection,
        Action? releaseCallback = null, bool captureMouse = true)
    {
        StopTracking(hide: false);
        if (_disposed || !IsHandleCreated) return;
        _projection = projection;
        _released = releaseCallback;
        _sawButtonDown = IsLeftButtonDown();
        if (captureMouse)
        {
            _ = SetCapture(Handle);
            _nativeMouseCaptured = GetCapture() == Handle;
        }
        _frames.Start();
    }

    private void OnPointerFrame()
    {
        if (_disposed || _projection is null) return;
        PresentLatestPointer();
        var down = IsLeftButtonDown();
        _sawButtonDown |= down;
        if (_sawButtonDown && !down) CompleteTracking();
    }

    private void PresentLatestPointer()
    {
        if (_projection is null || !GetCursorPos(out var cursor)) return;
        var bounds = _projection(cursor);
        if (bounds != _lastBounds || !_borderVisible) Update(bounds);
    }

    private void CompleteTracking()
    {
        // Commit the release position before notifying the selection owner.
        PresentLatestPointer();
        _frames.Stop();
        var released = _released;
        _released = null;
        released?.Invoke();
    }

    public void StopTracking(bool hide = true)
    {
        _frames.Stop();
        if (!_disposed) PresentLatestPointer();
        _projection = null;
        _released = null;
        if (_nativeMouseCaptured && GetCapture() == Handle) _ = ReleaseCapture();
        _nativeMouseCaptured = false;
        if (hide) Hide();
    }

    public new void Hide()
    {
        if (_disposed) return;
        _border.Hide();
        _maskWindow?.HideMask();
        _sizeWindow?.HideSize();
    }

    public void HideBorderKeepMask() => HideBorderOnly();

    public void HideBorderOnly()
    {
        if (_disposed) return;
        _borderVisible = false;
        _border.Hide();
        _sizeWindow?.HideSize();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084)
        {
            message.Result = new IntPtr(-1);
            return;
        }
        if (message.Msg == 0x0202 && IsTracking) CompleteTracking();
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            StopTracking();
            _frames.Dispose();
            _border.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private static Rectangle NormalizeSelection(DrawingPoint start, DrawingPoint end,
        Rectangle surface, double? aspectRatio)
    {
        if (aspectRatio is > 0)
            return CaptureAspectRatioHelper.ConstrainFromAnchor(start, end, surface, aspectRatio);
        var left = Math.Clamp(Math.Min(start.X, end.X), surface.Left, surface.Right);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), surface.Top, surface.Bottom);
        var right = Math.Clamp(Math.Max(start.X, end.X), surface.Left, surface.Right);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), surface.Top, surface.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(1) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out DrawingPoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
