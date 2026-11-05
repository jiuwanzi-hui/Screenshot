using System.Drawing;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Capture;

internal readonly record struct ResidentSelectionVisual(
    ScreenRegion Region, Point Cursor, Rectangle Monitor, bool Selected, bool Compact);

// All intermediate drawing stays in a session-owned DIB. The HWND receives only
// the completed frame; neither the unmasked source nor an erased picker is shown.
internal sealed class ResidentSelectionRenderer : IDisposable
{
    private readonly NativeDesktopFrame _source;
    private NativeDesktopFrame? _buffer, _shade;
    private IntPtr _background, _foreground, _panelBorder, _selectionBorder, _font, _sizeFont;
    private IntPtr _pickerValueFont, _pickerHintFont, _pickerAccentPen, _black, _white;
    private readonly int _pickerTextWidth;
    private int _measuredSizeLength;
    private Size _sizeBadgeSize;
    private readonly double _scale;
    private readonly uint _textColor;
    private ResidentSelectionVisual? _painted;
    private bool _disposed;
    internal NativeDesktopFrame Buffer => _buffer ?? throw new ObjectDisposedException(nameof(ResidentSelectionRenderer));
    private Rectangle Surface => new(0, 0, _source.Region.Width, _source.Region.Height);

    internal ResidentSelectionRenderer(NativeDesktopFrame source, AppTheme theme, double scale)
    {
        _source = source;
        _scale = scale;
        var colors = AppThemeManager.GetChromeColors(theme);
        _textColor = ToColorRef(ColorTranslator.FromHtml(colors.Foreground));
        try
        {
            _buffer = new NativeDesktopFrame(source.Region);
            _shade = new NativeDesktopFrame(new(0, 0, 1, 1));
            Marshal.WriteInt32(_shade.Pixels, 0);
            _background = CreateSolidBrush(ToColorRef(ColorTranslator.FromHtml(colors.Background)));
            _foreground = CreateSolidBrush(_textColor);
            _panelBorder = CreateSolidBrush(ToColorRef(ColorTranslator.FromHtml(colors.Border)));
            _selectionBorder = CreateSolidBrush(ToColorRef(ColorTranslator.FromHtml(AppThemeManager.GetSelectionBorderColor(theme))));
            _font = CreateFontW(-(int)Math.Round(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            _sizeFont = CreateFontW(-(int)Math.Round(11 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _pickerValueFont = CreateFontW(-(int)Math.Round(13 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            _pickerHintFont = CreateFontW(-(int)Math.Round(10 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
            _pickerAccentPen = CreatePen(0, Math.Max(1, (int)Math.Round(scale)), ToColorRef(ColorTranslator.FromHtml(AppThemeManager.GetSelectionBorderColor(theme))));
            _black = CreateSolidBrush(0); _white = CreateSolidBrush(0xffffff);
            if (new[] { _background, _foreground, _panelBorder, _selectionBorder, _font, _sizeFont,
                    _pickerValueFont, _pickerHintFont, _pickerAccentPen, _black, _white }.Any(handle => handle == IntPtr.Zero))
                throw new Win32Exception("无法创建截图绘图资源。");
            var dc = _buffer.DeviceContext;
            var previous = SelectObject(dc, _pickerHintFont);
            _ = GetTextExtentPoint32W(dc, UiLocalization.Get("C 复制  ·  Esc/右键退出"), UiLocalization.Get("C 复制  ·  Esc/右键退出").Length, out var hint);
            _ = SelectObject(dc, _sizeFont);
            const string rgb = "RGB(255, 255, 255)";
            _ = GetTextExtentPoint32W(dc, rgb, rgb.Length, out var rgbSize);
            _ = SelectObject(dc, previous);
            _pickerTextWidth = Math.Max(hint.Width, rgbSize.Width);
        }
        catch { Dispose(); throw; }
    }

    internal Rectangle GetDamage(ResidentSelectionVisual visual)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_painted is not { } previous) return Surface;
        if (previous == visual) return Rectangle.Empty;
        var damage = Rectangle.Union(GetBadgeDamage(previous), GetBadgeDamage(visual));
        if (previous.Region != visual.Region || previous.Selected != visual.Selected)
        {
            if (!previous.Region.IsEmpty) damage = Rectangle.Union(damage, Local(previous.Region));
            if (!visual.Region.IsEmpty) damage = Rectangle.Union(damage, Local(visual.Region));
        }
        return Rectangle.Intersect(Surface, damage);
    }

    private Rectangle GetBadgeDamage(ResidentSelectionVisual visual)
    {
        var bounds = GetBadgeBounds(visual);
        if (!bounds.IsEmpty && !visual.Compact)
        {
            // RoundRect centers its pen on the outline. At high DPI the wide
            // pen extends beyond the layout bounds; include both old and new
            // strokes when restoring pixels and invalidating the HWND.
            var penWidth = Math.Max(1, (int)Math.Round(_scale));
            var overhang = (penWidth + 1) / 2;
            bounds.Inflate(overhang, overhang);
        }
        return bounds;
    }

    internal Rectangle Render(ResidentSelectionVisual visual)
    {
        var damage = GetDamage(visual);
        if (damage.IsEmpty) return damage;
        var dc = Buffer.DeviceContext;
        var saved = SaveDC(dc);
        if (saved == 0) throw new Win32Exception("无法保存截图绘图状态。");
        var restored = false;
        try
        {
            if (IntersectClipRect(dc, damage.Left, damage.Top, damage.Right, damage.Bottom) == 0)
                throw new Win32Exception("无法设置截图重绘区域。");
            CopySource(dc, damage);
            Dim(dc, damage, 71); // Match the normal overlay's neutral 28% mask.
            if (!visual.Region.IsEmpty)
            {
                var selection = Rectangle.Intersect(Surface, Local(visual.Region));
                // Hover only previews a window boundary. The idle mask remains;
                // only an actual drag/click opens a clear selection in the mask.
                if (visual.Selected) CopySource(dc, selection);
                else Dim(dc, selection, 36); // WindowSnapRectangle's #24000000 fill.
                var thickness = visual.Selected ? NativeSelectionBorder.Thickness : Math.Max(3, (int)Math.Round(3 * _scale));
                for (var i = 0; i < thickness && selection.Width > 0 && selection.Height > 0; i++)
                {
                    var edge = new NativeRect(selection);
                    if (_selectionBorder == IntPtr.Zero || FrameRect(dc, ref edge, _selectionBorder) == 0)
                        throw new Win32Exception("无法绘制截图边框。");
                    selection.Inflate(-1, -1);
                }
            }
            DrawBadge(dc, visual);
            // Synchronize GDI writes before the completed DIB is read/presented.
            if (!GdiFlush()) throw new Win32Exception("截图绘制未完成。");
            _painted = visual;
        }
        catch
        {
            // Never treat a partially restored/dimmed frame as a valid base
            // for later dirty-region updates; it would retain trails.
            _painted = null;
            throw;
        }
        finally
        {
            restored = RestoreDC(dc, saved);
            if (!restored) _painted = null;
        }
        if (!restored) throw new Win32Exception("无法恢复截图绘图状态。");
        return damage;
    }

    private Rectangle Local(ScreenRegion region) => new(region.X - _source.Region.X,
        region.Y - _source.Region.Y, region.Width, region.Height);

    private void CopySource(IntPtr dc, Rectangle area)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
        if (!NativeDesktopFrame.BitBlt(dc, area.X, area.Y, area.Width, area.Height,
                _source.DeviceContext, area.X, area.Y, 0x00CC0020))
            throw new Win32Exception("无法恢复截图原始像素。");
    }

    private void Dim(IntPtr dc, Rectangle area, byte alpha)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
        if (!AlphaBlend(dc, area.X, area.Y, area.Width, area.Height,
                _shade!.DeviceContext, 0, 0, 1, 1, (uint)alpha << 16))
            throw new Win32Exception("无法绘制截图遮罩。");
    }

    internal Rectangle GetBadgeBounds(ResidentSelectionVisual visual)
    {
        if (visual.Compact && !visual.Region.IsEmpty)
        {
            var text = $"{visual.Region.Width} × {visual.Region.Height}";
            if (_measuredSizeLength != text.Length)
            {
                var dc = Buffer.DeviceContext;
                var previousFont = SelectObject(dc, _sizeFont);
                try
                {
                    _ = GetTextExtentPoint32W(dc, text, text.Length, out var measured);
                    _sizeBadgeSize = new(Math.Max((int)Math.Ceiling(52 * _scale),
                        measured.Width + (int)Math.Ceiling(16 * _scale)), (int)Math.Ceiling(22 * _scale));
                    _measuredSizeLength = text.Length;
                }
                finally { _ = SelectObject(dc, previousFont); }
            }
            // Use the editor's placement policy even on the cold resident path.
            // The badge belongs to the selection, not the moving pointer.
            var region = visual.Region;
            var screen = Rectangle.Intersect(visual.Monitor,
                new(_source.Region.X, _source.Region.Y, _source.Region.Width, _source.Region.Height));
            var placement = SelectionSizeBadgeLayout.Place(
                new(region.X, region.Y, region.Width, region.Height),
                new(_sizeBadgeSize.Width, _sizeBadgeSize.Height),
                new(screen.X, screen.Y, screen.Width, screen.Height), [], Math.Ceiling(6 * _scale));
            return placement.IsEmpty ? Rectangle.Empty : new(
                (int)Math.Round(placement.X) - _source.Region.X,
                (int)Math.Round(placement.Y) - _source.Region.Y,
                _sizeBadgeSize.Width, _sizeBadgeSize.Height);
        }
        var width = visual.Compact
            ? (int)Math.Round((UiLocalization.Language == "zh-Hans" ? 162 : visual.Region.IsEmpty ? 270 : 162) * _scale)
            : (int)Math.Ceiling(135 * _scale) + _pickerTextWidth;
        var height = (int)Math.Round((visual.Compact ? 29 : 124) * _scale);
        var monitor = visual.Monitor;
        var x = Math.Clamp(visual.Cursor.X + 20, monitor.Left, Math.Max(monitor.Left, monitor.Right - width)) - _source.Region.X;
        var y = Math.Clamp(visual.Cursor.Y + 24, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - height)) - _source.Region.Y;
        return new(x, y, width, height);
    }

    private void DrawBadge(IntPtr dc, ResidentSelectionVisual visual)
    {
        var bounds = GetBadgeBounds(visual);
        if (bounds.IsEmpty) return;
        if (!visual.Compact) { DrawColorPicker(dc, visual, bounds); return; }
        var x = bounds.X; var y = bounds.Y; var width = bounds.Width;
        var box = new NativeRect(bounds);
        _ = FillRect(dc, ref box, _background);
        _ = FrameRect(dc, ref box, _panelBorder);
        _ = SelectObject(dc, visual.Compact && !visual.Region.IsEmpty ? _sizeFont : _font);
        _ = SetBkMode(dc, 1);
        _ = SetTextColor(dc, _textColor);
        var text = visual.Region.IsEmpty ? UiLocalization.Get("拖动框选 · Esc 取消") : $"{visual.Region.Width} × {visual.Region.Height}";
        var label = new NativeRect(visual.Compact && !visual.Region.IsEmpty
            ? new Rectangle(x + 5, y + 1, width - 10, bounds.Height - 2)
            : new Rectangle(x + 5, y + 4, width - 10, box.Bottom - 3 - (y + 4)));
        _ = DrawTextW(dc, text, text.Length, ref label, 0x25);
    }

    private void DrawColorPicker(IntPtr dc, ResidentSelectionVisual visual, Rectangle bounds)
    {
        int S(double value) => (int)Math.Round(value * _scale);
        var oldBrush = SelectObject(dc, _background);
        var oldPen = SelectObject(dc, _pickerAccentPen);
        _ = RoundRect(dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, S(10), S(10));
        _ = SelectObject(dc, oldBrush); _ = SelectObject(dc, oldPen);
        var surface = _source.Region;
        var size = S(112); var x = bounds.X + S(6); var y = bounds.Y + S(6);
        // Keep the sampled cursor pixel at the exact center at screen edges.
        _ = SetStretchBltMode(dc, 3);
        var centerX = visual.Cursor.X - surface.X;
        var centerY = visual.Cursor.Y - surface.Y;
        if (centerX >= 12 && centerY >= 12 && centerX + 12 < surface.Width && centerY + 12 < surface.Height)
            _ = StretchBlt(dc, x, y, size, size,
                _source.DeviceContext, centerX - 12, centerY - 12, 25, 25, 0x00CC0020);
        else for (var row = 0; row < 25; row++)
            for (var column = 0; column < 25; column++)
            {
                var sx = Math.Clamp(centerX + column - 12, 0, surface.Width - 1);
                var sy = Math.Clamp(centerY + row - 12, 0, surface.Height - 1);
                var left = column * size / 25; var top = row * size / 25;
                _ = StretchBlt(dc, x + left, y + top,
                    (column + 1) * size / 25 - left, (row + 1) * size / 25 - top,
                    _source.DeviceContext, sx, sy, 1, 1, 0x00CC0020);
            }
        var crossX = x + size / 2; var crossY = y + size / 2;
        var horizontal = new NativeRect(new(crossX - S(7), crossY, S(15), Math.Max(1, S(1))));
        var vertical = new NativeRect(new(crossX, crossY - S(7), Math.Max(1, S(1)), S(15)));
        _ = FillRect(dc, ref horizontal, _white); _ = FillRect(dc, ref vertical, _white);
        var center = new NativeRect(new(crossX - S(2), crossY - S(2), S(5), S(5)));
        _ = FrameRect(dc, ref center, _black);
        var color = _source.PixelAt(visual.Cursor.X, visual.Cursor.Y);
        _ = SetBkMode(dc, 1); _ = SetTextColor(dc, _textColor);
        void Label(string text, int top, int height, IntPtr font)
        {
            _ = SelectObject(dc, font);
            var rectangle = new NativeRect(new(bounds.X + S(126), bounds.Y + S(top), _pickerTextWidth + S(3), S(height)));
            _ = DrawTextW(dc, text, text.Length, ref rectangle, 0x24);
        }
        Label("#" + color.ToString("X6", CultureInfo.InvariantCulture), 32, 20, _pickerValueFont);
        Label($"RGB({(color >> 16) & 255}, {(color >> 8) & 255}, {color & 255})", 55, 17, _sizeFont);
        Label(UiLocalization.Get("C 复制  ·  Esc/右键退出"), 79, 16, _pickerHintFont);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer?.Dispose(); _buffer = null;
        _shade?.Dispose(); _shade = null;
        foreach (var handle in new[] { _background, _foreground, _panelBorder, _selectionBorder, _font, _sizeFont,
            _pickerValueFont, _pickerHintFont, _pickerAccentPen, _black, _white })
            if (handle != IntPtr.Zero) _ = DeleteObject(handle);
        _background = _foreground = _panelBorder = _selectionBorder = _font = _sizeFont = IntPtr.Zero;
    }

    private static uint ToColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect(Rectangle rect)
    { public int Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom; }
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr dc, ref NativeRect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref NativeRect rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Size size);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dc, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("msimg32.dll")] private static extern bool AlphaBlend(IntPtr dc, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint blend);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
}
