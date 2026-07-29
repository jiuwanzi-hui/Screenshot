using System.Diagnostics;

namespace Screenshot.App.Capture;

/// <summary>
/// Confirms a scroll boundary only when fresh wheel input keeps arriving while
/// the captured viewport remains unchanged for a sustained interval.
/// </summary>
internal sealed class StationaryScrollBoundaryDetector
{
    internal const int ConfirmationMilliseconds = 200;
    internal const int MinimumConfirmationDelta = 240;

    private ScrollCaptureDirection? _direction;
    private long _firstStationaryTimestamp;
    private int _deltaAfterStationary;
    private bool _markerQueued;

    public void ObserveWheel(
        ScrollCaptureDirection direction,
        long timestamp,
        int delta)
    {
        if (_markerQueued ||
            _firstStationaryTimestamp == 0L ||
            timestamp <= _firstStationaryTimestamp)
        {
            return;
        }

        if (_direction != direction)
        {
            Reset();
            return;
        }

        _deltaAfterStationary = (int)Math.Clamp(
            (long)_deltaAfterStationary + delta,
            int.MinValue,
            int.MaxValue);
    }

    public bool ShouldQueueMarker(
        ScrollCaptureDirection direction,
        long timestamp,
        int pendingDelta)
    {
        if (_markerQueued)
        {
            return false;
        }

        if (_direction != direction || _firstStationaryTimestamp == 0L)
        {
            _direction = direction;
            _firstStationaryTimestamp = timestamp;
            _deltaAfterStationary = 0;
            return false;
        }

        return Math.Abs(_deltaAfterStationary) >= MinimumConfirmationDelta &&
            Stopwatch.GetElapsedTime(
                _firstStationaryTimestamp,
                timestamp) >=
            TimeSpan.FromMilliseconds(ConfirmationMilliseconds);
    }

    public void MarkQueued()
    {
        _markerQueued = true;
    }

    public void Reset()
    {
        _direction = null;
        _firstStationaryTimestamp = 0L;
        _deltaAfterStationary = 0;
        _markerQueued = false;
    }
}
