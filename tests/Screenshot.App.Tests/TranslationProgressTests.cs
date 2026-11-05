using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationProgressTests
{
    [Fact]
    public async Task ConcurrentTranslationsOnlyReceiveTheirOwnWorkerProgress()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task<string[]> Run(string name)
        {
            var messages = new List<string>();
            using var progress = TranslationProgress.Begin(messages.Add);
            if (Interlocked.Increment(ref count) == 2) arrived.SetResult();
            await arrived.Task;
            await Task.Run(() => TranslationProgress.Report(name));
            return messages.ToArray();
        }
        var results = await Task.WhenAll(Task.Run(() => Run("first")), Task.Run(() => Run("second")));
        Assert.Equal(["first"], results[0]);
        Assert.Equal(["second"], results[1]);
    }

    [Fact]
    public async Task QueuedProgressCannotOverwriteTheResultAfterAnOperationFinishes()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedContext();
        var messages = new List<string>();
        TranslationProgress? progress = null;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            progress = TranslationProgress.Begin(messages.Add);
            pending = Task.Run(() => TranslationProgress.Report("loading"));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            await pending;
            progress.Dispose();
            Assert.Single(context.Pending);
            foreach (var callback in context.Pending) callback();
            Assert.Empty(messages);
        }
        finally { progress?.Dispose(); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        internal List<Action> Pending { get; } = [];
        public override void Post(SendOrPostCallback callback, object? state) => Pending.Add(() => callback(state));
    }
}
