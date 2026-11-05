using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace Screenshot.App.Pin;

/// <summary>Draws from the original bitmap on physical pixel boundaries.</summary>
public sealed class PixelAlignedImage : Image
{
    protected override void OnRender(DrawingContext dc)
    {
        if (Source is not BitmapSource bitmap || Stretch != Stretch.Uniform)
        {
            base.OnRender(dc);
            return;
        }
        var bounds = GetPixelAlignedBounds(bitmap.PixelWidth, bitmap.PixelHeight,
            RenderSize, VisualTreeHelper.GetDpi(this));
        if (!bounds.IsEmpty) dc.DrawImage(bitmap, bounds);
    }

    internal static Rect GetPixelAlignedBounds(int pixelWidth, int pixelHeight, Size available, DpiScale dpi)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || available.Width <= 0 || available.Height <= 0)
            return Rect.Empty;
        var width = Math.Round(available.Width * dpi.DpiScaleX);
        var height = Math.Round(available.Height * dpi.DpiScaleY);
        var scale = Math.Min(width / pixelWidth, height / pixelHeight);
        // WPF rounds the native window upwards. A fractional-DPI conversion can
        // leave one extra pixel, even for an exact native-size pin. Treat it as
        // padding: fitting into it resamples every subsequent screenshot.
        if (width >= pixelWidth && width <= pixelWidth + 1 &&
            height >= pixelHeight && height <= pixelHeight + 1) scale = 1;
        var drawnWidth = Math.Max(1, Math.Round(pixelWidth * scale));
        var drawnHeight = Math.Max(1, Math.Round(pixelHeight * scale));
        return new Rect(Math.Round((width - drawnWidth) / 2) / dpi.DpiScaleX,
            Math.Round((height - drawnHeight) / 2) / dpi.DpiScaleY,
            drawnWidth / dpi.DpiScaleX, drawnHeight / dpi.DpiScaleY);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }
}
