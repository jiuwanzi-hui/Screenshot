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

    /// <summary>
    /// Converts an explicit wheel delta into an expected row displacement using
    /// the calibration learned so far. The sampler needs this when several
    /// captured viewports have to be summarized as a single motion, which
    /// happens whenever fast scrolling forces intermediate samples to be
    /// skipped or merged.
    /// </summary>
    public int? GetExpectedRowsForDelta(
        int frameHeight,
        ScrollCaptureOptions options,
        int delta)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_syncRoot)
        {
            return GetExpectedRowsCore(frameHeight, options, delta);
        }
    }

    /// <summary>
    /// Combines two consecutive captured motions into the motion that spans
    /// both. Dropping or skipping a sample without merging its wheel delta made
    /// the next expected displacement far too small, which is what turned one
    /// unmatched fast-scroll frame into a run of unmatched frames.
    /// </summary>
    public ScrollWheelMotionSample MergeMotion(
        ScrollWheelMotionSample earlier,
        ScrollWheelMotionSample later,
        int frameHeight,
        ScrollCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (earlier.Delta == 0)
        {
            return later;
        }

        if (later.Delta == 0)
        {
            return new ScrollWheelMotionSample(
                earlier.Direction,
                earlier.ExpectedRows,
                earlier.Delta);
        }

        if (Math.Sign(earlier.Delta) != Math.Sign(later.Delta))
        {
            // A reversal is a new motion run. Keeping the older delta would
            // cancel it out and hide the direction change.
            return later;
        }

        var mergedDelta = earlier.Delta + later.Delta;
        return new ScrollWheelMotionSample(
            later.Direction,
            GetExpectedRowsForDelta(frameHeight, options, mergedDelta),
            mergedDelta);
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
