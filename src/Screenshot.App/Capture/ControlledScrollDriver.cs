namespace Screenshot.App.Capture;

/// <summary>
/// Moves the selected viewport with fixed-cadence wheel input. Each input batch
/// restores the pointer immediately, so the driver never holds the user's mouse.
/// </summary>
internal sealed class ControlledScrollDriver : IAsyncDisposable
{
    internal const int TickIntervalMilliseconds = 20;
    internal const int CapturePixelsPerTick = 5;
    internal const int ReturnPixelsPerTick = 20;
    internal const int PresentationSettleMilliseconds = 0;
    private const int CaptureWheelDelta = 10;
    private const int ReturnWheelDelta = 40;

    private readonly ScrollCaptureTarget _target;
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly Task _runTask;
    private readonly object _inputGate = new();
    private int _direction;
    private int _fastReturn;
    private long _totalTravelPixels;
    private int _inputStepCount;
    private int _recoveryProbeRequested;
    private int _inputFailureCode;
    private string? _inputFailureStage;
    private bool _disposed;

    public ControlledScrollDriver(ScrollCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _runTask = Task.Run(RunAsync);
    }

    public long TotalInputMagnitude => Interlocked.Read(
        ref _totalTravelPixels);

    public bool HasInputFailure => Volatile.Read(ref _inputFailureCode) != 0;

    public int InputFailureCode => Volatile.Read(ref _inputFailureCode);

    public string? InputFailureStage => Volatile.Read(ref _inputFailureStage);

    internal int InputStepCount => Volatile.Read(ref _inputStepCount);

    internal void RequestBoundaryProbe()
    {
        // Wheel input has no bounded gesture to restart. The next timer tick
        // naturally supplies the boundary-recovery probe.
        Interlocked.Exchange(ref _recoveryProbeRequested, 1);
    }

    public System.Drawing.Bitmap CaptureFrame()
    {
        // DwmFlush inside CaptureRegion provides a complete presented frame.
        // Do not stop the input stream just to sample it; that created a visible
        // pause at every capture interval.
        return ForegroundWindowCaptureService.CaptureRegion(
            _target.CaptureRegion);
    }

    public void SetDirection(
        ScrollCaptureDirection? direction,
        bool fastReturn = false)
    {
        var value = direction switch
        {
            ScrollCaptureDirection.Down => -1,
            ScrollCaptureDirection.Up => 1,
            _ => 0,
        };
        Interlocked.Exchange(ref _fastReturn, fastReturn ? 1 : 0);
        Interlocked.Exchange(
            ref _direction,
            HasInputFailure ? 0 : value);
        if (value == 0)
        {
            // Do not report a paused state while a wheel packet is still using
            // its temporary routing point. The packet restores or preserves
            // the user's latest cursor position before this method returns.
            lock (_inputGate)
            {
            }
        }
    }

    public void ResetDistance()
    {
        Interlocked.Exchange(ref _totalTravelPixels, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _direction, 0);
        await _cancellationSource.CancelAsync();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationSource.Dispose();
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(TickIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(
                   _cancellationSource.Token))
        {
            var direction = Volatile.Read(ref _direction);
            _ = Interlocked.Exchange(ref _recoveryProbeRequested, 0);
            if (direction == 0)
            {
                continue;
            }

            var fastReturn = Volatile.Read(ref _fastReturn) != 0;
            var pixels = fastReturn
                ? ReturnPixelsPerTick
                : CapturePixelsPerTick;
            var wheelMagnitude = fastReturn
                ? ReturnWheelDelta
                : CaptureWheelDelta;
            var wheelDelta = direction < 0
                ? -wheelMagnitude
                : wheelMagnitude;
            bool injected;
            lock (_inputGate)
            {
                injected = Volatile.Read(ref _direction) == direction &&
                    ForegroundWindowCaptureService.ScrollWithWheelMessage(
                        _target,
                        wheelDelta);
            }

            if (!injected)
            {
                if (Volatile.Read(ref _direction) == direction)
                {
                    RecordInputFailure(
                        "window-wheel-message",
                        System.Runtime.InteropServices.Marshal
                            .GetLastWin32Error());
                }
                continue;
            }

            Interlocked.Add(ref _totalTravelPixels, pixels);
            Interlocked.Increment(ref _inputStepCount);
        }
    }

    private void RecordInputFailure(string stage, int errorCode)
    {
        Interlocked.Exchange(ref _direction, 0);
        if (Interlocked.CompareExchange(
            ref _inputFailureCode,
            errorCode == 0 ? -1 : errorCode,
            0) == 0)
        {
            Volatile.Write(ref _inputFailureStage, stage);
        }
    }
}
