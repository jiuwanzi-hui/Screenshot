using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Screenshot.App.Capture;

internal sealed class NativeSelectionSizeWindow : Form
{
    private const int ExtendedWindowStyleIndex = -20;
    private const long ExTransparent = 0x20L;
    private const long ExToolWindow = 0x80L;
    private const long ExNoActivate = 0x08000000L;
    private const int Topmost = -1;
    private const uint NoActivate = 0x10;
    private const uint NoOwnerZOrder = 0x200;
    private const int ShowNormal = 5;
    private const int HideCommand = 0;
    private bool _disposed;
    private Color _backgroundColor = Color.FromArgb(30, 45, 60);
    private Color _borderColor = Color.FromArgb(90, 115, 140);
    private Color _textColor = Color.FromArgb(235, 240, 246);

    public NativeSelectionSizeWindow()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Width = 96;
        Height = 24;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        CreateControl();
        var style = GetWindowLongPtr(Handle, ExtendedWindowStyleIndex).ToInt64();
        _ = SetWindowLongPtr(Handle, ExtendedWindowStyleIndex,
            new IntPtr(style | ExTransparent | ExToolWindow | ExNoActivate));
    }

    public void Update(Rectangle selection)
    {
        if (_disposed || !IsHandleCreated || selection.Width <= 0 || selection.Height <= 0)
        {
            return;
        }

        var x = selection.Left;
        var y = selection.Top - Height - 8;
        if (y < 0)
        {
            y = selection.Bottom + 8;
        }
        _ = SetWindowPos(Handle, new IntPtr(Topmost), x, y, Width, Height,
            NoActivate | NoOwnerZOrder);
        _ = InvalidateRect(Handle, IntPtr.Zero, false);
        _ = ShowWindow(Handle, ShowNormal);
    }

    public void EnsureVisible(Rectangle selection)
    {
        if (_disposed || !IsHandleCreated || selection.Width <= 0 ||
            selection.Height <= 0)
        {
            return;
        }

        var x = selection.Left;
        var y = selection.Top - Height - 8;
        if (y < 0)
        {
            y = selection.Bottom + 8;
        }

        _ = SetWindowPos(Handle, new IntPtr(Topmost), x, y, Width, Height,
            NoActivate | NoOwnerZOrder);
        _ = ShowWindow(Handle, ShowNormal);
        _ = UpdateWindow(Handle);
    }

    public void SetOwner(IntPtr owner)
    {
        if (_disposed || !IsHandleCreated || owner == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowLongPtr(Handle, OwnerIndex, owner);
    }

    public void HideSize()
    {
        if (!_disposed && IsHandleCreated)
        {
            _ = ShowWindow(Handle, HideCommand);
        }
    }

    public void SetThemeColors(Color accentStart, Color accentEnd)
    {
        if (_disposed)
        {
            return;
        }

        _borderColor = Color.FromArgb(accentEnd.R, accentEnd.G, accentEnd.B);
        _backgroundColor = ControlPaint.Dark(accentStart, 0.72f);
        _textColor = ControlPaint.Light(accentStart, 0.88f);
        if (IsHandleCreated)
        {
            _ = InvalidateRect(Handle, IntPtr.Zero, false);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        e.Graphics.Clear(Color.Magenta);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(_backgroundColor);
        using var border = new Pen(_borderColor);
        var bounds = new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        using var path = CreateRoundedRectanglePath(bounds, 6);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);
        using var textBrush = new SolidBrush(_textColor);
        using var font = new Font("Consolas", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        var text = $"{Math.Max(0, _lastWidth)} × {Math.Max(0, _lastHeight)}";
        var size = e.Graphics.MeasureString(text, font);
        e.Graphics.DrawString(text, font, textBrush,
            (ClientSize.Width - size.Width) / 2,
            (ClientSize.Height - size.Height) / 2);
    }

    private static GraphicsPath CreateRoundedRectanglePath(
        Rectangle bounds,
        int radius)
    {
        var diameter = Math.Min(radius * 2,
            Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top,
            diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter,
            diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private int _lastWidth;
    private int _lastHeight;

    public void SetDimensions(int width, int height)
    {
        _lastWidth = width;
        _lastHeight = height;
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
            HideSize();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    private const int OwnerIndex = -8;
    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => GetWindowLongPtr64(window, index);
    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) => SetWindowLongPtr64(window, index, value);
}
