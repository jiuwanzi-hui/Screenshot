namespace Screenshot.App.Capture;

public sealed class ScrollWheelMotionTracker
{
    private const int StandardWheelDelta = 120;
    private readonly object _syncRoot = new();
    private int _pendingDelta;
    private double? _pixelsPerWheelDelta;

    public bool HasPendingInput
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingDelta != 0;
            }
        }
    }

    public ScrollCaptureDirection Direction
    {
        get
        {
            lock (_syncRoot)
            {
                return GetDirection(_pendingDelta);
            }
        }
    }

    public int PendingDelta
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingDelta;
            }
        }
    }

    public void AddDelta(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            // A reversal starts a new motion run. Mixing the old and new signs into
            // a net delta hides the direction change and is what made returned
            // content append to the wrong end of the long image.
            if (_pendingDelta != 0 && Math.Sign(_pendingDelta) != Math.Sign(delta))
            {
                _pendingDelta = delta;
                return;
            }

            _pendingDelta = Math.Clamp(
                _pendingDelta + delta,
                -StandardWheelDelta * 40,
                StandardWheelDelta * 40);
        }
    }

    public int? GetExpectedRows(int frameHeight, ScrollCaptureOptions options)
    {
        lock (_syncRoot)
        {
            return GetExpectedRowsCore(frameHeight, options, _pendingDelta);
        }
    }

    public ScrollWheelMotionSample TakePendingMotion(
        int frameHeight,
        ScrollCaptureOptions options,
        ScrollCaptureDirection fallbackDirection)
    {
        lock (_syncRoot)
        {
            if (_pendingDelta == 0)
            {
                return new ScrollWheelMotionSample(
                    fallbackDirection,
                    ExpectedRows: null,
                    Delta: 0);
            }

            var delta = _pendingDelta;
            var sample = new ScrollWheelMotionSample(
                GetDirection(delta),
                GetExpectedRowsCore(frameHeight, options, delta),
                delta);
            _pendingDelta = 0;
            return sample;
        }
    }

    public void ObserveMovement(int rows)
    {
        lock (_syncRoot)
        {
            ObserveMovementCore(rows, _pendingDelta);
            if (rows > 0)
            {
                _pendingDelta = 0;
            }
        }
    }

    public void ObserveMovement(int rows, int sourceDelta)
    {
        lock (_syncRoot)
        {
            ObserveMovementCore(rows, sourceDelta);
        }
    }

    private int? GetExpectedRowsCore(
        int frameHeight,
        ScrollCaptureOptions options,
        int delta)
    {
        if (delta == 0)
        {
            return null;
        }

        var pixelsPerDelta = _pixelsPerWheelDelta ??
            (frameHeight / 6d / StandardWheelDelta);
        var expectedRows = (int)Math.Round(Math.Abs(delta) * pixelsPerDelta);
        return Math.Clamp(
            expectedRows,
            options.MinimumNewRows,
            Math.Max(options.MinimumNewRows, frameHeight - options.MinimumOverlapRows));
    }

    private void ObserveMovementCore(int rows, int sourceDelta)
    {
        if (sourceDelta == 0 || rows <= 0)
        {
            return;
        }

        var observedPixelsPerDelta = Math.Clamp(
            rows / (double)Math.Abs(sourceDelta),
            0.05,
            4.0);
        _pixelsPerWheelDelta = _pixelsPerWheelDelta is { } calibrated
            ? (calibrated * 0.7) + (observedPixelsPerDelta * 0.3)
            : observedPixelsPerDelta;
    }

    private static ScrollCaptureDirection GetDirection(int delta) => delta > 0
        ? ScrollCaptureDirection.Up
        : ScrollCaptureDirection.Down;
}

public readonly record struct ScrollWheelMotionSample(
    ScrollCaptureDirection Direction,
    int? ExpectedRows,
    int Delta)
{
    public bool HasFreshInput => Delta != 0;
}
