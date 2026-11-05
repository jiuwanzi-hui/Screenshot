using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCapturePreviewPublisherTests
{
    [Fact]
    public async Task TransientRenderFailureRetriesWithoutAnotherCaptureRequest()
    {
        var attempts = 0;
        await using var publisher = new ScrollCapturePreviewPublisher(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("render resource busy");
            return Task.CompletedTask;
        }, 0, CancellationToken.None);
        await publisher.RefreshAsync();
        Assert.Equal(2, attempts);
        Assert.Null(publisher.LastError);
    }

    [Fact]
    public async Task PersistentPreviewFailureIsBoundedAndNextRequestCanRecover()
    {
        var fail = true;
        var attempts = 0;
        var status = new List<Exception?>();
        await using var publisher = new ScrollCapturePreviewPublisher(() =>
        {
            Interlocked.Increment(ref attempts);
            if (fail) throw new InvalidOperationException("preview unavailable");
            return Task.CompletedTask;
        }, 0, CancellationToken.None, status.Add);
        await publisher.RefreshAsync();
        Assert.Equal(3, attempts);
        Assert.NotNull(publisher.LastError);
        fail = false;
        await publisher.RefreshAsync();
        Assert.Equal(4, attempts);
        Assert.Null(publisher.LastError);
        Assert.Collection(status, error => Assert.NotNull(error), error => Assert.Null(error));
    }

    [Fact]
    public async Task SlowPreviewCoalescesFramesAndDrainsTheLatestBeforeDisposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<int>();
        var latest = 0;
        var publisher = new ScrollCapturePreviewPublisher(async () =>
        {
            published.Add(Volatile.Read(ref latest));
            if (published.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
        }, 0, CancellationToken.None);
        publisher.Request();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var frame = 1; frame <= 100; frame++)
        {
            Volatile.Write(ref latest, frame);
            publisher.Request();
        }
        var disposing = publisher.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        release.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(published, value => Assert.Equal(0, value), value => Assert.Equal(100, value));
        publisher.Request();
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public async Task LastExpansionInsideTheRefreshIntervalIsPublishedWithoutAnotherFrame()
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var publisher = new ScrollCapturePreviewPublisher(() =>
        {
            if (Interlocked.Increment(ref count) == 2) published.SetResult();
            return Task.CompletedTask;
        }, 100, CancellationToken.None);
        await publisher.RefreshAsync();
        publisher.Request();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CancelingADelayedPreviewStopsItsWorkerBeforeTheComposerIsDisposed()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var publisher = new ScrollCapturePreviewPublisher(() =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        }, 1000, cancellation.Token);
        await publisher.RefreshAsync();
        publisher.Request();
        cancellation.Cancel();
        await publisher.DisposeAsync();
        Assert.Equal(1, count);
    }
}
