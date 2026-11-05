using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Image = System.Windows.Controls.Image;

namespace Screenshot.App.Tests;

public sealed class CapturePixelFidelityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void FrozenDesktopDisplaysOriginalPixelsAtMonitorDpi(double dpi)
    {
        WpfTestHost.Invoke(() =>
        {
            const int width = 83, height = 47;
            var expected = new byte[width * height * 4];
            for (var i = 0; i < expected.Length; i += 4)
            {
                expected[i] = expected[i + 1] = expected[i + 2] = (byte)(i / 4 % 2 * 255);
                expected[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, expected, width * 4);
            bitmap.Freeze();
            using var overlay = new CaptureOverlayWindow(null, deferInitialScreenCapture: true);
            try
            {
                var rendered = RenderFrozenDesktop(overlay, bitmap, dpi);
                var actual = new byte[expected.Length];
                rendered.CopyPixels(actual, width * 4, 0);
                Assert.Equal(expected, actual);
            }
            finally { overlay.Close(); }
        });
    }

    internal static BitmapSource RenderFrozenDesktop(CaptureOverlayWindow overlay, BitmapSource bitmap, double dpi)
    {
        var image = (Image)overlay.FindName("FrozenScreenImage");
        ((Panel)image.Parent).Children.Remove(image);
        image.Source = bitmap;
        var root = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.Children.Add(image);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
        var size = new Size(bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var rendered = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight,
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        rendered.Render(root);
        rendered.Freeze();
        return rendered;
    }
}
