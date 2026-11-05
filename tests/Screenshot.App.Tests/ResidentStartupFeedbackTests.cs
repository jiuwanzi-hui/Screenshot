using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class ResidentStartupFeedbackTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void StartupNoticeRendersOffscreenAndReleasesNativePixels(double scale)
    {
        using var frame = ResidentStartupFeedback.Render("已最小化启动", AppTheme.Light, scale);
        Assert.InRange(frame.Region.Width / scale, 100, 400);
        Assert.InRange(frame.Region.Height / scale, 30, 80);
        using (var bitmap = new Bitmap(frame.Region.Width, frame.Region.Height, frame.Stride, PixelFormat.Format32bppRgb, frame.Pixels))
        {
            var colors = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += 2)
                for (var x = 0; x < bitmap.Width; x += 2) colors.Add(bitmap.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count > 8); // Both glyphs and themed chrome were drawn.
        }
        frame.Dispose();
        Assert.Equal(IntPtr.Zero, frame.Pixels);
        Assert.Equal(IntPtr.Zero, frame.DeviceContext);
    }
}
