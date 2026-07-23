using Screenshot.App.Capture;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class PinnedImageManagerTests
{
    [Fact]
    public void ShowsAndReleasesPinnedImages()
    {
        var countAfterPinning = 0;
        var countAfterDisposal = -1;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            CapturedImage? image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            using var manager = new PinnedImageManager();

            try
            {
                manager.Pin(image);
                image = null;
                countAfterPinning = manager.Count;
            }
            finally
            {
                image?.Dispose();
                manager.Dispose();
                countAfterDisposal = manager.Count;
            }
        });

        Assert.Equal(1, countAfterPinning);
        Assert.Equal(0, countAfterDisposal);
    }
}
