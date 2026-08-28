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
    private const int ShowNormal = 5;
    private const int HideCommand = 0;
    private bool _disposed;
    // Keep the dimming layer neutral. The selection border follows the theme,
    // but the mask must not inherit the accent color.
    private static readonly Color NeutralMaskColor = Color.Black;

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
        if (_disposed || !IsHandleCreated || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var hole = Rectangle.Intersect(
            new Rectangle(selection.X - surface.X, selection.Y - surface.Y,
                selection.Width, selection.Height),
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
            _ = SetWindowPos(Handle, new IntPtr(TopmostWindow), surface.X, surface.Y,
                surface.Width, surface.Height,
                DoNotActivate | DoNotChangeOwnerZOrder);
            // USER32 owns the region after this call. Repaint only the changed
            // window after it is visible so the mask cannot disappear between
            // the native border update and the WPF controls becoming visible.
            _ = SetWindowRgn(Handle, region, false);
            region = IntPtr.Zero;
            _ = ShowWindow(Handle, ShowNormal);
            _ = InvalidateRect(Handle, IntPtr.Zero, false);
        }
        finally
        {
            if (region != IntPtr.Zero)
            {
                _ = DeleteObject(region);
            }
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
