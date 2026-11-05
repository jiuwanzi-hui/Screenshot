using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Infrastructure;
using WpfRect = System.Windows.Rect;

namespace Screenshot.App.Capture;

internal sealed class NativeSelectionSizeWindow : Form
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
    private const long ExTransparent = 0x20L;
    private const long ExToolWindow = 0x80L;
    private const long ExNoActivate = 0x08000000L;
    private const int Topmost = -1;
    private const uint NoActivate = 0x10;
    private const uint NoOwnerZOrder = 0x200;
    private const int ShowNoActivateCommand = 8;
    private const int HideCommand = 0;
    private bool _disposed;
    private Color _backgroundColor = Color.FromArgb(30, 45, 60);
    private Color _backgroundEndColor = Color.FromArgb(36, 51, 66);
    private Color _borderColor = Color.FromArgb(90, 115, 140);
    private Color _textColor = Color.FromArgb(235, 240, 246);
    private Font _labelFont = new("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private Point _lastPosition;
    private bool _isShown;
    private bool _shouldShow;
    private bool _dimensionsChanged = true;
    private Rectangle _lastSelection;
    private Rectangle _monitorBounds;
    private double _scale = 1;
    private WpfRect[] _avoidRegions = [];
    private string _labelText = "0 × 0";
    private int _measuredTextLength;

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
        Width = 52;
        Height = 22;
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

        _lastSelection = selection;
        _shouldShow = true;
        var screen = Screen.FromRectangle(selection).Bounds;
        if (_monitorBounds != screen)
        {
            _monitorBounds = screen;
            var scale = MonitorGeometryService.GetDpiScale(selection).X;
            if (Math.Abs(scale - _scale) > .01)
            {
                _scale = scale;
                _labelFont.Dispose();
                _labelFont = new Font("Segoe UI", (float)(11 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
                _measuredTextLength = 0;
                _dimensionsChanged = true;
            }
        }
        if (_dimensionsChanged)
        {
            _labelText = $"{Math.Max(0, _lastWidth)} × {Math.Max(0, _lastHeight)}";
            // Segoe UI uses tabular digits. Re-measure only when the digit
            // count or monitor scale changes, not on every pointer frame.
            if (_labelText.Length != _measuredTextLength)
            {
                var textSize = TextRenderer.MeasureText(_labelText, _labelFont, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                ClientSize = new Size(Math.Max((int)Math.Ceiling(52 * _scale),
                    textSize.Width + (int)Math.Ceiling(16 * _scale)), (int)Math.Ceiling(22 * _scale));
                _measuredTextLength = _labelText.Length;
            }
        }
        var placement = SelectionSizeBadgeLayout.Place(ToRect(selection),
            new System.Windows.Size(Width, Height), ToRect(screen), _avoidRegions, Math.Ceiling(6 * _scale));
        if (placement.IsEmpty) { HideBadgeWindow(); return; }
        var x = (int)Math.Round(placement.X);
        var y = (int)Math.Round(placement.Y);
        var position = new Point(x, y);
        if (!_isShown || position != _lastPosition)
        {
            _ = SetWindowPos(Handle, _isShown ? IntPtr.Zero : new IntPtr(Topmost),
                x, y, 0, 0, NoActivate | NoOwnerZOrder | 0x1u | (_isShown ? 0x4u : 0u));
            _lastPosition = position;
        }
        if (_dimensionsChanged || !_isShown)
        {
            _ = InvalidateRect(Handle, IntPtr.Zero, false);
            // Paint this small badge with the border's sample, rather than
            // waiting for WM_PAINT behind another batch of mouse messages.
            _ = UpdateWindow(Handle);
            _dimensionsChanged = false;
        }
        if (!_isShown)
        {
            _ = ShowWindow(Handle, ShowNoActivateCommand);
            _isShown = true;
        }
    }

    public void EnsureVisible(Rectangle selection)
    {
        Update(selection);
        if (!_disposed && _isShown)
            _ = SetWindowPos(Handle, new IntPtr(Topmost), 0, 0, 0, 0,
                NoActivate | NoOwnerZOrder | 0x3u);
    }

    public void SetAvoidRegions(IReadOnlyList<Rectangle> regions)
    {
        if (_disposed) return;
        if (_avoidRegions.Length == regions.Count &&
            regions.Select(ToRect).SequenceEqual(_avoidRegions)) return;
        _avoidRegions = regions.Select(ToRect).ToArray();
        if (_shouldShow) Update(_lastSelection);
    }

    private static WpfRect ToRect(Rectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    public void SetOwner(IntPtr owner)
    {
        if (_disposed || !IsHandleCreated || owner == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowLongPtr(Handle, OwnerIndex, owner);
        _monitorBounds = Rectangle.Empty;
    }

    public void HideSize()
    {
        _shouldShow = false;
        HideBadgeWindow();
    }

    private void HideBadgeWindow()
    {
        if (!_disposed && IsHandleCreated && _isShown)
        {
            _ = ShowWindow(Handle, HideCommand);
            _isShown = false;
        }
    }

    public void SetThemeColors(Color background, Color backgroundEnd, Color border, Color text)
    {
        if (_disposed)
        {
            return;
        }

        if (_borderColor == border && _backgroundColor == background &&
            _backgroundEndColor == backgroundEnd && _textColor == text) return;
        _borderColor = border;
        _backgroundColor = background;
        _backgroundEndColor = backgroundEnd;
        _textColor = text;
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
        using var background = new LinearGradientBrush(ClientRectangle,
            _backgroundColor, _backgroundEndColor, LinearGradientMode.Vertical);
        using var border = new Pen(_borderColor, (float)_scale);
        var bounds = new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        using var path = CreateRoundedRectanglePath(bounds, (int)Math.Round(5 * _scale));
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, _labelText, _labelFont, ClientRectangle, _textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
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
        _dimensionsChanged |= _lastWidth != width || _lastHeight != height;
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
        if (disposing && !_disposed)
        {
            HideSize();
            _labelFont.Dispose();
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
