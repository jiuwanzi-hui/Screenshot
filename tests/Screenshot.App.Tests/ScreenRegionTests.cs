using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScreenRegionTests
{
    [Fact]
    public void ContainsOnlyPointsInsideTheRegion()
    {
        var region = new ScreenRegion(10, 20, 30, 40);

        Assert.True(region.Contains(10, 20));
        Assert.True(region.Contains(39, 59));
        Assert.False(region.Contains(9, 20));
        Assert.False(region.Contains(40, 59));
        Assert.False(region.Contains(39, 60));
    }

    [Fact]
    public void NormalizesASelectionDraggedInAnyDirection()
    {
        var region = ScreenRegion.FromCorners(420, 320, 120, 80);

        Assert.Equal(120, region.X);
        Assert.Equal(80, region.Y);
        Assert.Equal(300, region.Width);
        Assert.Equal(240, region.Height);
    }

    [Fact]
    public void ReportsANonEmptyVirtualDesktop()
    {
        var virtualDesktop = VirtualScreen.GetBounds();

        Assert.False(virtualDesktop.IsEmpty);
    }

    [Fact]
    public void IntersectsASelectionWithAWindowRegion()
    {
        var windowRegion = new ScreenRegion(100, 80, 500, 400);
        var selection = new ScreenRegion(450, 300, 300, 240);

        var intersection = ScreenRegion.Intersect(windowRegion, selection);

        Assert.Equal(new ScreenRegion(450, 300, 150, 180), intersection);
        Assert.True(ScreenRegion.Intersect(
            windowRegion,
            new ScreenRegion(800, 800, 20, 20)).IsEmpty);
    }
}
