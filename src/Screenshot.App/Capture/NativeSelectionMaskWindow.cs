using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Screenshot.App.Capture;

internal sealed class NativeSelectionMaskWindow : Form
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const int TopmostWindow = -1;
    private const uint DoNotActivate = 0x0010;
    private const uint DoNotChangeOwnerZOrder = 0x0200;
    private const uint NoRedraw = 0x0008;
    private const int ShowNormal = 5;
    private const int HideCommand = 0;
    private bool _disposed;
    private readonly object _updateLock = new();
    // Keep the dimming layer neutral. The selection border follows the theme,
    // but the mask must not inherit the accent color.
    private static readonly Color NeutralMaskColor = Color.Black;
    private Rectangle? _excludedRegion;

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
        lock (_updateLock)
        {
            _excludedRegion = excluded is { Width: > 0, Height: > 0 }
                ? excluded
                : null;
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
            // Leave a tiny safety gap around the selection so a compositor
            // frame can never cover the border while the two HWNDs change
            // z-order at high pointer rates.
            hole.Inflate(2, 2);
            hole = Rectangle.Intersect(
                hole,
                new Rectangle(0, 0, surface.Width, surface.Height));
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                AddRegion(region, new Rectangle(0, 0, surface.Width, Math.Max(0, hole.Top)));
                AddRegion(region, new Rectangle(0, hole.Bottom, surface.Width,
                    Math.Max(0, surface.Height - hole.Bottom)));
                AddRegion(region, new Rectangle(0, hole.Top, Math.Max(0, hole.Left),
                    Math.Max(0, hole.Height)));
                AddRegion(region, new Rectangle(hole.Right, hole.Top,
                    Math.Max(0, surface.Width - hole.Right), Math.Max(0, hole.Height)));
                if (_excludedRegion is { } excluded)
                {
                    var exclusion = Rectangle.Intersect(
                        new Rectangle(excluded.X - surface.X, excluded.Y - surface.Y,
                            excluded.Width, excluded.Height),
                        new Rectangle(0, 0, surface.Width, surface.Height));
                    if (!exclusion.IsEmpty)
                    {
                        var exclusionRegion = CreateRectRgn(
                            exclusion.Left, exclusion.Top,
                            exclusion.Right, exclusion.Bottom);
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
                var insertAfter = belowWindow == IntPtr.Zero
                    ? new IntPtr(TopmostWindow)
                    : belowWindow;
                var flags = belowWindow == IntPtr.Zero
                    ? DoNotActivate | DoNotChangeOwnerZOrder | NoRedraw
                    : DoNotActivate | NoRedraw;
                _ = SetWindowPos(Handle, insertAfter, surface.X, surface.Y,
                    surface.Width, surface.Height, flags);
                _ = SetWindowRgn(Handle, region, true);
                region = IntPtr.Zero;
                if (show)
                {
                    _ = ShowWindow(Handle, ShowNormal);
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
            _excludedRegion = region is { Width: > 0, Height: > 0 }
                ? region
                : null;
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
        _ = SetWindowLongPtr(Handle, OwnerIndex, owner);
    }

    public void PlaceBelow(IntPtr sibling)
    {
        if (_disposed || !IsHandleCreated || sibling == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            Handle,
            sibling,
            Left,
            Top,
            Width,
            Height,
            DoNotActivate);
    }

    public void HideMask()
    {
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
            HideMask();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private static void AddRegion(IntPtr destination, Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }
        var source = CreateRectRgn(rectangle.Left, rectangle.Top,
            rectangle.Right, rectangle.Bottom);
        try
        {
            _ = CombineRgn(destination, destination, source, 2);
        }
        finally
        {
            _ = DeleteObject(source);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
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
