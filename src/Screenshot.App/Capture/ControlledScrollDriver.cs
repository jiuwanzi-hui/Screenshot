using System.Diagnostics;

namespace Screenshot.App.Capture;

// Automatic click-scroll only. Do not add manual wheel queue behavior here.
/// <summary>
/// Moves the selected viewport with fixed-cadence wheel input. Each input batch
/// restores the pointer immediately, so the driver never holds the user's mouse.
/// </summary>
internal sealed class ControlledScrollDriver : IAsyncDisposable
{
    internal const int TickIntervalMilliseconds = 20;
    internal const int CapturePixelsPerTick = 5;
    internal const int CaptureWheelDeltaForCapture = 10;
    internal const int MaximumCaptureStepsPerFrame = 12;
    internal const int ReturnPixelsPerTick = 20;
    internal const int PresentationSettleMilliseconds = 0;
    private const int ReturnWheelDelta = 40;
    private readonly ScrollCaptureTarget _target;
    private readonly Func<ScrollCaptureTarget, int, bool> _sendInput;
    private readonly Func<ScrollCaptureTarget, System.Drawing.Bitmap> _captureFrame;
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly Task _runTask;
    private readonly object _inputGate = new();
    private readonly SemaphoreSlim _resumeSignal = new(0, 1);
    private int _direction;
    private int _fastReturn;
    private long _totalTravelPixels;
    private long _signedWheelDistance;
    private int _inputStepCount;
    private int _captureStepsSinceFrame;
    private int _backpressureWaitTicks;
    private int _lastCapturedInputStepCount;
    private int _lastCapturedBackpressureWaitTicks;
    private int _recoveryProbeRequested;
    private long _lastCapturedInputMagnitude;
    private int _inputFailureCode;
    private string? _inputFailureStage;
    private bool _disposed;

    public ControlledScrollDriver(ScrollCaptureTarget target)
        : this(target, ControlledScrollInput.Send, ForegroundWindowCaptureService.CaptureScrollTargetRegion)
    {
    }

    internal ControlledScrollDriver(ScrollCaptureTarget target,
        Func<ScrollCaptureTarget, int, bool> sendInput,
        Func<ScrollCaptureTarget, System.Drawing.Bitmap> captureFrame)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _sendInput = sendInput;
        _captureFrame = captureFrame;
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

    internal long LastCapturedInputMagnitude => Interlocked.Read(ref _lastCapturedInputMagnitude);

    internal void RetryInput()
    {
        Interlocked.Exchange(ref _inputFailureCode, 0);
        Volatile.Write(ref _inputFailureStage, null);
    }

    internal bool StepBackForRecovery(ScrollCaptureDirection captureDirection, int maximumWheelDelta = 120)
    {
        // The capture loop calls this only while stopped. A whole detent also
        // works in editors which buffer small deltas; it moves toward the last
        // verified viewport, never farther into unrecorded content.
        SetDirection(null);
        lock (_inputGate)
        {
            if (HasInputFailure) return false;
            // Bound reverse input by the wheel input this leg actually sent,
            // plus two detents for presentation inertia already in flight.
            // A failed matcher must not repeatedly replenish blind full-detent
            // retries at an unchanged viewport. This is an input bound, not a
            // claim that wheel units measure identical pixels in every app.
            var available = (captureDirection == ScrollCaptureDirection.Up ? _signedWheelDistance : -_signedWheelDistance) + 240;
            var magnitude = (int)Math.Clamp(available, 0, Math.Clamp(maximumWheelDelta, 1, 120));
            if (magnitude <= 0) return false;
            var delta = captureDirection == ScrollCaptureDirection.Up ? -magnitude : magnitude;
            var injected = _sendInput(_target, delta);
            if (injected) _signedWheelDistance += delta;
            if (!injected) RecordInputFailure("reconnect-wheel", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return injected;
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
        // Blocking wheel input cannot stop the target's existing animation.
        // Let DWM present independently; never hold the input gate during a
        // screen copy or preview patch. This prevents a pause on every frame.
        var frame = _captureFrame(_target);
        Interlocked.Exchange(ref _lastCapturedInputMagnitude, TotalInputMagnitude);
        AcknowledgeCapturedFrame();
        return frame;
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
        var previous = Interlocked.Exchange(
            ref _direction,
            HasInputFailure ? 0 : value);
        if (previous == 0 && value != 0 && !HasInputFailure && _resumeSignal.CurrentCount == 0)
            _resumeSignal.Release();
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
        lock (_inputGate)
        {
            Interlocked.Exchange(ref _totalTravelPixels, 0);
            _signedWheelDistance = 0;
        }
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
            _resumeSignal.Dispose();
        }
    }

    private async Task RunAsync()
    {
        // PeriodicTimer resumes immediately after a slow consumer.  That
        // behaviour is useful for polling, but it creates a burst of wheel
        // packets here whenever image matching takes longer than one tick.
        // The burst makes scrolling visibly change speed and can move the
        // viewport past the overlap before the next sample is available.
        // Pace against an absolute monotonic deadline and discard missed
        // ticks instead of catching up.  This keeps the input cadence stable
        // even while the capture thread is busy.
        var intervalTicks = Math.Max(
            1L,
            (long)Math.Round(
                Stopwatch.Frequency * TickIntervalMilliseconds / 1000d));
        var nextTick = Stopwatch.GetTimestamp();
        while (!_cancellationSource.IsCancellationRequested)
        {
            if (Volatile.Read(ref _direction) == 0)
            {
                await _resumeSignal.WaitAsync(_cancellationSource.Token);
                nextTick = Stopwatch.GetTimestamp();
                continue;
            }
            nextTick += intervalTicks;
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = nextTick - now;
            if (remainingTicks > 0)
            {
                var delayMilliseconds = (int)Math.Min(
                    TickIntervalMilliseconds,
                    Math.Max(
                        1,
                        Math.Ceiling(
                            remainingTicks * 1000d /
                            Stopwatch.Frequency)));
                await Task.Delay(
                    delayMilliseconds,
                    _cancellationSource.Token);
                // Task.Delay has millisecond granularity.  If it woke late,
                // skip directly to the next real-time slot rather than
                // emitting several packets back-to-back.
                now = Stopwatch.GetTimestamp();
                if (now > nextTick + intervalTicks)
                {
                    nextTick = now;
                }
            }

            var direction = Volatile.Read(ref _direction);
            var boundaryProbe = Interlocked.Exchange(ref _recoveryProbeRequested, 0) != 0;
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
            if (boundaryProbe)
            {
                wheelMagnitude = 120;
                pixels = 60;
            }
            var wheelDelta = direction < 0
                ? -wheelMagnitude
                : wheelMagnitude;
            bool injected;
            var inputRegion = _target.ScrollInputRegionProvider?.Invoke() ?? _target.CaptureRegion;
            var stepLimit = GetCaptureStepLimit(inputRegion.Height, fastReturn);
            lock (_inputGate)
            {
                if (Volatile.Read(ref _direction) != direction)
                {
                    continue;
                }

                // Keep unseen travel below a fraction of the actual scrollable
                // viewport. Fast machines replenish this budget every sample;
                // slow capture/matching/preview work cannot scroll past overlap.
                if (!boundaryProbe && _captureStepsSinceFrame >= stepLimit)
                {
                    _backpressureWaitTicks++;
                    continue;
                }

                injected = _sendInput(
                    _target,
                    wheelDelta);
                if (injected)
                {
                    _captureStepsSinceFrame++;
                    _signedWheelDistance += wheelDelta;
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
        Volatile.Write(ref _lastCapturedBackpressureWaitTicks, _backpressureWaitTicks);
        _backpressureWaitTicks = 0;
        _captureStepsSinceFrame = 0;
    }

    internal static int GetCaptureStepLimit(int viewportHeight, bool fastReturn) =>
        Math.Clamp(viewportHeight / (4 * (fastReturn ? ReturnPixelsPerTick : CapturePixelsPerTick)),
            1, MaximumCaptureStepsPerFrame);
}
