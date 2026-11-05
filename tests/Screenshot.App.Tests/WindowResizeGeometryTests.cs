using System.Windows;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class WindowResizeGeometryTests
{
    [Theory]
    [InlineData("TopLeft", 15, 20, -785, -380, 485, 330)]
    [InlineData("BottomRight", 15, 20, -800, -400, 515, 370)]
    [InlineData("Left", 15, 20, -785, -400, 485, 350)]
    [InlineData("Bottom", 15, 20, -800, -400, 500, 370)]
    public void KeepsTheOppositeEdgesFixedIncludingNegativeMonitorCoordinates(string edge,
        double dx, double dy, double x, double y, double width, double height)
    {
        var result = WindowResizeGeometry.Calculate(new Rect(-800, -400, 500, 350), edge,
            new Vector(dx, dy), 200, 100, 1000, 1000);
        Assert.Equal(new Rect(x, y, width, height), result);
    }

    [Fact]
    public void ReturningFromMinimumSizeUsesTheOriginalPointerAnchor()
    {
        var start = new Rect(100, 100, 500, 350);
        var clamped = WindowResizeGeometry.Calculate(start, "TopLeft", new Vector(450, 300), 200, 100, 1000, 1000);
        Assert.Equal(new Rect(400, 350, 200, 100), clamped);
        var returned = WindowResizeGeometry.Calculate(start, "TopLeft", new Vector(150, 100), 200, 100, 1000, 1000);
        Assert.Equal(new Rect(250, 200, 350, 250), returned);
    }
}
