using System.Text.Json;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureTimingSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const string Root = @"D:\SnapCut\ScreenshotData\Diagnostics";

    [Fact]
    public void ASessionExpiresAndCannotRemainEnabledForDays()
    {
        var marker = JsonSerializer.Serialize(new { directory = Root + @"\ToolbarTrace-1", expiresUtc = Now.AddDays(1) });
        var session = Assert.IsType<CaptureTimingSession>(CaptureTimingSession.Parse(marker, Root, Now));
        Assert.Equal(Now.AddMinutes(15), session.ExpiresUtc);
        Assert.True(session.IsActive(Now.AddMinutes(14)));
        Assert.False(session.IsActive(Now.AddMinutes(15)));
    }

    [Theory]
    [InlineData(@"D:\Other\Logs")]
    [InlineData(@"D:\SnapCut\ScreenshotData\Diagnostics-other\Logs")]
    [InlineData(@"D:\SnapCut\ScreenshotData\Diagnostics\..\Logs")]
    public void ACollectorCannotRedirectLoggingOutsideTheDiagnosticsDirectory(string directory)
    {
        var marker = JsonSerializer.Serialize(new { directory, expiresUtc = Now.AddMinutes(10) });
        Assert.Null(CaptureTimingSession.Parse(marker, Root, Now));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("{\"expiresUtc\":0}")]
    public void ACorruptMarkerDoesNotEnableTracing(string marker) =>
        Assert.Null(CaptureTimingSession.Parse(marker, Root, Now));

    [Fact]
    public void AnExpiredMarkerDoesNotEnableANewWorker()
    {
        var marker = JsonSerializer.Serialize(new { directory = Root + @"\ToolbarTrace-1", expiresUtc = Now.AddSeconds(-1) });
        Assert.Null(CaptureTimingSession.Parse(marker, Root, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("enabled")]
    public void TheLegacyMarkerStillHasABoundedLifetime(string marker)
    {
        var session = Assert.IsType<CaptureTimingSession>(CaptureTimingSession.Parse(marker, Root, Now));
        Assert.Equal(Root, session.Directory);
        Assert.False(session.IsActive(Now.AddMinutes(15)));
    }
}
