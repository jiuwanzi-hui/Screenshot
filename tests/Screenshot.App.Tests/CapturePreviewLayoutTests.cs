using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CapturePreviewLayoutTests
{
    [Fact]
    public void PreviewUsesRightSideWhenItFitsOnSourceMonitor()
    {
        var result = CapturePreviewWindow.CalculateAdjacentBounds(
            new System.Drawing.Rectangle(-1500, 200, 400, 300),
            new System.Drawing.Size(300, 500),
            new System.Drawing.Rectangle(-1920, 0, 1920, 1040),
            gap: 12);

        Assert.Equal(-1088, result.X);
        Assert.Equal(100, result.Y);
    }

    [Fact]
    public void PreviewFallsBackToLeftSideBeforeLeavingMonitor()
    {
        var result = CapturePreviewWindow.CalculateAdjacentBounds(
            new System.Drawing.Rectangle(1450, 200, 400, 300),
            new System.Drawing.Size(300, 500),
            new System.Drawing.Rectangle(0, 0, 1920, 1040),
            gap: 12);

        Assert.Equal(1138, result.X);
        Assert.Equal(100, result.Y);
    }

    [Fact]
    public void PreviewStaysInsideSmallWorkAreaWhenNeitherSideFits()
    {
        var workArea = new System.Drawing.Rectangle(1920, -200, 800, 600);
        var result = CapturePreviewWindow.CalculateAdjacentBounds(
            new System.Drawing.Rectangle(2100, -100, 500, 400),
            new System.Drawing.Size(700, 560),
            workArea,
            gap: 12);

        Assert.True(workArea.Contains(result));
    }
}
