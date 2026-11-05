using System.Drawing;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class PendingCaptureFrameTests
{
    [Fact]
    public async Task OverlayTakesTheOriginalFrameAndOwnsItsLifetime()
    {
        using var image = new CapturedImage(new Bitmap(7, 5), new ScreenRegion(-7, 20, 7, 5));
        using var pending = new PendingCaptureFrame(_ => Task.FromResult(image));
        Assert.Same(image, await pending.TakeAsync(CancellationToken.None));
        pending.Dispose();
        Assert.Equal(7, image.Preview.PixelWidth);
        Assert.Equal(new ScreenRegion(-7, 20, 7, 5), image.SourceRegion);
    }

    [Fact]
    public void ConstructionFailureReleasesAnAlreadyCompletedFrame()
    {
        var image = new CapturedImage(new Bitmap(7, 5));
        var pending = new PendingCaptureFrame(_ => Task.FromResult(image));
        pending.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Preview);
    }

    [Fact]
    public async Task EscapeDoesNotWaitForDriverAndLateFrameIsReleased()
    {
        var capture = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captureToken = default;
        using var pending = new PendingCaptureFrame(token => { captureToken = token; return capture.Task; });
        using var escape = new CancellationTokenSource();
        var take = pending.TakeAsync(escape.Token);
        escape.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
        pending.Dispose();
        Assert.True(captureToken.IsCancellationRequested);
        var late = new CapturedImage(new Bitmap(7, 5));
        capture.SetResult(late);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { _ = late.Preview; return false; }
            catch (ObjectDisposedException) { return true; }
        }, TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.TakeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CaptureFailureRemainsObservableAndDoesNotBlockDisposal()
    {
        using var pending = new PendingCaptureFrame(_ => Task.FromException<CapturedImage>(new TimeoutException("driver")));
        await Assert.ThrowsAsync<TimeoutException>(() => pending.TakeAsync(CancellationToken.None));
    }
}
