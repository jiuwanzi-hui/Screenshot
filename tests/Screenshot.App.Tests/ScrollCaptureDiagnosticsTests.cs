using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureDiagnosticsTests
{
    [Fact]
    public void DiagnosticsUseTheBuildAppropriateLoggingLevel()
    {
#if DEBUG
        Assert.True(ScrollCaptureDiagnostics.ShouldRecord("frame-captured"));
#else
        Assert.False(ScrollCaptureDiagnostics.ShouldRecord("frame-captured"));
        Assert.False(ScrollCaptureDiagnostics.ShouldRecord("controlled-state"));
#endif
        Assert.True(ScrollCaptureDiagnostics.ShouldRecord("capture-failed"));
        Assert.True(ScrollCaptureDiagnostics.ShouldRecord("capture-exception"));
        Assert.True(ScrollCaptureDiagnostics.ShouldRecord("native-error"));
    }
}
