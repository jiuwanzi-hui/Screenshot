using System.Drawing;

namespace Screenshot.App.Capture;

internal sealed partial class AutomaticScrollCaptureComposerCore
{
    private ManualScrollFrame? _verifiedCurrent;
    private Bitmap? _verifiedCurrentSource;
    private ScrollCapturePosition? _contextOrigin;

    private bool TryAddContextFrame(Bitmap frame, ScrollCaptureDirection direction,
        ScrollCaptureOptions options, int? expectedRows, out ImageOverlapMatch? overlapMatch)
    {
        overlapMatch = null;
        var evidence = ScrollCapturePosition.Read(frame)!;
        if (evidence.Rows is not { } position || _contextOrigin!.Rows is not { } origin ||
            !ReferenceEquals(evidence.Session, _contextOrigin.Session))
        {
            LastRejectReason = "automatic-context-unverified";
            OnViewportLost(expectedRows, direction, frame.Height, options);
            return false;
        }
        var nextTop = position - origin;
        var shift = nextTop - _currentFrameTop;
        using var previous = new ManualScrollFrame(_currentFrame!);
        using var sample = new ManualScrollFrame(frame);
        if (!previous.IsConsistentAtShift(sample, shift) ||
            Math.Abs(shift) > frame.Height - options.MinimumOverlapRows)
        {
            LastRejectReason = "automatic-context-overlap-disagreement";
            OnViewportLost(expectedRows, direction, frame.Height, options);
            return false;
        }
        if (shift == 0)
        {
            LastFrameMovementRows = 0;
            OnViewportLocated();
            return false;
        }
        var candidate = new AlignmentCandidate(shift > 0 ? ScrollCaptureDirection.Down : ScrollCaptureDirection.Up,
            new ImageOverlapMatch(frame.Height - Math.Abs(shift), 1));
        return CommitAlignedFrame(frame, candidate, nextTop, nextTop + frame.Height,
            options, expectedRows, false, false, AutomaticViewportFingerprint.Create(frame),
            out overlapMatch, verified: true);
    }

    private bool? TryAddVerifiedContentFrame(Bitmap frame, ScrollCaptureDirection direction,
        ScrollCaptureOptions options, int? expectedRows, int? maximumRows,
        out ImageOverlapMatch? overlapMatch, bool exactOnly = false)
    {
        overlapMatch = null;
        if (!ReferenceEquals(_verifiedCurrentSource, _currentFrame))
        {
            _verifiedCurrent?.Dispose();
            _verifiedCurrent = new ManualScrollFrame(_currentFrame!);
            _verifiedCurrentSource = _currentFrame;
        }
        using var sample = new ManualScrollFrame(frame);
        var minimumOverlap = Math.Max(options.MinimumOverlapRows, Math.Min(48, frame.Height / 6));
        var match = _verifiedCurrent!.Locate(sample, minimumOverlap);
        // For normal automatic capture, exact distributed anchors provide a
        // fast path independent of blank margins and the wheel's pixel guess.
        // Animated/fixed/horizontally shifted pages retain the existing search.
        if (exactOnly && (match is null ||
            !_verifiedCurrent.IsExactOverlap(sample, match.Value.Shift, 0, frame.Height))) return null;
        if (match is null)
        {
            LastRejectReason = "automatic-unverified-content";
            OnViewportLost(expectedRows, direction, frame.Height, options);
            return false;
        }
        var shift = match.Value.Shift;
        var movement = Math.Abs(shift);
        if (exactOnly && movement > 0 && movement < options.MinimumNewRows) return null;
        if (movement == 0)
        {
            if (exactOnly)
                _stationaryAtCapturedBoundary =
                    (direction == ScrollCaptureDirection.Up && _currentFrameTop == _capturedContentTop) ||
                    (direction == ScrollCaptureDirection.Down && _currentFrameTop + frame.Height == _capturedContentBottom);
            LastFrameMovementRows = 0;
            OnViewportLocated();
            return false;
        }
        if (maximumRows is { } cap && movement > cap)
        {
            // Exact content already proves the displacement. Preserve this
            // reason so the controller can verify a quantized editor jump
            // immediately; a second approximate search can pick a repeated
            // code line and turn a simple cap retry into stop/backtracking.
            LastRejectReason = "movement-cap-veto";
            OnViewportLost(expectedRows, direction, frame.Height, options);
            return false;
        }
        var actualDirection = shift > 0 ? ScrollCaptureDirection.Down : ScrollCaptureDirection.Up;
        if (expectedRows is > 0 && direction != actualDirection)
        {
            if (exactOnly) return null;
            LastRejectReason = "fresh-wheel-opposite-veto";
            OnViewportLost(expectedRows, direction, frame.Height, options);
            return false;
        }
        var nextTop = checked(_currentFrameTop + shift);
        var nextBottom = checked(nextTop + frame.Height);
        var above = nextTop < _capturedContentTop;
        var below = nextBottom > _capturedContentBottom;
        var boundaryTop = above ? _topBoundaryFrameTop : _bottomBoundaryFrameTop;
        var boundaryFrame = above ? _topBoundaryFrame : _bottomBoundaryFrame;
        if ((above || below) && boundaryTop != _currentFrameTop)
        {
            // Crossing a previously saved edge must agree with its pixels;
            // repeated avatars cannot move the absolute boundary on return.
            using var boundary = new ManualScrollFrame(boundaryFrame!);
            var verified = boundary.Locate(sample, minimumOverlap);
            if (verified is null || boundaryTop + verified.Value.Shift != nextTop)
            {
                if (exactOnly) return null;
                LastRejectReason = "automatic-content-boundary-disagreement";
                OnViewportLost(expectedRows, direction, frame.Height, options);
                return false;
            }
        }
        var candidate = new AlignmentCandidate(actualDirection,
            new ImageOverlapMatch(frame.Height - movement, match.Value.Confidence));
        // The verified displacement and interval subtraction determine new
        // pixels, including blank padding or repeated text at new positions.
        return CommitAlignedFrame(frame, candidate, nextTop, nextBottom, options,
            expectedRows, false, false, AutomaticViewportFingerprint.Create(frame),
            out overlapMatch, verified: true);
    }
}
