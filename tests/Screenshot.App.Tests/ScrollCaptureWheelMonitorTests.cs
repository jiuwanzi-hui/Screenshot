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

}
