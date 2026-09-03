namespace Screenshot.App.Infrastructure;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly Task _activationListener;
    private readonly Action _activationRequested;
    private bool _disposed;

    private SingleInstanceCoordinator(
        EventWaitHandle activationEvent,
        Action activationRequested)
    {
        _activationEvent = activationEvent;
        _activationRequested = activationRequested;
        _activationListener = Task.Run(ListenForActivation);
    }

    public static SingleInstanceCoordinator? TryAcquire(
        string instanceName,
        Action activationRequested,
        bool signalExistingInstance = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(activationRequested);

        var eventName = $@"Local\{instanceName}.Activation";
        var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            eventName,
            out var createdNew);

        if (createdNew)
        {
            return new SingleInstanceCoordinator(
                activationEvent,
                activationRequested);
        }

        try
        {
            if (signalExistingInstance)
            {
                // Signal the already-running tray instance. The primary
                // process owns the UI dispatcher and will bring its window
                // forward without starting a second process.
                _ = activationEvent.Set();
            }
        }
        finally
        {
            activationEvent.Dispose();
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownSource.Cancel();

        try
        {
            _activationListener.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _shutdownSource.Dispose();
        _activationEvent.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ListenForActivation()
    {
        var waitHandles = new[]
        {
            _activationEvent,
            _shutdownSource.Token.WaitHandle,
        };

        while (!_shutdownSource.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(waitHandles) != 0)
            {
                return;
            }

            try
            {
                _activationRequested();
            }
            catch (Exception)
            {
                // A failed activation request must not terminate the primary instance.
            }
        }
    }
}
