using System.Windows.Threading;

namespace Screenshot.App.Infrastructure;

/// <summary>A session-scoped message loop, isolated from rendering and image work.</summary>
internal sealed class SessionInputThread : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal SessionInputThread(Action initialize, Action release)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                initialize();
                _ready.TrySetResult(dispatcher);
                if (Volatile.Read(ref _disposed) == 0) System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception exception) { _ready.TrySetException(exception); }
            finally
            {
                try { release(); }
                finally { _stopped.TrySetResult(); }
            }
        }) { IsBackground = true, Name = "SnapCut scroll input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    internal Dispatcher Dispatcher => _ready.Task.GetAwaiter().GetResult();
    internal Task Stopped => _stopped.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ready.Task.IsCompletedSuccessfully && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        // No UI-thread join. A late startup observes _disposed before Run().
    }
}
