using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Capture;

internal sealed class NativeSelectionMaskWindow : Form
{
    // Keep this invariant from the first HWND through WinForms style refreshes.
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x080000A0; // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        OverlayShellBehavior.Apply(Handle);
    }

    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint NoRedraw = 0x0008;
    private const int ShowNoActivateCommand = 8;
    private const int HideCommand = 0;
    private bool _disposed;
    private readonly object _updateLock = new();
    // Keep the dimming layer neutral. The selection border follows the theme,
    // but the mask must not inherit the accent color.
    private static readonly Color NeutralMaskColor = Color.Black;
    private Rectangle[] _excludedRegions = [];
    private Rectangle _positionedSurface;
    private IntPtr _positionedBelow;
    private bool _hasPosition;
    private bool _hasRegion;
    private Rectangle _lastHole;
    private Rectangle _regionSurface;
    private Rectangle[] _lastExclusions = [];
    private IntPtr _presentationCover;
    private IntPtr _coverPreviousOwner;

    // During a cold editor handoff the resident's opaque DIB remains above
    // this mask. Dimming that already-masked DIB would cause a dark flash.
    internal void SetPresentationCover(IntPtr cover)
    {
        if (_presentationCover == cover) return;
        if (_presentationCover != IntPtr.Zero && IsWindow(_presentationCover) &&
            GetWindowLongPtr(_presentationCover, OwnerIndex) == Handle)
            _ = SetWindowLongPtr(_presentationCover, OwnerIndex, _coverPreviousOwner);
        _presentationCover = IntPtr.Zero;
        if (cover == IntPtr.Zero || _disposed || !IsWindow(cover)) return;
        // A sibling order can be changed by USER32 while raising the owner
        // group. Ownership enforces cover > mask > editor throughout first
        // layout, including native raises that precede WINDOWPOSCHANGING.
        _coverPreviousOwner = SetWindowLongPtr(cover, OwnerIndex, Handle);
        if (GetWindowLongPtr(cover, OwnerIndex) == Handle) _presentationCover = cover;
    }

    internal void CompleteCoveredPresentation(IntPtr belowWindow)
    {
        SetPresentationCover(IntPtr.Zero);
        _positionedBelow = belowWindow;
    }

    internal void PrepareCoveredPresentation()
    {
        if (_disposed || !IsHandleCreated) return;
        _ = InvalidateRect(Handle, IntPtr.Zero, true);
        _ = UpdateWindow(Handle);
    }

    public NativeSelectionMaskWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        BackColor = NeutralMaskColor;
        Opacity = 0.28;
        Width = 1;
        Height = 1;
        CreateControl();
        var style = GetWindowLongPtr(Handle, ExtendedWindowStyleIndex).ToInt64();
        _ = SetWindowLongPtr(Handle, ExtendedWindowStyleIndex,
            new IntPtr(style | ExtendedStyleTransparent |
                       ExtendedStyleToolWindow | ExtendedStyleNoActivate));
    }

    public void Update(Rectangle surface, Rectangle selection)
    {
        UpdateCore(surface, selection, show: true, belowWindow: IntPtr.Zero);
    }

    // The native drag loop calls this method directly. It deliberately avoids
    // WinForms control state and only uses USER32/GDI handles, so the mask is
    // committed on the same sample as the border without a dispatcher delay.
    public void UpdateNative(
        Rectangle surface,
        Rectangle selection,
        Rectangle? excluded,
        IntPtr belowWindow)
    {
        UpdateNative(
            surface,
            selection,
            excluded is { Width: > 0, Height: > 0 } ? [excluded.Value] : [],
            belowWindow);
    }

    public void UpdateNative(
        Rectangle surface,
        Rectangle selection,
        IReadOnlyList<Rectangle> excluded,
        IntPtr belowWindow)
    {
        lock (_updateLock)
        {
            _excludedRegions = excluded as Rectangle[] ?? excluded.ToArray();
            UpdateCore(surface, selection, show: true, belowWindow);
        }
    }

    private void UpdateCore(
        Rectangle surface,
        Rectangle selection,
        bool show,
        IntPtr belowWindow)
    {
        if (_disposed || !IsHandleCreated || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        lock (_updateLock)
        {
            var hole = Rectangle.Intersect(
                new Rectangle(selection.X - surface.X, selection.Y - surface.Y,
                    selection.Width, selection.Height),
                new Rectangle(0, 0, surface.Width, surface.Height));
            var regionChanged = !_hasRegion || _regionSurface != surface ||
                _lastHole != hole || !_excludedRegions.AsSpan().SequenceEqual(_lastExclusions);
            // The mask has constant black pixels. Repeated visibility/toolbar
            // refreshes of identical geometry must not repaint the desktop.
            var region = regionChanged ? CreateRectRgn(0, 0, surface.Width, surface.Height) : IntPtr.Zero;
            if (regionChanged && region == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                if (regionChanged) SubtractRegion(region, hole);
                foreach (var excluded in regionChanged ? _excludedRegions : Array.Empty<Rectangle>())
                {
                    var exclusion = Rectangle.Intersect(
                        new Rectangle(excluded.X - surface.X, excluded.Y - surface.Y,
                            excluded.Width, excluded.Height),
                        new Rectangle(0, 0, surface.Width, surface.Height));
                    if (!exclusion.IsEmpty)
                    {
                        // Keep the exclusion shape aligned with the rounded
                        // WPF toolbar/picker. A rectangular exclusion exposes
                        // the four corners as a visible hole in the mask.
                        var cornerRadius = Math.Clamp(
                            Math.Min(exclusion.Width, exclusion.Height) / 6,
                            4,
                            12);
                        var exclusionRegion = CreateRoundRectRgn(
                            exclusion.Left,
                            exclusion.Top,
                            exclusion.Right,
                            exclusion.Bottom,
                            cornerRadius,
                            cornerRadius);
                        try
                        {
                            _ = CombineRgn(region, region, exclusionRegion, 4);
                        }
                        finally
                        {
                            _ = DeleteObject(exclusionRegion);
                        }
                    }
                }
                if (_presentationCover != IntPtr.Zero && !IsWindow(_presentationCover))
                    _presentationCover = IntPtr.Zero;
                var insertAfter = _presentationCover != IntPtr.Zero ? _presentationCover : belowWindow == IntPtr.Zero
                    ? new IntPtr(TopmostWindow)
                    : belowWindow;
                var flags = belowWindow == IntPtr.Zero || _presentationCover != IntPtr.Zero
                    ? DoNotActivate | DoNotChangeOwnerZOrder | NoRedraw
                    : DoNotActivate | NoRedraw;
                if (!_hasPosition || _positionedSurface != surface || _positionedBelow != insertAfter)
                {
                    _ = SetWindowPos(Handle, insertAfter, surface.X, surface.Y,
                        surface.Width, surface.Height, flags);
                    _positionedSurface = surface;
                    _positionedBelow = insertAfter;
                    _hasPosition = true;
                }
                if (regionChanged && SetWindowRgn(Handle, region, true) != 0)
                {
                    region = IntPtr.Zero; // USER32 owns a successfully applied region.
                    _lastHole = hole;
                    _regionSurface = surface;
                    if (!_excludedRegions.AsSpan().SequenceEqual(_lastExclusions))
                        _lastExclusions = (Rectangle[])_excludedRegions.Clone();
                    _hasRegion = true;
                }
                if (show && !IsWindowVisible(Handle))
                {
                    // SW_SHOW explicitly activates even a no-activate tool
                    // window, dismissing the shell menu before frame handoff.
                    // Showing a topmost HWND with ShowWindow can raise it
                    // above the resident cover even after SetWindowPos. Commit
                    // visibility and the required sibling order together.
                    if (_presentationCover != IntPtr.Zero)
                        _ = SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
                            DoNotActivate | DoNotChangeOwnerZOrder | 0x0001 | 0x0002 | 0x0040);
                    else
                        _ = ShowWindow(Handle, ShowNoActivateCommand);
                }
            }
            finally
            {
                if (region != IntPtr.Zero)
                {
                    _ = DeleteObject(region);
                }
            }
        }
    }

    public void SetExcludedRegion(Rectangle? region)
    {
        lock (_updateLock)
        {
            _excludedRegions = region is { Width: > 0, Height: > 0 }
                ? [region.Value]
                : [];
        }
    }

    public void SetOwner(IntPtr owner)
    {
        if (_disposed || !IsHandleCreated || owner == IntPtr.Zero)
        {
            return;
        }

        // Keep the mask in the overlay's owned topmost window group. WPF
        // ContextMenu popups can otherwise reorder topmost windows and leave
        // the mask behind the overlay until the next selection update.
        _hasPosition = false;
        _ = SetWindowLongPtr(Handle, OwnerIndex, owner);
    }

    public void PlaceBelow(IntPtr sibling)
    {
        if (_disposed || !IsHandleCreated || sibling == IntPtr.Zero)
        {
            return;
        }

        _hasPosition = false;
        _ = SetWindowPos(
            Handle,
            sibling,
            Left,
            Top,
            Width,
            Height,
            DoNotActivate);
    }

    public void RefreshPresentation()
    {
        lock (_updateLock)
        {
            if (_disposed || !IsHandleCreated || !_hasPosition || !_hasRegion ||
                !IsWindowVisible(Handle)) return;

            // An owner activation/layout pass may cover the mask even though
            // its cached bounds and cutouts did not change. Repair this only
            // at the editor handoff; live pointer/scroll updates keep reusing
            // the existing region and never invalidate the whole surface.
            _ = SetWindowPos(Handle, _positionedBelow, 0, 0, 0, 0,
                DoNotActivate | DoNotChangeOwnerZOrder | 0x0001 | 0x0002);
            _ = InvalidateRect(Handle, IntPtr.Zero, true);
        }
    }

    public void HideMask()
    {
        _hasPosition = false;
        if (!_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084)
        {
            message.Result = new IntPtr(-1);
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetPresentationCover(IntPtr.Zero);
            HideMask();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private static void SubtractRegion(IntPtr destination, Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }
        var source = CreateRectRgn(rectangle.Left, rectangle.Top,
            rectangle.Right, rectangle.Bottom);
        try
        {
            _ = CombineRgn(destination, destination, source, 4);
        }
        finally
        {
            _ = DeleteObject(source);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1,
        IntPtr source2, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    private const int OwnerIndex = -8;
    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        GetWindowLongPtr64(window, index);
    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        SetWindowLongPtr64(window, index, value);
}
