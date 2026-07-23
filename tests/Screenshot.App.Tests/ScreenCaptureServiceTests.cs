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
}
