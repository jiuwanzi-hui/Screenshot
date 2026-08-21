using Avalonia;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Tests;

public sealed class SelectionGeometryTests
{
    [Fact]
    public void MapsOverlayCoordinatesIntoOffsetDisplaySpace()
    {
        var result = SelectionGeometry.ToGlobalRect(
            new Rect(100, 50, 400, 300),
            new Size(1440, 900),
            new CGRect(-1440, 120, 2880, 1800));

        Assert.Equal(-1240, result.Left, precision: 6);
        Assert.Equal(220, result.Top, precision: 6);
        Assert.Equal(800, result.Size.Width, precision: 6);
        Assert.Equal(600, result.Size.Height, precision: 6);
    }

    [Fact]
    public void RejectsAnUnmeasuredOverlay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionGeometry.ToGlobalRect(
                new Rect(0, 0, 100, 100),
                default,
                new CGRect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void ConvertsSelectionPointsToRetinaPixels()
    {
        var result = SelectionGeometry.ToPixelSize(
            new Rect(50, 40, 320, 180),
            new Size(1440, 900),
            displayPixelWidth: 2880,
            displayPixelHeight: 1800);

        Assert.Equal(new PixelSize(640, 360), result);
    }
}
