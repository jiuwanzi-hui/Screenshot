namespace Screenshot.App.Capture;

// Manual wheel mode only. This driver consumes queued physical wheel intent;
// automatic click-scroll uses ControlledScrollDriver instead.
/// <summary>
/// Moves the selected viewport with fixed-cadence wheel input. Each input batch
/// restores the pointer immediately, so the driver never holds the user's mouse.
/// </summary>
internal sealed class ManualScrollDriver : IAsyncDisposable
{
    internal const int TickIntervalMilliseconds = 20;
    internal const int CapturePixelsPerTick = 5;
    internal const int CaptureWheelDeltaForCapture = 10;
    internal const int MaximumCaptureStepsPerFrame = 12;
    internal const int ReturnPixelsPerTick = 20;
    internal const int PresentationSettleMilliseconds = 0;
    private const int ReturnWheelDelta = 40;
    private const int MaximumQueuedCaptureSteps = 2400;

    private readonly ScrollCaptureTarget _target;
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly Task _runTask;
    private readonly object _inputGate = new();
    private int _direction;
    private int _fastReturn;
    private long _totalTravelPixels;
    private int _inputStepCount;
    private int _remainingCaptureStepBudget = MaximumCaptureStepsPerFrame;
    private int _captureStepsSinceFrame;
    private int _injectedWheelDeltaSinceSample;
    private int _pendingCaptureSteps;
    private int _backpressureWaitTicksSinceFrame;
    private int _lastCapturedInputStepCount;
    private int _lastCapturedBackpressureWaitTicks;
    private int _recoveryProbeRequested;
    private int _inputFailureCode;
    private string? _inputFailureStage;
    private bool _disposed;

    public ManualScrollDriver(ScrollCaptureTarget target)
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

    internal int LastCapturedInputStepCount => Volatile.Read(
        ref _lastCapturedInputStepCount);

    internal int LastCapturedBackpressureWaitTicks => Volatile.Read(
        ref _lastCapturedBackpressureWaitTicks);

    internal bool HasPendingCaptureInput =>
        Volatile.Read(ref _pendingCaptureSteps) != 0;

    internal int PendingCaptureStepCount =>
        Math.Abs(Volatile.Read(ref _pendingCaptureSteps));

    internal ScrollCaptureDirection? PendingCaptureDirection =>
        Volatile.Read(ref _pendingCaptureSteps) switch
        {
            > 0 => ScrollCaptureDirection.Up,
            < 0 => ScrollCaptureDirection.Down,
            _ => null,
        };

    internal void QueueCaptureInput(int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return;
        }

        var direction = Math.Sign(wheelDelta);
        var wheelNotches = Math.Max(1, Math.Abs(wheelDelta) / 120);
        var requestedSteps = Math.Min(
            MaximumQueuedCaptureSteps,
            wheelNotches * MaximumCaptureStepsPerFrame);
        lock (_inputGate)
        {
            if (_pendingCaptureSteps != 0 &&
                Math.Sign(_pendingCaptureSteps) != direction)
            {
                // A real reversal must react immediately; unfinished travel in
                // the old direction is stale user intent, not work to drain.
                _pendingCaptureSteps = 0;
            }

            _pendingCaptureSteps = Math.Clamp(
                _pendingCaptureSteps + (direction * requestedSteps),
                -MaximumQueuedCaptureSteps,
                MaximumQueuedCaptureSteps);
        }
    }

    internal int TakeInjectedWheelDelta()
    {
        lock (_inputGate)
        {
            var delta = _injectedWheelDeltaSinceSample;
            _injectedWheelDeltaSinceSample = 0;
            return delta;
        }
    }

    internal void RequestBoundaryProbe()
    {
        // Wheel input has no bounded gesture to restart. The next timer tick
        // naturally supplies the boundary-recovery probe.
        Interlocked.Exchange(ref _recoveryProbeRequested, 1);
    }

    public System.Drawing.Bitmap CaptureFrame()
    {
        // DwmFlush inside CaptureRegion provides a complete presented frame.
        // Hold the input gate so a wheel packet cannot change the viewport in
        // the middle of the screen copy. Replenishing one standard detent of
        // fine-grained input here couples maximum travel to the rate at which
        // frames are actually captured and processed.
        lock (_inputGate)
        {
            var frame = ForegroundWindowCaptureService.CaptureRegion(
                _target.CaptureRegion);
            AcknowledgeCapturedFrameCore();
            return frame;
        }
    }

    internal void AcknowledgeCapturedFrame()
    {
        lock (_inputGate)
        {
            AcknowledgeCapturedFrameCore();
        }
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
                : CaptureWheelDeltaForCapture;
            var wheelDelta = direction < 0
                ? -wheelMagnitude
                : wheelMagnitude;
            bool injected;
            lock (_inputGate)
            {
                if (Volatile.Read(ref _direction) != direction)
                {
                    continue;
                }

                if (!fastReturn && _remainingCaptureStepBudget <= 0)
                {
                    _backpressureWaitTicksSinceFrame++;
                    continue;
                }

                if (!fastReturn &&
                    (_pendingCaptureSteps == 0 ||
                     Math.Sign(_pendingCaptureSteps) != direction))
                {
                    continue;
                }

                if (!fastReturn)
                {
                    _remainingCaptureStepBudget--;
                }

                injected = ForegroundWindowCaptureService.ScrollWithWheelMessage(
                    _target,
                    wheelDelta);
                if (!injected && !fastReturn)
                {
                    _remainingCaptureStepBudget++;
                }
                else if (injected && !fastReturn)
                {
                    _captureStepsSinceFrame++;
                    _injectedWheelDeltaSinceSample += wheelDelta;
                    _pendingCaptureSteps -= direction;
                }
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

    private void AcknowledgeCapturedFrameCore()
    {
        Volatile.Write(
            ref _lastCapturedInputStepCount,
            _captureStepsSinceFrame);
        Volatile.Write(
            ref _lastCapturedBackpressureWaitTicks,
            _backpressureWaitTicksSinceFrame);
        _captureStepsSinceFrame = 0;
        _backpressureWaitTicksSinceFrame = 0;
        _remainingCaptureStepBudget = MaximumCaptureStepsPerFrame;
    }
}
