using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScreenCaptureServiceTests
{
    [Fact]
    public void CapturesAPixelFromTheVirtualDesktop()
    {
        var virtualDesktop = VirtualScreen.GetBounds();
        var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1);

        using var image = ScreenCaptureService.Capture(region);

        Assert.Equal(1, image.Bitmap.Width);
        Assert.Equal(1, image.Bitmap.Height);
        Assert.Equal(1, image.Preview.PixelWidth);
        Assert.Equal(1, image.Preview.PixelHeight);
        Assert.Equal(region, image.SourceRegion);

        using var clone = image.Clone();
        Assert.Equal(region, clone.SourceRegion);
    }

    [Fact]
    public void CapturesTheCurrentVirtualDesktopAtItsWindowsLayoutSize()
    {
        var virtualDesktop = VirtualScreen.GetBounds();

        using var image = ScreenCaptureService.CaptureAllScreens();

        Assert.Equal(virtualDesktop.Width, image.Bitmap.Width);
        Assert.Equal(virtualDesktop.Height, image.Bitmap.Height);
        Assert.Equal(virtualDesktop.Width, image.Preview.PixelWidth);
        Assert.Equal(virtualDesktop.Height, image.Preview.PixelHeight);
        Assert.Equal(virtualDesktop, image.SourceRegion);
    }

    [Fact]
    public void ComposesOffsetMonitorsAtTheirWindowsPositionsWithWhiteGaps()
    {
        var virtualBounds = new ScreenRegion(-3, -2, 10, 8);
        var leftMonitor = new ScreenRegion(-3, -2, 4, 4);
        var rightMonitor = new ScreenRegion(2, 1, 5, 5);
        using var snapshot = new Bitmap(
            virtualBounds.Width,
            virtualBounds.Height,
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(snapshot))
        {
            graphics.Clear(Color.Magenta);
            graphics.FillRectangle(Brushes.Red, 0, 0, 4, 4);
            graphics.FillRectangle(Brushes.Blue, 5, 3, 5, 5);
        }

        using var composed = ScreenCaptureService.ComposeMonitorLayout(
            snapshot,
            virtualBounds,
            [leftMonitor, rightMonitor]);

        Assert.Equal(Color.Red.ToArgb(), composed.GetPixel(1, 1).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), composed.GetPixel(6, 4).ToArgb());
        Assert.Equal(Color.White.ToArgb(), composed.GetPixel(4, 2).ToArgb());
        Assert.NotEqual(Color.Red.ToArgb(), composed.GetPixel(0, 0).ToArgb());
        Assert.NotEqual(Color.Blue.ToArgb(), composed.GetPixel(5, 3).ToArgb());
    }

    [Fact]
    public void KeepsSingleMonitorPixelsWithoutAddingADivider()
    {
        var virtualBounds = new ScreenRegion(0, 0, 4, 3);
        using var snapshot = new Bitmap(
            virtualBounds.Width,
            virtualBounds.Height,
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(snapshot))
        {
            graphics.Clear(Color.Lime);
        }

        using var composed = ScreenCaptureService.ComposeMonitorLayout(
            snapshot,
            virtualBounds,
            [virtualBounds]);

        Assert.Equal(Color.Lime.ToArgb(), composed.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), composed.GetPixel(3, 2).ToArgb());
    }
}
