using System.Drawing;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureBorderGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1920, -1080)]
    [InlineData(1920, 0)]
    public void EveryBorderStripRemainsOnScreenAtAllEdges(int x, int y)
    {
        var screen = new Rectangle(x, y, 1920, 1080);
        foreach (var selection in new[] { screen, new Rectangle(x, y, 400, 300),
            new Rectangle(x + 1520, y + 780, 400, 300), new Rectangle(x, y, 1, 1) })
        {
            var bounds = ScrollCaptureBorderGeometry.GetBounds(selection, screen);
            Assert.True(screen.Contains(bounds));
            Assert.True(bounds.Contains(selection));
            foreach (var strip in NativeSelectionBorder.GetStrips(bounds))
            {
                if (strip.Width > 0 && strip.Height > 0) Assert.True(screen.Contains(strip));
            }
            var hole = ScrollCaptureBorderGeometry.GetInputHole(selection);
            Assert.True(selection.Contains(hole));
            Assert.True(hole.Width > 0 && hole.Height > 0);
        }
    }
}
