using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureHistoryServiceTests
{
    [Fact]
    public void KeepsOnlyTheConfiguredNumberOfHistoryItems()
    {
        var virtualDesktop = VirtualScreen.GetBounds();
        var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1);
        var history = new CaptureHistoryService();

        using var firstImage = ScreenCaptureService.Capture(region);
        using var secondImage = ScreenCaptureService.Capture(region);

        _ = history.Add(firstImage, capacity: 1);
        var retainedItem = history.Add(secondImage, capacity: 1);

        Assert.NotNull(retainedItem);
        Assert.Single(history.Items);
        Assert.Same(retainedItem, history.Items[0]);
        Assert.Equal(1, retainedItem.Thumbnail.PixelWidth);
        Assert.Equal(1, retainedItem.Thumbnail.PixelHeight);
    }
}
