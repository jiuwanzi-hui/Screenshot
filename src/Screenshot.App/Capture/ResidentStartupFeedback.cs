using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Capture;

// A short, non-activating native notice. A tray-only launch must not create a
// complete WPF feature process and transfer global input just to show a toast.
internal sealed class ResidentStartupFeedback : NativeWindow, IDisposable
{
    private const string CaptureFeedbackProperty = "SnapCut.CompletionFeedback.v1";
    private NativeDesktopFrame? _frame;
    private bool _disposed;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Closed => _closed.Task;

    internal ResidentStartupFeedback(string text, AppTheme theme, ScreenRegion? captureRegion = null)
    {
        try
        {
            var area = captureRegion is { IsEmpty: false } capture
                ? Screen.FromRectangle(new(capture.X, capture.Y, capture.Width, capture.Height)).Bounds
                : Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            var scale = MonitorGeometryService.GetDpiScale(area).X;
            _frame = Render(UiLocalization.Get(text), theme, scale);
            var size = _frame.Region;
            CreateHandle(new CreateParams { Caption = captureRegion is null ? "SnapCut" : "SnapCut " + text, Style = unchecked((int)0x80000000),
                ExStyle = 0x080000A8, X = area.X + (area.Width - size.Width) / 2,
                Y = area.Y + (area.Height - size.Height) / 2, Width = size.Width, Height = size.Height });
            var corner = Math.Max(2, (int)Math.Round(20 * scale));
            var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, corner, corner);
            if (region != IntPtr.Zero && SetWindowRgn(Handle, region, false) == 0) _ = DeleteObject(region);
            // This notice must also be visible through remote-control capture.
            // Remove it explicitly before our next capture instead of WDA_EXCLUDEFROMCAPTURE.
            if (captureRegion is not null && !SetProp(Handle, CaptureFeedbackProperty, new IntPtr(1)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _ = ShowWindow(Handle, 8);
            _ = UpdateWindow(Handle);
            if (SetTimer(Handle, 1, captureRegion is null ? 1500u : 800u, IntPtr.Zero) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    internal static NativeDesktopFrame Render(string text, AppTheme theme, double scale)
    {
        using var font = new Font("Microsoft YaHei UI", (float)(14 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        var measured = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var width = measured.Width + (int)Math.Ceiling(55 * scale);
        var height = measured.Height + (int)Math.Ceiling(22 * scale);
        var frame = new NativeDesktopFrame(new(0, 0, width, height));
        try
        {
            using var bitmap = new Bitmap(width, height, frame.Stride, PixelFormat.Format32bppRgb, frame.Pixels);
            using var graphics = Graphics.FromImage(bitmap);
            var colors = AppThemeManager.GetChromeColors(theme);
            var background = ColorTranslator.FromHtml(colors.Background);
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(ColorTranslator.FromHtml(colors.Border), (float)scale);
            using var path = new GraphicsPath();
            var radius = (float)(10 * scale);
            var diameter = radius * 2;
            path.AddArc(.5f, .5f, diameter, diameter, 180, 90);
            path.AddArc(width - diameter - 1, .5f, diameter, diameter, 270, 90);
            path.AddArc(width - diameter - 1, height - diameter - 1, diameter, diameter, 0, 90);
            path.AddArc(.5f, height - diameter - 1, diameter, diameter, 90, 90);
            path.CloseFigure();
            graphics.DrawPath(border, path);
            using var dot = new SolidBrush(ColorTranslator.FromHtml(AppThemeManager.GetSelectionBorderColor(theme)));
            graphics.FillEllipse(dot, (float)(18 * scale), height / 2f - (float)(4.5 * scale),
                (float)(9 * scale), (float)(9 * scale));
            TextRenderer.DrawText(graphics, text, font,
                new Rectangle((int)Math.Round(37 * scale), 0, measured.Width + 2, height),
                ColorTranslator.FromHtml(colors.Foreground), background,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            graphics.Flush();
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x14: message.Result = new IntPtr(1); return;
            case 0x84: message.Result = new IntPtr(-1); return;
            case 0x21: message.Result = new IntPtr(3); return; // MA_NOACTIVATE
            case 0x113: case 0x10: Dispose(); return;
            case 0x317: case 0x318: // WM_PRINT / WM_PRINTCLIENT (native thumbnails and accessibility)
                PaintTo(message.WParam); return;
            case 0xF:
                var dc = BeginPaint(Handle, out var paint);
                try { PaintTo(dc); }
                finally { _ = EndPaint(Handle, ref paint); }
                return;
        }
        base.WndProc(ref message);
    }

    private void PaintTo(IntPtr dc)
    {
        if (dc != IntPtr.Zero && _frame is { } frame)
            _ = NativeDesktopFrame.BitBlt(dc, 0, 0, frame.Region.Width, frame.Region.Height,
                frame.DeviceContext, 0, 0, 0x00CC0020);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero) { _ = RemoveProp(Handle, CaptureFeedbackProperty); _ = KillTimer(Handle, 1); DestroyHandle(); }
        _frame?.Dispose(); _frame = null;
        _closed.TrySetResult();
    }

    internal static void DismissBeforeCapture()
    {
        // The resident captures the next first frame while the previous worker
        // can still own a notice. Match only our explicitly marked HWNDs.
        var notices = new List<IntPtr>();
        _ = EnumWindows((window, _) =>
        {
            if (GetProp(window, CaptureFeedbackProperty) != IntPtr.Zero) notices.Add(window);
            return true;
        }, IntPtr.Zero);
        foreach (var notice in notices)
        {
            _ = SendMessageTimeout(notice, 0x10, IntPtr.Zero, IntPtr.Zero, 0x3, 100, out _);
            if (IsWindowVisible(notice))
                throw new InvalidOperationException("上次截图提示尚未关闭，请重试。");
        }
        if (notices.Count > 0) _ = DwmFlush();
    }

    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public IntPtr Dc; public int Erase; public int Left, Top, Right, Bottom; public int Restore, Incremental;
        public long Reserved1, Reserved2, Reserved3, Reserved4;
    }
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(IntPtr window, nuint id, uint ms, IntPtr callback);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, nuint id);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}
