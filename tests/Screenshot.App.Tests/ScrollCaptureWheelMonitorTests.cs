using System.Reflection;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureWheelMonitorTests
{
    [Theory]
    [InlineData(0x0201, false)]
    [InlineData(0x0202, false)]
    [InlineData(0x0204, true)]
    [InlineData(0x0205, true)]
    [InlineData(0x0207, false)]
    [InlineData(0x0208, false)]
    [InlineData(0x020B, false)]
    [InlineData(0x020C, false)]
    [InlineData(0x020E, false)]
    [InlineData(0x020A, false)]
    [InlineData(0x0200, false)]
    public void BlocksOnlyPointerActionsThatCouldModifyTheCaptureTarget(int message, bool expected)
    {
        var method = typeof(ScrollCaptureWheelMonitor).GetMethod(
            "IsBlockedPointerMessage", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<bool>(method.Invoke(null, [message])));
    }

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(299, 349, true)]
    [InlineData(99, 200, false)]
    [InlineData(300, 349, false)]
    [InlineData(299, 350, false)]
    public void ControlledClickCompletesOnlyInsideTheSelection(
        int releaseX,
        int releaseY,
        bool expected)
    {
        var captureRegion = new ScreenRegion(100, 200, 200, 150);

        Assert.Equal(
            expected,
            ScrollCaptureWheelMonitor.ShouldCompleteControlledClick(
                captureRegion,
                releaseX,
                releaseY));
    }

    [Theory]
    [InlineData(0x00000000, false)]
    [InlineData(0x00000001, false)]
    [InlineData(0xFF515740, false)]
    [InlineData(0xFF515780, true)]
    [InlineData(0xFF5157C0, true)]
    public void OnlyTouchPromotedMouseMessagesAreReservedForSmoothScrolling(
        uint extraInformation,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollCaptureWheelMonitor.IsTouchPromotedMouse(
                new IntPtr(unchecked((int)extraInformation))));
    }

}
