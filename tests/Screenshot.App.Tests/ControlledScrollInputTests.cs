using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ControlledScrollInputTests
{
    [Fact]
    public void InitialWheelRoutingExcludesTitleAndInputBarInsideSelection()
    {
        var target = new ScrollCaptureTarget(new(1), new(1), new(-800, 300, 720, 600), false);
        var region = ControlledScrollInput.GetRoutingRegion(target);
        Assert.False(region.Contains(-440, 325)); // Title, inside the screenshot.
        Assert.False(region.Contains(-440, 860)); // Message composer.
        Assert.False(region.Contains(100, 600)); // Preview next to the screenshot.
        Assert.True(region.Contains(-440, 550)); // Scrollable messages.
        Assert.Equal(region, ScreenRegion.Intersect(region, target.CaptureRegion));
    }

    [Fact]
    public void RoutingTracksDiscoveredFixedBarsWithoutChangingCaptureBounds()
    {
        var body = new ScreenRegion(100, 200, 600, 600);
        var target = new ScrollCaptureTarget(new(1), new(1), body, false)
        {
            ScrollInputRegionProvider = () => body,
        };
        var initial = ControlledScrollInput.GetRoutingRegion(target);
        body = new ScreenRegion(100, 255, 600, 225); // Large fixed input area below.
        var adjusted = ControlledScrollInput.GetRoutingRegion(target);
        Assert.True(initial.Contains(400, 500));
        Assert.False(adjusted.Contains(400, 500));
        Assert.True(adjusted.Contains(400, 360));
        Assert.Equal(new ScreenRegion(100, 200, 600, 600), target.CaptureRegion);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 29)]
    public void TinySelectionsStillHaveAValidRoutingPoint(int width, int height)
    {
        var target = new ScrollCaptureTarget(new(1), new(1), new(0, 0, width, height), false);
        var region = ControlledScrollInput.GetRoutingRegion(target);
        Assert.False(region.IsEmpty);
        Assert.True(target.CaptureRegion.Contains(region.X + region.Width / 2, region.Y + region.Height / 2));
    }
}
