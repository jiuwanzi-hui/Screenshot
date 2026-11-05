using System.Drawing;

namespace Screenshot.App.Capture;

public enum CaptureAspectRatio
{
    Free,
    Ratio16x9,
    Ratio16x10,
    Ratio3x2,
    Ratio4x3,
    Ratio5x4,
    Ratio1x1,
    Ratio9x16,
    Ratio10x16,
    Ratio2x3,
    Ratio19x5x9,
    Ratio20x9,
}

public static class CaptureAspectRatioHelper
{
    public static string GetLabel(CaptureAspectRatio ratio) => ratio switch
    {
        CaptureAspectRatio.Ratio16x9 => "16:9（横屏）",
        CaptureAspectRatio.Ratio16x10 => "16:10（横屏）",
        CaptureAspectRatio.Ratio3x2 => "3:2（横屏）",
        CaptureAspectRatio.Ratio4x3 => "4:3（横屏）",
        CaptureAspectRatio.Ratio5x4 => "5:4（横屏）",
        CaptureAspectRatio.Ratio1x1 => "1:1（正方形）",
        CaptureAspectRatio.Ratio9x16 => "9:16（手机竖屏）",
        CaptureAspectRatio.Ratio10x16 => "10:16（竖屏）",
        CaptureAspectRatio.Ratio2x3 => "2:3（竖屏）",
        CaptureAspectRatio.Ratio19x5x9 => "19.5:9（手机）",
        CaptureAspectRatio.Ratio20x9 => "20:9（手机）",
        _ => "自由比例",
    };

    public static double? GetValue(CaptureAspectRatio ratio) => ratio switch
    {
        CaptureAspectRatio.Ratio16x9 => 16d / 9,
        CaptureAspectRatio.Ratio16x10 => 16d / 10,
        CaptureAspectRatio.Ratio3x2 => 3d / 2,
        CaptureAspectRatio.Ratio4x3 => 4d / 3,
        CaptureAspectRatio.Ratio5x4 => 5d / 4,
        CaptureAspectRatio.Ratio1x1 => 1,
        CaptureAspectRatio.Ratio9x16 => 9d / 16,
        CaptureAspectRatio.Ratio10x16 => 10d / 16,
        CaptureAspectRatio.Ratio2x3 => 2d / 3,
        CaptureAspectRatio.Ratio19x5x9 => 19.5d / 9,
        CaptureAspectRatio.Ratio20x9 => 20d / 9,
        _ => null,
    };

    // Work in physical pixels so the toolbar and the native resize handles
    // agree, including on scaled displays and displays left of the primary.
    public static bool TryFitSelection(
        Rectangle selection,
        Rectangle surface,
        CaptureAspectRatio mode,
        Rectangle? protectedBounds,
        out Rectangle result)
    {
        result = selection;
        if (!Enum.IsDefined(mode) || selection.Width < 2 || selection.Height < 2 ||
            !surface.Contains(selection))
            return false;
        if (GetValue(mode) is not { } ratio)
            return true;

        var required = protectedBounds.GetValueOrDefault();
        var hasProtection = required.Width > 0 && required.Height > 0;
        if (hasProtection && !surface.Contains(required))
            return false;

        var minWidth = Math.Max(2, 2 * ratio);
        if (hasProtection)
            minWidth = Math.Max(minWidth, Math.Max(required.Width, required.Height * ratio));
        var maxWidth = Math.Min(surface.Width, surface.Height * ratio);
        if (minWidth > maxWidth)
            return false;

        // Prefer a centered crop. Enlarge only when necessary to keep edits.
        var desiredWidth = Math.Clamp(Math.Min(selection.Width, selection.Height * ratio), minWidth, maxWidth);
        var width = Math.Clamp((int)Math.Round(desiredWidth), 2, surface.Width);
        var height = Math.Clamp((int)Math.Round(desiredWidth / ratio), 2, surface.Height);
        var minX = surface.Left;
        var maxX = surface.Right - width;
        var minY = surface.Top;
        var maxY = surface.Bottom - height;
        if (hasProtection)
        {
            minX = Math.Max(minX, required.Right - width);
            maxX = Math.Min(maxX, required.Left);
            minY = Math.Max(minY, required.Bottom - height);
            maxY = Math.Min(maxY, required.Top);
        }
        if (minX > maxX || minY > maxY)
            return false;

        result = new Rectangle(
            Math.Clamp(selection.Left + (int)Math.Round((selection.Width - width) / 2d), minX, maxX),
            Math.Clamp(selection.Top + (int)Math.Round((selection.Height - height) / 2d), minY, maxY),
            width, height);
        return true;
    }

    public static Rectangle ConstrainFromAnchor(
        Point start,
        Point end,
        Rectangle surface,
        double? ratio)
    {
        var left = Math.Clamp(Math.Min(start.X, end.X), surface.Left, surface.Right);
        var top = Math.Clamp(Math.Min(start.Y, end.Y), surface.Top, surface.Bottom);
        var right = Math.Clamp(Math.Max(start.X, end.X), surface.Left, surface.Right);
        var bottom = Math.Clamp(Math.Max(start.Y, end.Y), surface.Top, surface.Bottom);
        if (ratio is not > 0 || right <= left || bottom <= top)
            return new Rectangle(left, top, right - left, bottom - top);

        var width = right - left;
        var height = bottom - top;
        if (width / (double)height > ratio.Value)
            width = (int)Math.Round(height * ratio.Value);
        else
            height = (int)Math.Round(width / ratio.Value);
        width = Math.Max(1, Math.Min(width, surface.Width));
        height = Math.Max(1, Math.Min(height, surface.Height));
        if (end.X < start.X) left = right - width;
        else right = left + width;
        if (end.Y < start.Y) top = bottom - height;
        else bottom = top + height;
        if (right > surface.Right) { right = surface.Right; left = right - width; }
        if (bottom > surface.Bottom) { bottom = surface.Bottom; top = bottom - height; }
        if (left < surface.Left) { left = surface.Left; right = left + width; }
        if (top < surface.Top) { top = surface.Top; bottom = top + height; }
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
