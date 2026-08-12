using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Automatic click-scroll only. Keep this implementation isolated from the
// manual wheel pipeline so tuning manual stitching cannot change automation.
internal sealed class AutomaticScrollCaptureComposerCore : IDisposable
{
    private const int MaximumViewportHistory = 128;
    private const int MaximumRecentViewports = 4;
    // Wheel direction is the primary motion signal. Repetitive code/chat rows can
    // yield a slightly higher false score in the opposite direction; switching on
    // an 0.8% difference appended reverse-scroll content to the bottom. Keep the
    // opposite fallback for stale inertia events, but require decisive evidence.
    private const double OppositeDirectionConfidenceAdvantage = 0.03;
    // A fresh wheel displacement is independent evidence for a tightly bounded
    // local probe. Fixed sidebars measured on real browser pages can lower the
    // correct content match to about 0.919; boundary/no-input/global searches
    // keep the configured threshold and never use this floor.
    // A fresh wheel estimate is useful for ranking repeated rows, but it is not
    // pixel evidence. Accepting a weak local peak here produced duplicated code
    // lines at stitch seams (especially while the matcher was draining a smooth
    // scroll backlog). Keep the interactive neighborhood fast path, but require
    // the same confidence as an ordinary image match.
    private const double FreshWheelLocalConfidence = 0.94;
    private const int AlignmentToleranceRows = 3;
    // Largest positional drift a decisive boundary match may correct. Drift
    // accumulates in small mis-steps, while a periodic false peak sits at
    // least one content period away — far beyond this.
    private const int BoundaryReanchorMaximumRows = 64;
    // An image match this strong is direct evidence of where the viewport went.
    // The wheel estimate is only a prior, so it must not be allowed to veto it.
    private const double DecisiveMatchConfidence = 0.99;
    // A perfect-looking short-period peak can still be wrong on source code:
    // measured failures matched 15/25 rows while the recent automatic-scroll
    // cadence was 104-130 rows, dropping the intervening lines. Recheck only
    // this severe case and require the replacement to retain strong pixel
    // evidence; ordinary speed changes and final short boundary steps keep the
    // original image-led result.
    private const int SevereTemporalUndershootDivisor = 3;
    private const double TemporalUndershootReplacementConfidenceGap = 0.04;
    // Crossing an already captured edge is a higher-risk operation than an
    // ordinary adjacent stitch. Repetitive code/chat rows can produce a
    // 0.99-ish periodic peak while the compositor is still settling on the
    // captured side of the edge. Require near-pixel-perfect evidence before
    // committing such a crossing; this stricter gate is deliberately local to
    // boundary decisions so normal scrolling keeps its tolerant threshold.
    private const double BoundaryDecisiveMatchConfidence = 0.998;
    // Real browser/code pages can repaint a sticky header between the stored
    // edge frame and the first frame beyond it. In that case the pixel match
    // confidence can fall to about 0.969 even though the verified displacement
    // is exact (zero boundary drift). This lower floor is used only for that
    // exact, wheel-backed boundary case; ordinary/repetitive crossings keep the
    // stricter decisive threshold above.
    private const double ExactWheelBoundaryMinimumConfidence = 0.92;
    private const int MinimumDetectedFixedBottomRows = 8;
    private const int MaximumDetectedFixedBottomRows = 96;
    private readonly bool _detectStationaryLeadingRows;
    private readonly LinkedList<Bitmap> _segments = [];
    private readonly Dictionary<ulong, int> _segmentAnchorHashCounts = [];
    private readonly LinkedList<ViewportAnchor> _viewportHistory = [];
    private readonly LinkedList<RecentViewport> _recentViewports = [];
    private Bitmap? _currentFrame;
    private Bitmap? _topBoundaryFrame;
    private int _topBoundaryFrameTop;
    private Bitmap? _bottomBoundaryFrame;
    private int _bottomBoundaryFrameTop;
    // These logical coordinates let a reverse scroll move through content that is
    // already in _segments without adding it to the output a second time.
    private int _currentFrameTop;
    private int _capturedContentTop;
    private int _capturedContentBottom;
    private int _initialFrameHeight;
    private int? _lastSuccessfulNewRows;
    private ScrollCaptureDirection? _lastMatchedDirection;
    private int _stationaryLeadingRows;
    private int _fixedBottomRows;
    // Lost-anchor bookkeeping: how many consecutive frames could not be located
    // and roughly how far the viewport has travelled since the last located
    // one. Travel is wheel-estimated, so it is only used to decide how much
    // image evidence a resume must present — never to place pixels. The net
    // variant keeps direction (down positive) so a re-anchor proposal can be
    // checked against where the wheel actually sent the viewport — on periodic
    // content the wheel is the only signal a repetition cannot fake.
    private int _unlocatedRunLength;
    private int _unlocatedTravelRows;
    private int _unlocatedNetTravelRows;
    private int _lastLostExpectedRows;
    private ScrollCaptureDirection? _lastLostDirection;
    private ScrollCaptureDirection? _wheelReturnDirection;
    // When a reverse walk reaches a captured edge, a sticky header or a page
    // relayout can move the old viewport by a clean, pixel-perfect amount even
    // though the document itself is already at its physical boundary.  If that
    // first apparent crossing disagrees with the fresh wheel displacement, keep
    // the return leg quarantined until the user changes direction. Otherwise
    // the following inertia frames extend the same false coordinate and prepend
    // several copies of the page header.
    private ScrollCaptureDirection? _suspectedReturnBoundaryDirection;
    // Set only when a frame was observed stationary exactly at a captured
    // edge.  The next same-direction frame is then checked as a possible
    // compositor settling sample before it is allowed to extend the result.
    private bool _stationaryAtCapturedBoundary;
    // A strong pixel duplicate immediately after that stationary edge is
    // visual proof that we are at the physical boundary.  Keep rejecting
    // same-direction settling/inertia frames until the user reverses; a new
    // wheel tick must not turn the same stale header into another copy.
    private ScrollCaptureDirection? _confirmedVisualBoundaryDirection;
    private bool _topBoundaryReached;
    private bool _bottomBoundaryReached;
    private Bitmap? _compositeCache;
    private int _compositeContentTop;
    private int _compositeUsedHeight;
    private Bitmap? _previewStripCache;
    private int _previewStripContentTop;
    private int _previewStripUsedHeight;
    private int _previewStripWidth;
    private int _previewStripSourceHeight;
    private bool _disposed;

    public int FrameCount => _segments.Count;

    public int AddedAboveFrameCount { get; private set; }

    public int AddedBelowFrameCount { get; private set; }

    public int OutputWidth => _segments.First?.Value.Width ?? 0;

    public int OutputHeight => _segments.Sum(segment => segment.Height);

    public int? LastFrameMovementRows { get; private set; }

    /// <summary>
    /// Which safeguard rejected the most recent frame, for diagnostics only.
    /// Null when the frame was located or added.
    /// </summary>
    public string? LastRejectReason { get; private set; }

    /// <summary>
    /// Positional drift the most recent boundary verification measured, and
    /// the confidence of that verification. Diagnostics only.
    /// </summary>
    public int? LastBoundaryDriftRows { get; private set; }

    public double? LastBoundaryConfidence { get; private set; }

    /// <summary>
    /// True when the most recent frame was placed by the wheel estimate through
    /// content too flat to match on pixels (a "bridge"). Bridged displacements
    /// must not feed the wheel calibration — they are the estimate.
    /// </summary>
    public bool LastFrameWasBridged { get; private set; }

    public int? LastTemporalUndershootRows { get; private set; }

    public int? LastTemporalReplacementRows { get; private set; }

    public AutomaticScrollCaptureComposerCore()
        : this(detectStationaryLeadingRows: true)
    {
    }

    internal AutomaticScrollCaptureComposerCore(bool detectStationaryLeadingRows)
        : this(detectStationaryLeadingRows, fixedBottomRows: 0)
    {
    }

    internal AutomaticScrollCaptureComposerCore(
        bool detectStationaryLeadingRows,
        int fixedBottomRows)
    {
        _detectStationaryLeadingRows = detectStationaryLeadingRows;
        _fixedBottomRows = Math.Max(0, fixedBottomRows);
    }

    internal int CurrentFrameTop => _currentFrameTop;

    internal int CapturedContentTop => _capturedContentTop;

    internal int CapturedContentBottom => _capturedContentBottom;

    internal int InitialContentHeight => _initialFrameHeight;

    internal int FixedBottomRows => _fixedBottomRows;

    internal bool IsNearCapturedBoundary(
        ScrollCaptureDirection direction,
        int frameHeight)
    {
        // Collapse only the short final run that is already at the seam.  A
        // full-viewport tolerance discarded the very frames needed to carry
        // a reverse fling across the edge, leaving the logical anchor stuck
        // a hundred pixels inside the capture while the live viewport had
        // already moved well above it.
        var tolerance = Math.Max(
            96,
            Math.Max(AlignmentToleranceRows * 8, frameHeight / 3));
        return direction == ScrollCaptureDirection.Up
            ? _currentFrameTop - _capturedContentTop <= tolerance
            : _capturedContentBottom - (_currentFrameTop + frameHeight) <= tolerance;
    }

    internal void MarkBoundaryReached(ScrollCaptureDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_suspectedReturnBoundaryDirection == direction)
        {
            _suspectedReturnBoundaryDirection = null;
        }

        if (direction == ScrollCaptureDirection.Down)
        {
            _bottomBoundaryReached = true;
        }
        else
        {
            _topBoundaryReached = true;
        }
    }

    internal bool TryMarkBoundaryReached(
        Bitmap frame,
        ScrollCaptureDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        var boundaryFrame = direction == ScrollCaptureDirection.Down
            ? _bottomBoundaryFrame
            : _topBoundaryFrame;
        if (boundaryFrame is null ||
            boundaryFrame.Width != frame.Width ||
            boundaryFrame.Height != frame.Height)
        {
            return false;
        }

        var boundaryFingerprint = AutomaticViewportFingerprint.Create(boundaryFrame);
        var markerFingerprint = AutomaticViewportFingerprint.Create(frame);
        if (!boundaryFingerprint.IsStationaryComparedTo(markerFingerprint))
        {
            return false;
        }

        MarkBoundaryReached(direction);
        return true;
    }

    public bool TryAddFrame(
        Bitmap frame,
        ScrollCaptureOptions options,
        out ImageOverlapMatch? overlapMatch)
    {
        return TryAddFrame(
            frame,
            ScrollCaptureDirection.Down,
            options,
            out overlapMatch);
    }

    public bool TryAddFrame(
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        out ImageOverlapMatch? overlapMatch)
    {
        return TryAddFrame(
            frame,
            direction,
            options,
            expectedNewRows: null,
            lockDirection: false,
            out overlapMatch);
    }

    /// <param name="lockDirection">
    /// Historical name: fresh wheel input once locked the search to
    /// <paramref name="direction"/>. The wheel is only a preference now — the
    /// opposite direction is always probed, because during rapid reversals the
    /// wheel flips before smooth scrolling does — so this flag no longer
    /// changes behavior and is kept only for call-site compatibility.
    /// </param>
    public bool TryAddFrame(
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? expectedNewRows,
        bool lockDirection,
        out ImageOverlapMatch? overlapMatch)
    {
        return TryAddFrameCore(
            frame,
            direction,
            options,
            expectedNewRows,
            lockDirection,
            maximumAcceptedNewRows: null,
            out overlapMatch);
    }

    internal bool TryAddFrame(
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? expectedNewRows,
        bool lockDirection,
        int? maximumAcceptedNewRows,
        out ImageOverlapMatch? overlapMatch,
        int? programmaticExpectedRows = null,
        bool tolerateQuantizedExpectation = false)
    {
        return TryAddFrameCore(
            frame,
            direction,
            options,
            expectedNewRows,
            lockDirection,
            maximumAcceptedNewRows,
            out overlapMatch,
            programmaticExpectedRows,
            tolerateQuantizedExpectation);
    }

    private bool TryAddFrameCore(
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? expectedNewRows,
        bool lockDirection,
        int? maximumAcceptedNewRows,
        out ImageOverlapMatch? overlapMatch,
        int? programmaticExpectedRows = null,
        bool tolerateQuantizedExpectation = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options, frame.Height);
        _ = lockDirection;
        LastFrameMovementRows = null;
        LastRejectReason = null;
        LastBoundaryDriftRows = null;
        LastBoundaryConfidence = null;
        LastFrameWasBridged = false;
        LastTemporalUndershootRows = null;
        LastTemporalReplacementRows = null;

        var wasStationaryAtCapturedBoundary = _stationaryAtCapturedBoundary;
        _stationaryAtCapturedBoundary = false;

        if (_confirmedVisualBoundaryDirection is { } confirmedBoundaryDirection &&
            confirmedBoundaryDirection != direction)
        {
            _confirmedVisualBoundaryDirection = null;
        }

        if (_suspectedReturnBoundaryDirection is { } suspectedDirection &&
            suspectedDirection != direction)
        {
            // A real direction change gives a future approach to this edge a
            // clean chance to cross it. Carrying suspicion across reversals
            // would permanently disable legitimate bidirectional capture.
            _suspectedReturnBoundaryDirection = null;
        }

        if (_currentFrame is null)
        {
            Bitmap? initialSegment = null;
            Bitmap? initialCurrentFrame = null;
            Bitmap? initialTopBoundaryFrame = null;
            Bitmap? initialBottomBoundaryFrame = null;

            try
            {
                initialSegment = CloneScrollableViewport(frame);
                initialCurrentFrame = (Bitmap)frame.Clone();
                initialTopBoundaryFrame = (Bitmap)frame.Clone();
                initialBottomBoundaryFrame = (Bitmap)frame.Clone();
                _segments.AddLast(initialSegment);
                AddSegmentAnchorHashes(initialSegment);
                _currentFrame = initialCurrentFrame;
                _currentFrameTop = 0;
                _capturedContentTop = 0;
                _capturedContentBottom = frame.Height;
                _initialFrameHeight = initialSegment.Height;
                _topBoundaryFrame = initialTopBoundaryFrame;
                _topBoundaryFrameTop = 0;
                initialTopBoundaryFrame = null;
                _bottomBoundaryFrame = initialBottomBoundaryFrame;
                _bottomBoundaryFrameTop = 0;
                initialBottomBoundaryFrame = null;
                RememberViewport(
                    AutomaticViewportFingerprint.Create(frame),
                    _currentFrameTop);
                RememberRecentViewport(frame, _currentFrameTop);
                /* cache built lazily */
                initialSegment = null;
                initialCurrentFrame = null;
                overlapMatch = null;
                LastFrameMovementRows = 0;
                return true;
            }
            finally
            {
                initialSegment?.Dispose();
                initialCurrentFrame?.Dispose();
                initialTopBoundaryFrame?.Dispose();
                initialBottomBoundaryFrame?.Dispose();
            }
        }

        if (_currentFrame.Width != frame.Width || _currentFrame.Height != frame.Height)
        {
            LastRejectReason = "size-mismatch";
            overlapMatch = null;
            return false;
        }

        var fingerprint = AutomaticViewportFingerprint.Create(frame);

        // At a scroll boundary Windows still emits wheel input, while the
        // viewport stays at the same document position. Detect that exact
        // anchor before searching repetitive code rows for a non-zero shift.
        // This is deliberately stricter than matching any historical viewport:
        // only the current absolute anchor can classify the frame as stationary.
        if (_viewportHistory.Any(anchor =>
                anchor.FrameTop == _currentFrameTop &&
                anchor.Fingerprint.IsStationaryComparedTo(fingerprint)))
        {
            _stationaryAtCapturedBoundary =
                (direction == ScrollCaptureDirection.Up &&
                 _currentFrameTop == _capturedContentTop) ||
                (direction == ScrollCaptureDirection.Down &&
                 _currentFrameTop + frame.Height == _capturedContentBottom);
            OnViewportLocated();
            overlapMatch = null;
            LastFrameMovementRows = 0;
            return false;
        }

        // A returned viewport is stronger evidence than a wheel direction. It
        // lets the user reverse through captured content without repeatedly
        // adding the same strips at either boundary.
        var preferredNewRows = expectedNewRows ?? _lastSuccessfulNewRows;
        if (TryFindKnownViewport(
                frame,
                fingerprint,
                direction,
                preferredNewRows,
                options,
                out var knownTop))
        {
            // Keep the confirmed return leg active. The next sample may cross
            // the captured edge; clearing this here prevented the dedicated
            // boundary matcher from prepending/appending that first new strip.
            _wheelReturnDirection = direction;
            OnViewportLocated();
            LastFrameMovementRows = Math.Abs(knownTop - _currentFrameTop);
            ReplaceCurrentFrame(frame, knownTop);
            RememberRecentViewport(frame, knownTop);
            _lastMatchedDirection = direction;
            overlapMatch = null;
            return false;
        }

        // During a reverse walk the viewport is often still entirely inside
        // the stitched range. Do not spend a global image search on every
        // queued compositor sample in that interval: the wheel-integrated
        // displacement is sufficient to move the logical anchor, and the
        // boundary path below will switch back to pixel verification exactly
        // when the next step reaches/crosses the captured edge. Processing
        // this cheap path before candidate discovery keeps the live preview
        // from sitting several seconds behind a reverse fling and also avoids
        // periodic code-row peaks being mistaken for new content.
        if (TryWalkInsideCapturedRangeByWheel(
                frame,
                direction,
                expectedNewRows,
                options))
        {
            overlapMatch = null;
            return false;
        }

        // No runaway-miss throttle here, unlike the manual composer: the
        // controlled service paces its own samples and its re-anchor
        // attempts are bounded and deliberate. Skipping every other one of
        // those attempts left recoveries stuck in no-candidate-throttled and
        // auto-paused the capture.

        // A predicted return walk may cross a captured edge. The wheel only
        // tells us which stored boundary to compare; pixels must still verify
        // the displacement before any new rows are written.
        var localCandidate = expectedNewRows is { } freshExpectedRows
            ? FindAlignmentCandidate(
                  frame,
                  direction,
                  options,
                  freshExpectedRows,
                  preferredNeighborhoodOnly: true,
                  minimumConfidenceOverride: Math.Max(
                      options.MinimumOverlapConfidence,
                      FreshWheelLocalConfidence)) ??
              FindAlignmentCandidate(
                  frame,
                  direction,
                  options,
                  freshExpectedRows)
            : FindAlignmentCandidate(
                frame,
                direction,
                options,
                preferredNewRows);
        localCandidate = TryFindAdjacentBoundaryCandidate(
                frame,
                direction,
                expectedNewRows ?? preferredNewRows,
                options) ??
            localCandidate;
        var boundaryCrossingCandidate = TryCreateWheelBoundaryCrossingCandidate(
            frame,
            direction,
            expectedNewRows,
            options);
        // Prefer a decisive adjacent-frame match when it only crosses the
        // stored edge by a small strip. This preserves the real first rows
        // above/below the initial viewport even when the wheel estimate still
        // lags, while the dedicated boundary candidate remains the fallback
        // for a fast jump with no trustworthy adjacent overlap.
        var preferredCandidate =
            localCandidate is not null &&
            IsSmallVerifiedBoundaryCrossing(
                frame,
                localCandidate,
                options)
                ? localCandidate
                : boundaryCrossingCandidate ?? localCandidate;

        if (preferredCandidate is null &&
            TryWalkInsideCapturedRangeByWheel(
                frame,
                direction,
                expectedNewRows,
                options))
        {
            // Returning through already captured content only changes the
            // logical viewport coordinate. It never writes pixels. Crossing a
            // capture boundary still requires an image match below.
            overlapMatch = null;
            return false;
        }
        var alternateDirection = direction == ScrollCaptureDirection.Down
            ? ScrollCaptureDirection.Up
            : ScrollCaptureDirection.Down;
        // The wheel direction is only a preference, never a lock: during rapid
        // reversals the wheel flips before smooth scrolling does, so a frame
        // whose wheel says Down can still be moving Up on screen. The opposite
        // direction is therefore always probed — but only as a cheap
        // neighborhood scan, and skipped entirely when the preferred candidate
        // is confident enough that the opposite one could never outrank it.
        var skipAlternate =
            boundaryCrossingCandidate is not null ||
            preferredCandidate is not null &&
            preferredCandidate.Match.Confidence +
                OppositeDirectionConfidenceAdvantage > 1.0;
        // A reversal that arrives without wheel input accelerates from rest,
        // so its displacement lives below the recent step magnitude — and the
        // probe neighborhood around that magnitude always reaches down to the
        // minimum displacement.
        var alternateSeed = _lastMatchedDirection == alternateDirection
            ? _lastSuccessfulNewRows
            : (expectedNewRows ?? _lastSuccessfulNewRows);
        var alternateCandidate = skipAlternate
            ? null
            : FindAlignmentCandidate(
                frame,
                alternateDirection,
                options,
                alternateSeed,
                preferredNeighborhoodOnly: true);
        var estimatedTravelRows = Math.Max(
            _unlocatedTravelRows,
            expectedNewRows ?? 0);
        var candidate = OrderAlignmentCandidates(
                preferredCandidate,
                alternateCandidate)
            .FirstOrDefault(candidate =>
                IsAlignmentCandidateConsistent(frame, candidate, options));
        string? anchorVetoReason = null;
        var freshWheelOppositeRejected = false;
        if (candidate is not null &&
            expectedNewRows is not null &&
            candidate.Direction != direction)
        {
            // Repeated code/chat rows can produce a near-perfect peak in the
            // opposite direction. A fresh wheel sample is independent motion
            // evidence, so never let one image pair reverse the logical anchor.
            // Smooth-scroll inertia is still image-led on samples without fresh
            // wheel input; the next settled frame can therefore recover safely.
            freshWheelOppositeRejected = true;
            anchorVetoReason = "fresh-wheel-opposite-veto";
            candidate = null;
        }

        if (candidate is not null)
        {
            candidate = RecheckSevereTemporalUndershoot(
                frame,
                candidate,
                expectedNewRows,
                options);
        }

        if (candidate is not null &&
            !IsAnchorCandidateTrustworthy(
                frame,
                candidate,
                estimatedTravelRows,
                expectedNewRows,
                options))
        {
            // Do not stop at the veto: a vetoed anchor match is precisely the
            // situation the recovery ladder below exists for.
            anchorVetoReason = "stale-anchor-veto";
            candidate = null;
        }

        // Recovery ladder. A frame that matched nothing is not necessarily
        // garbage: the wheel may have flipped before the screen (probe the
        // opposite direction globally), the viewport may be reconnecting with
        // a capture boundary after an unmatchable gap (probe the boundary
        // frames), or the content may simply be too flat to match on pixels
        // (bridge on the wheel estimate). Try the cheap, evidence-bounded bridge
        // first. Full alternate/boundary searches are intentionally staggered:
        // running every global search for every queued miss made a single frame
        // take hundreds of milliseconds and caused the live preview to lag until
        // it looked frozen during repeated direction changes.
        if (candidate is null &&
            !freshWheelOppositeRejected &&
            ShouldProbeLostRecovery(phase: 1))
        {
            var globalAlternate = FindAlignmentCandidate(
                frame,
                alternateDirection,
                options,
                alternateSeed,
                preferredNeighborhoodOnly: false);

            if (globalAlternate is not null &&
                IsAlignmentCandidateConsistent(frame, globalAlternate, options) &&
                IsAnchorCandidateTrustworthy(
                    frame,
                    globalAlternate,
                    estimatedTravelRows,
                    expectedNewRows,
                    options))
            {
                candidate = globalAlternate;
            }
        }

        if (candidate is null && ShouldProbeLostRecovery(phase: 2))
        {
            candidate = TryReanchorAtBoundary(frame, options);
        }

        if (candidate is not null &&
            expectedNewRows is not null &&
            candidate.Direction != direction)
        {
            // Boundary re-anchoring runs after the first fresh-wheel direction
            // veto above. Apply the same invariant to its result; otherwise a
            // periodic document can return a near-perfect stored-boundary match
            // in the opposite direction and move the logical viewport backward
            // even though this sample contains newly injected wheel input.
            freshWheelOppositeRejected = true;
            anchorVetoReason = "fresh-wheel-opposite-veto";
            candidate = null;
        }

        if (candidate is null)
        {
            LastRejectReason = anchorVetoReason ?? "no-candidate";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        // A lost queue sample by itself is not proof that the physical page
        // stopped at the captured edge. Boundary evidence is evaluated below
        // against the stored edge frame; quarantining every post-gap frame here
        // left legitimate reverse scrolls stuck at currentTop=0 until a later
        // fling happened to reconnect. Static/duplicate edge frames are still
        // rejected by the stationary-boundary checks before this point and by
        // ShouldVetoSuspectedReturnBoundaryCrossing below.
        if (_unlocatedRunLength > 0 &&
            _wheelReturnDirection == candidate.Direction &&
            expectedNewRows is { } shortExpectedRows &&
            shortExpectedRows <= Math.Max(64, frame.Height / 5) &&
            ((candidate.Direction == ScrollCaptureDirection.Up &&
              _currentFrameTop <= _capturedContentTop) ||
             (candidate.Direction == ScrollCaptureDirection.Down &&
              _currentFrameTop + frame.Height >= _capturedContentBottom)))
        {
            LastRejectReason = "return-boundary-gap-veto";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        if ((_suspectedReturnBoundaryDirection == candidate.Direction ||
             _confirmedVisualBoundaryDirection == candidate.Direction) &&
            expectedNewRows is null &&
            ((candidate.Direction == ScrollCaptureDirection.Up &&
              _currentFrameTop <= _capturedContentTop) ||
             (candidate.Direction == ScrollCaptureDirection.Down &&
              _currentFrameTop + frame.Height >= _capturedContentBottom)))
        {
            LastRejectReason = "return-boundary-settle-veto";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        var candidateContinuesReturnWalk =
            _wheelReturnDirection == candidate.Direction;

        // Repeated structures separated by featureless padding admit several
        // pixel-consistent alignments, and the matcher can lock onto a closer
        // repetition with near-perfect confidence — silently dropping the
        // content in between. When the accepted displacement falls well short
        // of what the wheel measured, no independent reference vouches for it,
        // and the join at the wheel displacement runs through flat pixels
        // (the signature of that ambiguity), prefer the wheel's alignment: in
        // flat content a placement error is invisible, while a lost strip of
        // real content is not.
        overlapMatch = candidate.Match;
        var newRows = frame.Height - candidate.Match.OverlapRows;

        if (newRows < options.MinimumNewRows)
        {
            // The viewport matched essentially at the anchor — that is a
            // located viewport, not a lost one.
            LastRejectReason = "below-minimum";
            OnViewportLocated();
            return false;
        }

        if (maximumAcceptedNewRows is { } maximumRows &&
            newRows > maximumRows)
        {
            // Resume re-anchoring runs while the wheel driver is stopped. A
            // near-full-viewport match there is usually a dynamic chat/layout
            // reflow, not delayed scroll motion. Do not let even a nominally
            // perfect single-frame match write a foreign block into the result.
            LastRejectReason = "movement-cap-veto";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        if (_detectStationaryLeadingRows &&
            !candidate.IsBridged &&
            candidate.ReferenceTopOverride is null)
        {
            var detectedLeadingRows =
                AutomaticImageOverlapMatcher.FindStationaryLeadingRows(
                    _currentFrame,
                    frame,
                    candidate.Direction,
                    newRows);
            if (detectedLeadingRows > 0)
            {
                _stationaryLeadingRows = Math.Max(
                    _stationaryLeadingRows,
                    detectedLeadingRows);
            }
        }

        if (expectedNewRows is { } expected && !candidate.IsBridged)
        {
            // System wheel settings, smooth scrolling and capture timing make
            // this estimate approximate. During a fling it is not merely
            // imprecise but systematically low: the wheel calibration lags the
            // acceleration, and any sample the pipeline had to skip moved the
            // viewport without contributing to the estimate. A tight bound then
            // discarded perfectly matched large steps and stalled the stitch,
            // so keep a bound that still rejects a far-away periodic peak while
            // never overruling decisive image evidence.
            var tolerance = Math.Max(
                frame.Height / 2,
                (int)Math.Round(expected * 1.5));
            if (Math.Abs(newRows - expected) > tolerance &&
                candidate.Match.Confidence < DecisiveMatchConfidence &&
                candidate.ReferenceTopOverride is null)
            {
                LastRejectReason = "expected-veto";
                OnViewportLost(expectedNewRows, direction, frame.Height, options);
                overlapMatch = null;
                return false;
            }
        }

        // Unlike a physical wheel, the programmatic driver's travel count is
        // trustworthy: this composer never sees flings or user timing jitter.
        // A sub-decisive peak that disagrees with that count — or that needs a
        // horizontal shift the fixed capture region cannot produce — is a
        // repetition artifact. Field trace: approaching the page top through a
        // near-blank band, the temporal preference re-found the previous 93-row
        // displacement at confidence 0.947 with a 1px horizontal offset while
        // the driver had injected 60, and the prepend duplicated the header.
        if (programmaticExpectedRows is { } driverExpected &&
            driverExpected > 0 &&
            !candidate.IsBridged &&
            candidate.ReferenceTopOverride is null &&
            candidate.Match.Confidence < DecisiveMatchConfidence &&
            (candidate.Match.HorizontalOffset != 0 ||
             Math.Abs(newRows - driverExpected) >
                 // Notch-quantizing targets present the accumulated input in
                 // whole wheel notches, so the per-frame row count routinely
                 // disagrees with the driver's linear travel by a full notch
                 // in either direction. Keep the horizontal-offset arm strict
                 // but widen the row tolerance once quantization is known.
                 (tolerateQuantizedExpectation
                     ? Math.Max(192, driverExpected)
                     : Math.Max(24, driverExpected / 2))))
        {
            LastRejectReason = "automatic-expectation-veto";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        if (_fixedBottomRows == 0 &&
            _segments.Count == 1 &&
            !candidate.IsBridged &&
            candidate.ReferenceTopOverride is null)
        {
            var detectedFixedBottomRows = DetectFixedBottomRows(
                _currentFrame,
                frame,
                newRows);
            if (detectedFixedBottomRows > 0)
            {
                ApplyFixedBottomRows(detectedFixedBottomRows);
            }
        }

        var referenceTop = candidate.ReferenceTopOverride ?? _currentFrameTop;
        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(referenceTop + newRows)
            : checked(referenceTop - newRows);
        var nextFrameBottom = checked(nextFrameTop + frame.Height);
        if (candidate.ReferenceTopOverride is not null)
        {
            candidateContinuesReturnWalk |= IsLikelyReturnBoundaryApproach(
                candidate.Direction,
                expectedNewRows,
                nextFrameTop,
                nextFrameBottom,
                frame.Height,
                options);
        }

        if (ShouldVetoSuspectedReturnBoundaryCrossing(
                candidate.Direction,
                expectedNewRows,
                nextFrameTop,
                nextFrameBottom,
                frame.Height,
                candidateContinuesReturnWalk,
                wasStationaryAtCapturedBoundary,
                candidate.ReferenceTopOverride is not null,
                candidate.Match.Confidence,
                options))
        {
            LastRejectReason = _unlocatedRunLength > 0 &&
                candidate.ReferenceTopOverride is null
                    ? "return-boundary-gap-veto"
                    : "return-boundary-settle-veto";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        if (!TryResolveBoundaryExpansion(
                frame,
                candidate,
                direction,
                expectedNewRows,
                ref nextFrameTop,
                ref nextFrameBottom,
                options))
        {
            LastRejectReason ??= "boundary-inconsistent";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        Bitmap? nextCurrentFrame = null;
        Bitmap? newSegment = null;
        Bitmap? nextBoundaryFrame = null;

        try
        {
            nextCurrentFrame = (Bitmap)frame.Clone();
            var expandedCapturedRange = false;

            if (candidate.Direction == ScrollCaptureDirection.Down)
            {
                if (nextFrameBottom > _capturedContentBottom)
                {
                    nextBoundaryFrame = (Bitmap)frame.Clone();
                    var segmentSourceBottom = frame.Height - _fixedBottomRows;
                    var segmentSourceTop = Math.Max(
                        0,
                        segmentSourceBottom -
                            (nextFrameBottom - _capturedContentBottom));
                    newSegment = CreateSegment(
                        frame,
                        segmentSourceTop,
                        segmentSourceBottom - segmentSourceTop,
                        candidate.Match.HorizontalOffset);

                    if (IsAlreadyCapturedSegment(newSegment))
                    {
                        // The strip hashes as content that is already in the
                        // output, so it is not added again — but the anchor
                        // still follows the viewport. Rejecting the whole
                        // frame here left the anchor stuck at the boundary,
                        // where every following frame repeated the same
                        // rejection and expansion stalled permanently; when
                        // the hash was a false positive on repetitive
                        // content, the next frame now re-prepends what this
                        // one skipped.
                        _suspectedReturnBoundaryDirection =
                            ScrollCaptureDirection.Down;
                        LastRejectReason = "return-boundary-duplicate-veto";
                        newSegment.Dispose();
                        newSegment = null;
                        nextBoundaryFrame.Dispose();
                        nextBoundaryFrame = null;
                        OnViewportLost(
                            expectedNewRows,
                            direction,
                            frame.Height,
                            options);
                        overlapMatch = null;
                        return false;
                    }
                    else
                    {
                        _segments.AddLast(newSegment);
                        AddSegmentAnchorHashes(newSegment);
                        AppendSegmentToCompositeCache(newSegment);
                        AppendSegmentToPreviewStripCache(newSegment);
                        newSegment = null;
                        _capturedContentBottom = nextFrameBottom;
                        _bottomBoundaryFrame?.Dispose();
                        _bottomBoundaryFrame = nextBoundaryFrame;
                        _bottomBoundaryFrameTop = nextFrameTop;
                        nextBoundaryFrame = null;
                        AddedBelowFrameCount++;
                        expandedCapturedRange = true;
                    }
                }
            }
            else
            {
                if (nextFrameTop < _capturedContentTop)
                {
                    nextBoundaryFrame = (Bitmap)frame.Clone();
                    var segmentHeight = Math.Clamp(
                        _capturedContentTop - nextFrameTop,
                        0,
                        frame.Height);
                    var segmentSourceTop = Math.Min(
                        _stationaryLeadingRows,
                        Math.Max(0, frame.Height - segmentHeight));
                    newSegment = CreateSegment(
                        frame,
                        segmentSourceTop,
                        segmentHeight,
                        -candidate.Match.HorizontalOffset);

                    // The overlap match already defines the exact cut between
                    // this strip and the trusted initial viewport. Never trim
                    // the existing leading segment using approximate row
                    // similarity: adjacent source-code lines can be 96-98%
                    // alike, and deleting those "duplicates" clips one line at
                    // the initial-position seam. Absolute duplicate protection
                    // below still rejects a genuinely repeated boundary strip.

                    if (candidateContinuesReturnWalk &&
                        IsBoundarySegmentAlreadyPresent(
                            newSegment,
                            ScrollCaptureDirection.Up))
                    {
                        // The apparent new strip is already the leading edge
                        // of the result. This is a stronger duplicate signal
                        // than a single overlap peak (which can be produced by
                        // repeated chat/search rows), so quarantine the rest
                        // of this return leg instead of moving the anchor past
                        // the boundary and repeating it again.
                        _suspectedReturnBoundaryDirection =
                            ScrollCaptureDirection.Up;
                        LastRejectReason = "return-boundary-duplicate-veto";
                        OnViewportLost(
                            expectedNewRows,
                            direction,
                            frame.Height,
                            options);
                        overlapMatch = null;
                        return false;
                    }

                    if (wasStationaryAtCapturedBoundary &&
                        candidate.Direction == ScrollCaptureDirection.Up &&
                        _currentFrameTop == _capturedContentTop &&
                        IsBoundarySegmentAlreadyPresent(
                            newSegment,
                            ScrollCaptureDirection.Up))
                    {
                        // This is the common physical-top case where the
                        // compositor paints a small upward transition after
                        // the viewport has already stopped.  It has no new
                        // document rows, even if a fresh wheel event arrived.
                        _suspectedReturnBoundaryDirection =
                            ScrollCaptureDirection.Up;
                        _confirmedVisualBoundaryDirection =
                            ScrollCaptureDirection.Up;
                        LastRejectReason =
                            "return-boundary-stationary-duplicate-veto";
                        OnViewportLost(
                            expectedNewRows,
                            direction,
                            frame.Height,
                            options);
                        overlapMatch = null;
                        return false;
                    }

                    if (IsAlreadyCapturedSegment(newSegment))
                    {
                        // Same as the downward branch: skip the duplicate
                        // strip but keep the anchor tracking the viewport so
                        // boundary expansion can never stall on a hash
                        // false positive.
                        _suspectedReturnBoundaryDirection =
                            ScrollCaptureDirection.Up;
                        LastRejectReason = "return-boundary-duplicate-veto";
                        newSegment.Dispose();
                        newSegment = null;
                        nextBoundaryFrame.Dispose();
                        nextBoundaryFrame = null;
                        OnViewportLost(
                            expectedNewRows,
                            direction,
                            frame.Height,
                            options);
                        overlapMatch = null;
                        return false;
                    }
                    else
                    {
                        _segments.AddFirst(newSegment);
                        AddSegmentAnchorHashes(newSegment);
                        newSegment = null;
                        _capturedContentTop = nextFrameTop;
                        _topBoundaryFrame?.Dispose();
                        _topBoundaryFrame = nextBoundaryFrame;
                        _topBoundaryFrameTop = nextFrameTop;
                        nextBoundaryFrame = null;
                        AddedAboveFrameCount++;
                        expandedCapturedRange = true;
                        PrependSegmentToCompositeCache(_segments.First!.Value);
                        PrependSegmentToPreviewStripCache(_segments.First!.Value);
                    }
                }
            }

            _currentFrame.Dispose();
            _currentFrame = nextCurrentFrame;
            nextCurrentFrame = null;
            _currentFrameTop = nextFrameTop;
            RememberViewport(fingerprint, nextFrameTop);
            RememberRecentViewport(frame, nextFrameTop);

            // Track displacement even when the viewport only walks through already
            // captured content so the next boundary expansion keeps temporal glue.
            _lastSuccessfulNewRows = newRows;
            _lastMatchedDirection = candidate.Direction;
            LastFrameMovementRows = newRows;
            LastFrameWasBridged = candidate.IsBridged;
            if (expandedCapturedRange &&
                _wheelReturnDirection == candidate.Direction)
            {
                _wheelReturnDirection = null;
            }
            OnViewportLocated();

            return expandedCapturedRange;
        }
        finally
        {
            nextCurrentFrame?.Dispose();
            newSegment?.Dispose();
            nextBoundaryFrame?.Dispose();
        }
    }

    private bool ShouldVetoSuspectedReturnBoundaryCrossing(
        ScrollCaptureDirection candidateDirection,
        int? wheelExpectedRows,
        int nextFrameTop,
        int nextFrameBottom,
        int frameHeight,
        bool candidateContinuesReturnWalk,
        bool wasStationaryAtCapturedBoundary,
        bool hasBoundaryReference,
        double candidateConfidence,
        ScrollCaptureOptions options)
    {
        var crossesCapturedBoundary = candidateDirection == ScrollCaptureDirection.Up
            ? nextFrameTop < _capturedContentTop
            : nextFrameBottom > _capturedContentBottom;
        if (!crossesCapturedBoundary)
        {
            return false;
        }

        if (_suspectedReturnBoundaryDirection == candidateDirection)
        {
            if (_confirmedVisualBoundaryDirection == candidateDirection)
            {
                // Once pixels confirmed the physical edge, wheel ticks in the
                // same direction are still boundary input, not permission to
                // prepend the already captured header again.
                return true;
            }

            if (wheelExpectedRows is null)
            {
                // Inertia/paint samples carry no new input evidence. Keep them
                // out of the result; this is the exact tail that repeatedly
                // prepended the top layout in the field trace.
                return true;
            }

            // A fresh wheel event is not enough by itself: when the compositor
            // is still painting the old side of the seam, its expected travel
            // can remain entirely inside the captured range while a periodic
            // image peak proposes a large crossing. Keep that frame quarantined
            // until the wheel prediction itself reaches the edge.
            var recoveryDistanceToBoundary = candidateDirection == ScrollCaptureDirection.Up
                ? Math.Max(0, _currentFrameTop - _capturedContentTop)
                : Math.Max(
                    0,
                    _capturedContentBottom -
                    (_currentFrameTop + frameHeight));
            var recoveryPredictedBeyondBoundary = wheelExpectedRows.Value -
                recoveryDistanceToBoundary;
            if (recoveryPredictedBeyondBoundary < options.MinimumNewRows)
            {
                // Let the boundary verifier inspect this frame. The wheel has
                // not independently predicted a crossing yet, but a genuine
                // compositor frame may already have reached the edge.
                return false;
            }

            // Once independent wheel travel reaches the edge, re-evaluate the
            // candidate. The boundary matcher below still has to verify pixels;
            // this only prevents a stale suspicion from blocking a real crossing.
            _suspectedReturnBoundaryDirection = null;
            return false;
        }

        if (!candidateContinuesReturnWalk ||
            wheelExpectedRows is not { } expectedRows)
        {
            return false;
        }

        var distanceToBoundary = candidateDirection == ScrollCaptureDirection.Up
            ? Math.Max(0, _currentFrameTop - _capturedContentTop)
            : Math.Max(
                0,
                _capturedContentBottom - (_currentFrameTop + frameHeight));
        var predictedBeyondBoundary = expectedRows - distanceToBoundary;
        if (predictedBeyondBoundary < options.MinimumNewRows)
        {
            return false;
        }

        var matchedBeyondBoundary = candidateDirection == ScrollCaptureDirection.Up
            ? _capturedContentTop - nextFrameTop
            : nextFrameBottom - _capturedContentBottom;
        // Near an already captured edge the wheel prediction is only used as
        // independent motion evidence, not as pixel placement. A discrepancy
        // larger than ordinary frame-timing jitter is the signature seen when
        // a top header settles while the page is physically stationary.
        var allowedDifference = Math.Max(8, predictedBeyondBoundary / 6);
        if (Math.Abs(matchedBeyondBoundary - predictedBeyondBoundary) <=
            allowedDifference)
        {
            return false;
        }

        // A boundary-reanchored candidate has an independent absolute frame
        // to verify the seam.  Do not let a prior stationary sample quarantine
        // that candidate solely because the wheel estimate and live-frame
        // displacement differ during a fling; TryResolveBoundaryExpansion will
        // apply the strict confidence/drift gate next.
        if (_wheelReturnDirection == candidateDirection &&
            wheelExpectedRows is { } shortExpectedRows &&
            shortExpectedRows <= Math.Max(64, frameHeight / 5))
        {
            _suspectedReturnBoundaryDirection = candidateDirection;
            if (wasStationaryAtCapturedBoundary &&
                candidateDirection == ScrollCaptureDirection.Up)
            {
                _confirmedVisualBoundaryDirection = candidateDirection;
            }

            return true;
        }

        if (hasBoundaryReference &&
            candidateConfidence >= ExactWheelBoundaryMinimumConfidence &&
            Math.Abs(matchedBeyondBoundary - predictedBeyondBoundary) <=
                Math.Max(24, predictedBeyondBoundary / 4))
        {
            return false;
        }

        _suspectedReturnBoundaryDirection = candidateDirection;
        if (wasStationaryAtCapturedBoundary &&
            candidateDirection == ScrollCaptureDirection.Up)
        {
            _confirmedVisualBoundaryDirection = candidateDirection;
        }
        return true;
    }

    private bool IsLikelyReturnBoundaryApproach(
        ScrollCaptureDirection candidateDirection,
        int? wheelExpectedRows,
        int nextFrameTop,
        int nextFrameBottom,
        int frameHeight,
        ScrollCaptureOptions options)
    {
        if (wheelExpectedRows is null)
        {
            return false;
        }

        var crossesBoundary = candidateDirection == ScrollCaptureDirection.Up
            ? nextFrameTop < _capturedContentTop
            : nextFrameBottom > _capturedContentBottom;
        if (!crossesBoundary)
        {
            return false;
        }

        var edgeTolerance = Math.Max(
            24,
            Math.Min(frameHeight / 8, options.MinimumNewRows * 8));
        var distanceToBoundary = candidateDirection == ScrollCaptureDirection.Up
            ? _currentFrameTop - _capturedContentTop
            : _capturedContentBottom - (_currentFrameTop + frameHeight);
        return distanceToBoundary >= 0 && distanceToBoundary <= edgeTolerance;
    }

    private bool IsBoundarySegmentAlreadyPresent(
        Bitmap segment,
        ScrollCaptureDirection direction)
    {
        if (segment.Height < 8 || _segments.Count == 0)
        {
            return false;
        }

        var boundarySegment = direction == ScrollCaptureDirection.Up
            ? _segments.First!.Value
            : _segments.Last!.Value;
        if (segment.Width != boundarySegment.Width ||
            segment.Height > boundarySegment.Height)
        {
            return false;
        }

        // Boundary checks are rare (only when a return leg crosses an edge),
        // so a small deterministic pixel sample is preferable to another
        // global overlap search. Require a very high agreement ratio to avoid
        // mistaking legitimately repeated rows in the document for the edge.
        var sampleRows = Math.Min(segment.Height, 96);
        var sampleColumns = Math.Min(segment.Width, 96);
        var rowStep = Math.Max(1, sampleRows / 12);
        var columnStep = Math.Max(1, sampleColumns / 48);
        var maximumOffset = boundarySegment.Height - sampleRows;
        var offsetStep = Math.Max(1, sampleRows / 8);
        for (var offset = 0;
             offset <= maximumOffset;
             offset += offsetStep)
        {
            var matchingSamples = 0;
            var totalSamples = 0;
            for (var row = 0; row < sampleRows; row += rowStep)
            {
                var boundaryRow = direction == ScrollCaptureDirection.Up
                    ? offset + row
                    : boundarySegment.Height - sampleRows - offset + row;
                for (var column = 0;
                     column < sampleColumns;
                     column += columnStep)
                {
                    var boundaryColor = boundarySegment.GetPixel(column, boundaryRow);
                    var segmentColor = segment.GetPixel(column, row);
                    var colorDistance =
                        Math.Abs(boundaryColor.R - segmentColor.R) +
                        Math.Abs(boundaryColor.G - segmentColor.G) +
                        Math.Abs(boundaryColor.B - segmentColor.B) +
                        Math.Abs(boundaryColor.A - segmentColor.A);
                    if (colorDistance == 0)
                    {
                        matchingSamples++;
                    }

                    totalSamples++;
                }
            }

            if (totalSamples > 0 && matchingSamples == totalSamples)
            {
                return true;
            }
        }

        return false;
    }

    private Bitmap CloneScrollableViewport(Bitmap frame)
    {
        _fixedBottomRows = Math.Clamp(
            _fixedBottomRows,
            0,
            Math.Max(0, frame.Height - 1));
        return frame.Clone(
            new Rectangle(
                0,
                0,
                frame.Width,
                frame.Height - _fixedBottomRows),
            PixelFormat.Format32bppPArgb);
    }

    private static int DetectFixedBottomRows(
        Bitmap previousFrame,
        Bitmap currentFrame,
        int movementRows)
    {
        if (movementRows < 4 ||
            previousFrame.Width != currentFrame.Width ||
            previousFrame.Height != currentFrame.Height ||
            previousFrame.Height < 120)
        {
            return 0;
        }

        var maximumRows = Math.Min(
            MaximumDetectedFixedBottomRows,
            previousFrame.Height / 3);
        if (maximumRows < MinimumDetectedFixedBottomRows)
        {
            return 0;
        }

        var stripTop = previousFrame.Height - maximumRows;
        using var previousPixels = StripPixels.Copy(
            previousFrame,
            stripTop,
            maximumRows);
        using var currentPixels = StripPixels.Copy(
            currentFrame,
            stripTop,
            maximumRows);
        var sampleStep = Math.Max(1, previousFrame.Width / 240);
        var comparisonRight = previousFrame.Width >= 80
            ? previousFrame.Width - Math.Clamp(
                previousFrame.Width / 80,
                10,
                24)
            : previousFrame.Width;
        var fixedRows = 0;

        for (var y = maximumRows - 1; y >= 0; y--)
        {
            long totalDifference = 0;
            var sampledPixels = 0;
            var nearEqualPixels = 0;
            for (var x = 0; x < comparisonRight; x += sampleStep)
            {
                previousPixels.GetRgb(x, y, out var previousR, out var previousG, out var previousB);
                currentPixels.GetRgb(x, y, out var currentR, out var currentG, out var currentB);
                var difference = Math.Abs(previousR - currentR) +
                    Math.Abs(previousG - currentG) +
                    Math.Abs(previousB - currentB);
                totalDifference += difference;
                sampledPixels++;
                if (difference <= 12)
                {
                    nearEqualPixels++;
                }
            }

            var rowIsStationary = sampledPixels > 0 &&
                nearEqualPixels * 200 >= sampledPixels * 199 &&
                totalDifference <= sampledPixels * 2L;
            if (!rowIsStationary)
            {
                break;
            }

            fixedRows++;
        }

        if (fixedRows < MinimumDetectedFixedBottomRows)
        {
            return 0;
        }

        var bandTop = maximumRows - fixedRows;
        var minimumLuma = 255;
        var maximumLuma = 0;
        var edgeSamples = 0;
        var bandSamples = 0;
        for (var y = bandTop; y < maximumRows; y += 2)
        {
            var previousLuma = -1;
            for (var x = 0; x < comparisonRight; x += sampleStep)
            {
                previousPixels.GetRgb(x, y, out var r, out var g, out var b);
                var luma = (r * 54 + g * 183 + b * 19) >> 8;
                minimumLuma = Math.Min(minimumLuma, luma);
                maximumLuma = Math.Max(maximumLuma, luma);
                if (previousLuma >= 0 && Math.Abs(luma - previousLuma) >= 10)
                {
                    edgeSamples++;
                }

                previousLuma = luma;
                bandSamples++;
            }
        }

        // A uniform empty page bottom can remain unchanged after a small scroll.
        // Require visible fixed chrome (track, thumb, border or controls) before
        // removing it from the document output.
        if (maximumLuma - minimumLuma < 10 ||
            edgeSamples < Math.Max(2, bandSamples / 500))
        {
            return 0;
        }

        var movingProbeRows = Math.Min(12, maximumRows - fixedRows);
        var changedSamples = 0;
        var probeSamples = 0;
        for (var y = Math.Max(0, bandTop - movingProbeRows); y < bandTop; y++)
        {
            for (var x = 0; x < comparisonRight; x += sampleStep)
            {
                previousPixels.GetRgb(x, y, out var previousR, out var previousG, out var previousB);
                currentPixels.GetRgb(x, y, out var currentR, out var currentG, out var currentB);
                if (Math.Abs(previousR - currentR) +
                    Math.Abs(previousG - currentG) +
                    Math.Abs(previousB - currentB) >= 24)
                {
                    changedSamples++;
                }

                probeSamples++;
            }
        }

        return probeSamples > 0 &&
            changedSamples >= Math.Max(4, probeSamples / 100)
                ? fixedRows
                : 0;
    }

    private void ApplyFixedBottomRows(int fixedBottomRows)
    {
        if (_segments.Count != 1 || _fixedBottomRows > 0)
        {
            return;
        }

        var initialNode = _segments.First!;
        var initialFrame = initialNode.Value;
        fixedBottomRows = Math.Clamp(
            fixedBottomRows,
            MinimumDetectedFixedBottomRows,
            initialFrame.Height - 1);
        var scrollableInitialFrame = initialFrame.Clone(
            new Rectangle(
                0,
                0,
                initialFrame.Width,
                initialFrame.Height - fixedBottomRows),
            PixelFormat.Format32bppPArgb);
        initialNode.Value = scrollableInitialFrame;
        initialFrame.Dispose();
        _fixedBottomRows = fixedBottomRows;
        _initialFrameHeight = scrollableInitialFrame.Height;
        _segmentAnchorHashCounts.Clear();
        AddSegmentAnchorHashes(scrollableInitialFrame);
        InvalidateCompositeCache();
        InvalidatePreviewStripCache();
    }

    public Bitmap Compose()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("没有可拼接的长截图帧。");
        }

        EnsureCompositeCache();
        return _compositeCache!.Clone(
            new Rectangle(
                0,
                _compositeContentTop,
                OutputWidth,
                _compositeUsedHeight),
            PixelFormat.Format32bppPArgb);
    }

    internal void RefreshBoundaryViewport(
        Bitmap frame,
        ScrollCaptureDirection direction,
        int excludedBottomRows = 0,
        int? maximumRefreshRows = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_segments.Count == 0 ||
            frame.Width != OutputWidth ||
            frame.Height > OutputHeight)
        {
            return;
        }

        // Thin incremental strips keep matching cheap, but a compositor can
        // present one strip during a fractional smooth-scroll offset. Replace
        // the complete visible boundary with the latest clean viewport so a
        // small strip error cannot survive into the final image.
        EnsureCompositeCache();
        var sourceTop = 0;
        var copyHeight = frame.Height - Math.Clamp(
            excludedBottomRows,
            0,
            frame.Height - 1);
        var logicalDestinationTop = direction == ScrollCaptureDirection.Down
            ? _capturedContentBottom - frame.Height
            : _capturedContentTop;

        if (maximumRefreshRows is > 0)
        {
            var refreshRows = Math.Min(maximumRefreshRows.Value, frame.Height);
            if (direction == ScrollCaptureDirection.Down)
            {
                var sourceBottom = sourceTop + copyHeight;
                var limitedSourceTop = Math.Max(
                    sourceTop,
                    frame.Height - refreshRows);
                logicalDestinationTop += limitedSourceTop - sourceTop;
                sourceTop = limitedSourceTop;
                copyHeight = Math.Max(0, sourceBottom - sourceTop);
            }
            else
            {
                copyHeight = Math.Min(copyHeight, refreshRows);
            }
        }

        // The initial viewport is the one frame the user can inspect before
        // scrolling. Never replace any part of it with a later smooth-scroll
        // sample: a 2-5 px alignment error in the first small movement would
        // otherwise compress several text rows near the start. Boundary refresh
        // still repairs every pixel newly captured above or below that viewport.
        if (direction == ScrollCaptureDirection.Down &&
            logicalDestinationTop < _initialFrameHeight)
        {
            var skippedRows = Math.Min(
                copyHeight,
                _initialFrameHeight - logicalDestinationTop);
            sourceTop += skippedRows;
            logicalDestinationTop += skippedRows;
            copyHeight -= skippedRows;
        }
        else if (direction == ScrollCaptureDirection.Up &&
                 logicalDestinationTop + copyHeight > 0)
        {
            copyHeight = Math.Max(0, -logicalDestinationTop);
        }

        if (copyHeight <= 0)
        {
            return;
        }

        var destinationTop = _compositeContentTop +
            (logicalDestinationTop - _capturedContentTop);
        using var graphics = Graphics.FromImage(_compositeCache!);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.DrawImage(
            frame,
            new Rectangle(0, destinationTop, frame.Width, copyHeight),
            new Rectangle(0, sourceTop, frame.Width, copyHeight),
            GraphicsUnit.Pixel);
    }

    private void EnsureCompositeCache()
    {
        if (_compositeCache is not null)
        {
            return;
        }

        var width = OutputWidth;
        var height = OutputHeight;
        // Grow capacity ahead of subsequent appends so long captures do not
        // reallocate and copy the entire composite on every accepted frame.
        var headroom = Math.Min(512, Math.Max(64, height / 8));
        var capacity = checked(
            headroom + height +
            Math.Min(512, Math.Max(64, height / 8)));
        var composite = new Bitmap(width, capacity, PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(composite);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            var y = headroom;

            foreach (var segment in _segments)
            {
                graphics.DrawImageUnscaled(segment, 0, y);
                y += segment.Height;
            }

            _compositeCache = composite;
            _compositeContentTop = headroom;
            _compositeUsedHeight = height;
            composite = null;
        }
        finally
        {
            composite?.Dispose();
        }
    }

    private void AppendSegmentToCompositeCache(Bitmap segment)
    {
        if (_compositeCache is null)
        {
            // Lazy full build will include this segment.
            return;
        }

        var width = OutputWidth;
        var requiredHeight = checked(
            _compositeContentTop + _compositeUsedHeight + segment.Height);

        if (requiredHeight > _compositeCache.Height)
        {
            var capacity = Math.Max(
                requiredHeight,
                _compositeCache.Height + Math.Max(segment.Height, _compositeCache.Height / 2));
            var expanded = new Bitmap(width, capacity, PixelFormat.Format32bppPArgb);

            try
            {
                using var graphics = Graphics.FromImage(expanded);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                if (_compositeUsedHeight > 0)
                {
                    graphics.DrawImage(
                        _compositeCache,
                        new Rectangle(
                            0,
                            _compositeContentTop,
                            width,
                            _compositeUsedHeight),
                        new Rectangle(
                            0,
                            _compositeContentTop,
                            width,
                            _compositeUsedHeight),
                        GraphicsUnit.Pixel);
                }

                _compositeCache.Dispose();
                _compositeCache = expanded;
                expanded = null;
            }
            finally
            {
                expanded?.Dispose();
            }
        }

        using (var graphics = Graphics.FromImage(_compositeCache))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImageUnscaled(
                segment,
                0,
                _compositeContentTop + _compositeUsedHeight);
        }

        _compositeUsedHeight = checked(_compositeUsedHeight + segment.Height);
    }

    private void PrependSegmentToCompositeCache(Bitmap segment)
    {
        if (_compositeCache is null)
        {
            return;
        }

        if (_compositeContentTop >= segment.Height)
        {
            _compositeContentTop -= segment.Height;
            using var graphics = Graphics.FromImage(_compositeCache);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(segment, 0, _compositeContentTop);
            _compositeUsedHeight = checked(
                _compositeUsedHeight + segment.Height);
            return;
        }

        var width = OutputWidth;
        var nextUsedHeight = checked(_compositeUsedHeight + segment.Height);
        var headroom = Math.Min(512, Math.Max(64, nextUsedHeight / 8));
        var tailroom = Math.Min(512, Math.Max(64, nextUsedHeight / 8));
        var expanded = new Bitmap(
            width,
            checked(headroom + nextUsedHeight + tailroom),
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(expanded);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(segment, 0, headroom);
            graphics.DrawImage(
                _compositeCache,
                new Rectangle(
                    0,
                    headroom + segment.Height,
                    width,
                    _compositeUsedHeight),
                new Rectangle(
                    0,
                    _compositeContentTop,
                    width,
                    _compositeUsedHeight),
                GraphicsUnit.Pixel);
            _compositeCache.Dispose();
            _compositeCache = expanded;
            expanded = null;
            _compositeContentTop = headroom;
            _compositeUsedHeight = nextUsedHeight;
        }
        finally
        {
            expanded?.Dispose();
        }
    }

    private void InvalidateCompositeCache()
    {
        _compositeCache?.Dispose();
        _compositeCache = null;
        _compositeContentTop = 0;
        _compositeUsedHeight = 0;
    }

    private void EnsurePreviewStripCache(int maximumWidth)
    {
        var targetWidth = Math.Min(maximumWidth, OutputWidth);
        if (_previewStripCache is not null && _previewStripWidth == targetWidth)
        {
            return;
        }

        InvalidatePreviewStripCache();
        var widthScale = targetWidth / (double)OutputWidth;
        var usedHeight = Math.Max(
            1,
            (int)Math.Round(OutputHeight * widthScale));
        var headroom = Math.Min(256, Math.Max(32, usedHeight / 8));
        var capacity = checked(
            headroom + usedHeight +
            Math.Min(256, Math.Max(32, usedHeight / 8)));
        var previewStrip = new Bitmap(
            targetWidth,
            capacity,
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(previewStrip);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var y = headroom;
            var sourceBottom = 0;
            foreach (var segment in _segments)
            {
                sourceBottom += segment.Height;
                var nextY = Math.Max(
                    y,
                    headroom +
                        (int)Math.Round(sourceBottom * widthScale));
                var segmentHeight = nextY - y;
                if (segmentHeight <= 0)
                {
                    continue;
                }

                graphics.DrawImage(
                    segment,
                    new Rectangle(0, y, targetWidth, segmentHeight),
                    new Rectangle(0, 0, segment.Width, segment.Height),
                    GraphicsUnit.Pixel);
                y += segmentHeight;
            }

            _previewStripCache = previewStrip;
            _previewStripContentTop = headroom;
            _previewStripWidth = targetWidth;
            _previewStripUsedHeight = y - headroom;
            _previewStripSourceHeight = OutputHeight;
            previewStrip = null;
        }
        finally
        {
            previewStrip?.Dispose();
        }
    }

    private void AppendSegmentToPreviewStripCache(Bitmap segment)
    {
        if (_previewStripCache is null || _previewStripWidth <= 0)
        {
            return;
        }

        var widthScale = _previewStripWidth / (double)OutputWidth;
        var nextSourceHeight = checked(_previewStripSourceHeight + segment.Height);
        var requiredHeight = Math.Max(
            _previewStripUsedHeight,
            (int)Math.Round(nextSourceHeight * widthScale));
        var segmentHeight = requiredHeight - _previewStripUsedHeight;
        _previewStripSourceHeight = nextSourceHeight;
        if (segmentHeight <= 0)
        {
            return;
        }
        if (_previewStripContentTop + requiredHeight > _previewStripCache.Height)
        {
            var capacity = Math.Max(
                _previewStripContentTop + requiredHeight,
                _previewStripCache.Height +
                    Math.Max(segmentHeight, _previewStripCache.Height / 2));
            var expanded = new Bitmap(
                _previewStripWidth,
                capacity,
                PixelFormat.Format32bppPArgb);

            try
            {
                using var graphics = Graphics.FromImage(expanded);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(
                    _previewStripCache,
                    new Rectangle(
                        0,
                        _previewStripContentTop,
                        _previewStripWidth,
                        _previewStripUsedHeight),
                    new Rectangle(
                        0,
                        _previewStripContentTop,
                        _previewStripWidth,
                        _previewStripUsedHeight),
                    GraphicsUnit.Pixel);
                _previewStripCache.Dispose();
                _previewStripCache = expanded;
                expanded = null;
            }
            finally
            {
                expanded?.Dispose();
            }
        }

        using (var graphics = Graphics.FromImage(_previewStripCache))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(
                segment,
                new Rectangle(
                    0,
                    _previewStripContentTop + _previewStripUsedHeight,
                    _previewStripWidth,
                    segmentHeight),
                new Rectangle(0, 0, segment.Width, segment.Height),
                GraphicsUnit.Pixel);
        }

        _previewStripUsedHeight = requiredHeight;
    }

    private void PrependSegmentToPreviewStripCache(Bitmap segment)
    {
        if (_previewStripCache is null || _previewStripWidth <= 0)
        {
            return;
        }

        var widthScale = _previewStripWidth / (double)OutputWidth;
        var nextSourceHeight = checked(_previewStripSourceHeight + segment.Height);
        var nextUsedHeight = Math.Max(
            _previewStripUsedHeight,
            (int)Math.Round(nextSourceHeight * widthScale));
        var segmentHeight = nextUsedHeight - _previewStripUsedHeight;
        _previewStripSourceHeight = nextSourceHeight;
        if (segmentHeight <= 0)
        {
            return;
        }

        if (_previewStripContentTop >= segmentHeight)
        {
            _previewStripContentTop -= segmentHeight;
            using var graphics = Graphics.FromImage(_previewStripCache);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(
                segment,
                new Rectangle(
                    0,
                    _previewStripContentTop,
                    _previewStripWidth,
                    segmentHeight),
                new Rectangle(0, 0, segment.Width, segment.Height),
                GraphicsUnit.Pixel);
            _previewStripUsedHeight = nextUsedHeight;
            return;
        }

        var headroom = Math.Min(256, Math.Max(32, nextUsedHeight / 8));
        var expanded = new Bitmap(
            _previewStripWidth,
            checked(
                headroom + nextUsedHeight +
                Math.Min(256, Math.Max(32, nextUsedHeight / 8))),
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(expanded);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(
                segment,
                new Rectangle(0, headroom, _previewStripWidth, segmentHeight),
                new Rectangle(0, 0, segment.Width, segment.Height),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                _previewStripCache,
                new Rectangle(
                    0,
                    headroom + segmentHeight,
                    _previewStripWidth,
                    _previewStripUsedHeight),
                new Rectangle(
                    0,
                    _previewStripContentTop,
                    _previewStripWidth,
                    _previewStripUsedHeight),
                GraphicsUnit.Pixel);
            _previewStripCache.Dispose();
            _previewStripCache = expanded;
            expanded = null;
            _previewStripContentTop = headroom;
            _previewStripUsedHeight = nextUsedHeight;
        }
        finally
        {
            expanded?.Dispose();
        }
    }

    private void InvalidatePreviewStripCache()
    {
        _previewStripCache?.Dispose();
        _previewStripCache = null;
        _previewStripContentTop = 0;
        _previewStripUsedHeight = 0;
        _previewStripWidth = 0;
        _previewStripSourceHeight = 0;
    }

    public Bitmap ComposePreview(int maximumWidth, int maximumHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);

        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("没有可预览的滚动截图帧。");
        }

        using var composite = Compose();
        return ScalePreview(
            composite,
            0,
            OutputWidth,
            OutputHeight,
            maximumWidth,
            maximumHeight);
    }

    public Bitmap ComposeLivePreview(int maximumWidth, int maximumHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);

        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("没有可预览的滚动截图帧。");
        }

        // Keep a narrow, incrementally appended strip for live preview. Scaling
        // this small cache is independent of the full-resolution long image.
        // The preview always shows the WHOLE stitched image — the user reads
        // it as a global map of what has been captured so far — so the strip
        // is only scaled down (aspect preserved) when it outgrows the height
        // budget, never cropped to a slice.
        EnsurePreviewStripCache(maximumWidth);
        var stripRegion = new Rectangle(
            0,
            _previewStripContentTop,
            _previewStripWidth,
            Math.Max(1, _previewStripUsedHeight));

        if (stripRegion.Height <= maximumHeight)
        {
            return _previewStripCache!.Clone(
                stripRegion,
                PixelFormat.Format32bppPArgb);
        }

        var scale = maximumHeight / (double)stripRegion.Height;
        var scaledWidth = Math.Max(1, (int)Math.Round(stripRegion.Width * scale));
        var scaled = new Bitmap(
            scaledWidth,
            maximumHeight,
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(scaled);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                _previewStripCache!,
                new Rectangle(0, 0, scaledWidth, maximumHeight),
                stripRegion,
                GraphicsUnit.Pixel);
            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
    }

    private static Bitmap ScalePreview(
        Bitmap source,
        int sourceTop,
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int maximumHeight)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)sourceWidth,
                maximumHeight / (double)sourceHeight));
        var previewWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var previewHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var preview = new Bitmap(
            previewWidth,
            previewHeight,
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(preview);
            graphics.Clear(Color.FromArgb(255, 246, 248, 249));
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, previewWidth, previewHeight),
                new Rectangle(0, sourceTop, sourceWidth, sourceHeight),
                GraphicsUnit.Pixel);

            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var segment in _segments)
        {
            segment.Dispose();
        }

        _segments.Clear();
        _segmentAnchorHashCounts.Clear();
        _viewportHistory.Clear();

        foreach (var viewport in _recentViewports)
        {
            viewport.Frame.Dispose();
        }

        _recentViewports.Clear();
        _currentFrame?.Dispose();
        _currentFrame = null;
        _topBoundaryFrame?.Dispose();
        _topBoundaryFrame = null;
        _bottomBoundaryFrame?.Dispose();
        _bottomBoundaryFrame = null;
        InvalidateCompositeCache();
        InvalidatePreviewStripCache();
    }

    private static int GetSearchMinimumOverlapRows(
        int frameHeight,
        ScrollCaptureOptions options)
    {
        return frameHeight >= 480
            ? Math.Max(
                options.MinimumOverlapRows,
                Math.Min(64, frameHeight / 12))
            : options.MinimumOverlapRows;
    }

    private AlignmentCandidate? FindAlignmentCandidate(
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? preferredNewRows,
        bool preferredNeighborhoodOnly = false,
        double? minimumConfidenceOverride = null)
    {
        var previousFrame = direction == ScrollCaptureDirection.Down
            ? _currentFrame!
            : frame;
        var currentFrame = direction == ScrollCaptureDirection.Down
            ? frame
            : _currentFrame!;
        // Continuous user scrolling changes the viewport by a similar amount
        // frame-to-frame. Prefer that displacement when two periods of repeated
        // content would otherwise score almost equally.
        var minimumOverlapRows = GetSearchMinimumOverlapRows(
            frame.Height,
            options);
        var match = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows,
            minimumConfidenceOverride ?? options.MinimumOverlapConfidence,
            options.MinimumNewRows,
            preferredNewRows,
            preferredNeighborhoodOnly);

        return match is null
            ? null
            : new AlignmentCandidate(direction, match);
    }

    private AlignmentCandidate RecheckSevereTemporalUndershoot(
        Bitmap frame,
        AlignmentCandidate candidate,
        int? expectedNewRows,
        ScrollCaptureOptions options)
    {
        var candidateRows = frame.Height - candidate.Match.OverlapRows;
        if (!ShouldRecheckTemporalUndershoot(
                candidate,
                candidateRows,
                expectedNewRows,
                options))
        {
            return candidate;
        }

        LastTemporalUndershootRows = candidateRows;
        var previousFrame = candidate.Direction == ScrollCaptureDirection.Down
            ? _currentFrame!
            : frame;
        var currentFrame = candidate.Direction == ScrollCaptureDirection.Down
            ? frame
            : _currentFrame!;
        var replacementMatch = FindTemporalUndershootReplacement(
            previousFrame,
            currentFrame,
            candidate.Match,
            expectedNewRows!.Value,
            options);
        if (replacementMatch is null)
        {
            return candidate;
        }

        var replacementRows = frame.Height - replacementMatch.OverlapRows;
        LastTemporalReplacementRows = replacementRows;
        return new AlignmentCandidate(candidate.Direction, replacementMatch);
    }

    internal static ImageOverlapMatch? FindTemporalUndershootReplacement(
        Bitmap previousFrame,
        Bitmap currentFrame,
        ImageOverlapMatch originalMatch,
        int expectedNewRows,
        ScrollCaptureOptions options)
    {
        var maximumNewRows = currentFrame.Height - GetSearchMinimumOverlapRows(
            currentFrame.Height,
            options);
        var preferredRows = Math.Min(expectedNewRows, maximumNewRows);
        var minimumReplacementRows = Math.Max(
            options.MinimumNewRows,
            (preferredRows + 1) / 2);
        if (minimumReplacementRows > maximumNewRows)
        {
            return null;
        }

        var replacementMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            GetSearchMinimumOverlapRows(currentFrame.Height, options),
            Math.Max(
                options.MinimumOverlapConfidence,
                originalMatch.Confidence -
                    TemporalUndershootReplacementConfidenceGap),
            minimumReplacementRows,
            preferredRows);
        if (replacementMatch is null)
        {
            return null;
        }

        var originalRows = currentFrame.Height - originalMatch.OverlapRows;
        var replacementRows = currentFrame.Height - replacementMatch.OverlapRows;
        return ShouldReplaceTemporalUndershoot(
            originalRows,
            replacementRows,
            preferredRows)
                ? replacementMatch
                : null;
    }

    private bool ShouldRecheckTemporalUndershoot(
        AlignmentCandidate candidate,
        int candidateRows,
        int? expectedNewRows,
        ScrollCaptureOptions options)
    {
        return expectedNewRows is { } expected &&
            expected >= Math.Max(48, options.MinimumNewRows * 4) &&
            candidateRows * SevereTemporalUndershootDivisor < expected &&
            candidate.Direction == _lastMatchedDirection &&
            candidate.ReferenceTopOverride is null &&
            !candidate.IsBridged &&
            _unlocatedRunLength == 0 &&
            _wheelReturnDirection is null;
    }

    internal static bool ShouldReplaceTemporalUndershoot(
        int originalRows,
        int replacementRows,
        int preferredRows)
    {
        return replacementRows > originalRows &&
            Math.Abs(replacementRows - preferredRows) +
                AlignmentToleranceRows <
            Math.Abs(originalRows - preferredRows);
    }

    private AlignmentCandidate? TryFindAdjacentBoundaryCandidate(
        Bitmap frame,
        ScrollCaptureDirection direction,
        int? preferredNewRows,
        ScrollCaptureOptions options)
    {
        if (_currentFrame is null || preferredNewRows is null)
        {
            return null;
        }

        var previousFrame = direction == ScrollCaptureDirection.Down
            ? _currentFrame
            : frame;
        var currentFrame = direction == ScrollCaptureDirection.Down
            ? frame
            : _currentFrame;
        var match = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            GetSearchMinimumOverlapRows(frame.Height, options),
            BoundaryDecisiveMatchConfidence,
            options.MinimumNewRows,
            preferredNewRows,
            preferredNeighborhoodOnly: true);
        return match is null
            ? null
            : new AlignmentCandidate(direction, match);
    }

    private static AlignmentCandidate? SelectAlignmentCandidate(
        AlignmentCandidate? preferredCandidate,
        AlignmentCandidate? alternateCandidate)
    {
        if (preferredCandidate is null)
        {
            return alternateCandidate;
        }

        if (alternateCandidate is null ||
            alternateCandidate.Match.Confidence <
                preferredCandidate.Match.Confidence +
                    OppositeDirectionConfidenceAdvantage)
        {
            return preferredCandidate;
        }

        return alternateCandidate;
    }

    private static IEnumerable<AlignmentCandidate> OrderAlignmentCandidates(
        AlignmentCandidate? preferredCandidate,
        AlignmentCandidate? alternateCandidate)
    {
        var selectedCandidate = SelectAlignmentCandidate(
            preferredCandidate,
            alternateCandidate);

        if (selectedCandidate is not null)
        {
            yield return selectedCandidate;
        }

        var otherCandidate = selectedCandidate?.Direction ==
            preferredCandidate?.Direction
            ? alternateCandidate
            : preferredCandidate;

        if (otherCandidate is not null)
        {
            yield return otherCandidate;
        }
    }

    private bool IsAlignmentCandidateConsistent(
        Bitmap frame,
        AlignmentCandidate candidate,
        ScrollCaptureOptions options)
    {
        var newRows = frame.Height - candidate.Match.OverlapRows;

        if (newRows < options.MinimumNewRows)
        {
            return false;
        }

        var referenceTop = candidate.ReferenceTopOverride ?? _currentFrameTop;
        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(referenceTop + newRows)
            : checked(referenceTop - newRows);
        var nextFrameBottom = checked(nextFrameTop + frame.Height);
        var remainsInsideCapturedRange =
            nextFrameTop >= _capturedContentTop &&
            nextFrameBottom <= _capturedContentBottom;
        if (!remainsInsideCapturedRange && _unlocatedRunLength == 0)
        {
            // The current frame is already the temporal anchor for ordinary
            // boundary expansion. Re-matching every recent viewport here made
            // one input frame perform up to five full image searches.
            // While the chain is broken this shortcut is exactly how a false
            // peak on repetitive content sneaks into the output, so lost-state
            // candidates always face the verification below.
            return true;
        }

        // No independent reference can adjudicate this placement — accept and
        // let the caller's stale-anchor guard demand decisive confidence.
        return VerifyAgainstIndependentViewport(
            frame,
            nextFrameTop,
            candidate.ReferenceTopOverride,
            options) ?? true;
    }

    /// <summary>
    /// Confirms or refutes a proposed placement against a stored viewport
    /// other than the frame the candidate was matched with. Returns null when
    /// no stored viewport could overlap the placement enough to judge it.
    /// </summary>
    private bool? VerifyAgainstIndependentViewport(
        Bitmap frame,
        int nextFrameTop,
        int? referenceTopOverride,
        ScrollCaptureOptions options)
    {
        var matchedReferenceTop = referenceTopOverride ?? _currentFrameTop;
        var minimumVerificationOverlap = Math.Max(
            options.MinimumOverlapRows * 2,
            Math.Min(48, frame.Height / 4));
        var verificationViewport = _recentViewports
            .Where(viewport => viewport.FrameTop != matchedReferenceTop)
            .Select(viewport => new
            {
                Viewport = viewport,
                ExpectedNewRows = Math.Abs(nextFrameTop - viewport.FrameTop),
            })
            .Where(candidate =>
                candidate.ExpectedNewRows >= options.MinimumNewRows &&
                frame.Height - candidate.ExpectedNewRows >=
                    minimumVerificationOverlap)
            .OrderBy(candidate =>
                Math.Abs(candidate.Viewport.FrameTop - nextFrameTop))
            .FirstOrDefault();

        if (verificationViewport is null)
        {
            return null;
        }

        var anchor = verificationViewport.Viewport;
        var expectedRows = verificationViewport.ExpectedNewRows;
        // The verdict only cares whether the match lands within a few rows of
        // the expected displacement, and the fast-path neighborhood always
        // covers that. A global search could only find some other peak, which
        // the tolerance below rejects anyway — so skip its cost.
        var verificationMatch = nextFrameTop > anchor.FrameTop
            ? AutomaticImageOverlapMatcher.FindVerticalOverlap(
                anchor.Frame,
                frame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                expectedRows,
                preferredNeighborhoodOnly: true)
            : AutomaticImageOverlapMatcher.FindVerticalOverlap(
                frame,
                anchor.Frame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                expectedRows,
                preferredNeighborhoodOnly: true);

        return verificationMatch is not null &&
               Math.Abs(
                   frame.Height - verificationMatch.OverlapRows - expectedRows) <=
                   AlignmentToleranceRows;
    }

    private bool IsCandidateVerifiedByIndependentReference(
        Bitmap frame,
        AlignmentCandidate candidate,
        ScrollCaptureOptions options)
    {
        var newRows = frame.Height - candidate.Match.OverlapRows;
        var referenceTop = candidate.ReferenceTopOverride ?? _currentFrameTop;
        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(referenceTop + newRows)
            : checked(referenceTop - newRows);
        return VerifyAgainstIndependentViewport(
            frame,
            nextFrameTop,
            candidate.ReferenceTopOverride,
            options) == true;
    }

    /// <summary>
    /// Verifies a boundary crossing against the stored boundary frame and, on
    /// success, corrects the crossing position to that absolute evidence.
    /// </summary>
    /// <remarks>
    /// A long walk through periodic content lets individual steps lock onto a
    /// neighboring repetition, so the anchor's logical position accumulates a
    /// small drift the relative cross-checks cannot see. The boundary frame is
    /// the one absolute reference: when its match lands a few rows away from
    /// the predicted position, rejecting the frame can never converge —
    /// every following frame inherits the same drift and the capture stalls at
    /// the boundary forever. A decisive boundary match instead re-anchors the
    /// crossing, which zeroes the drift and keeps the prepended pixels exactly
    /// consistent with the stored boundary.
    /// </remarks>
    private bool TryResolveBoundaryExpansion(
        Bitmap frame,
        AlignmentCandidate candidate,
        ScrollCaptureDirection requestedDirection,
        int? wheelExpectedRows,
        ref int nextFrameTop,
        ref int nextFrameBottom,
        ScrollCaptureOptions options)
    {
        Bitmap previousFrame;
        Bitmap currentFrame;
        int candidateBoundaryNewRows;
        int? wheelBoundaryNewRows = null;

        // A sustained unchanged viewport with fresh wheel input is stronger
        // boundary evidence than a later match against repetitive rows. Keep
        // the live anchor at the confirmed edge, but never append/prepend past
        // it when the user returns after retracing already captured content.
        if (candidate.Direction == ScrollCaptureDirection.Down &&
            _bottomBoundaryReached &&
            nextFrameBottom > _capturedContentBottom &&
            _bottomBoundaryFrame is not null)
        {
            nextFrameTop = _bottomBoundaryFrameTop;
            nextFrameBottom = checked(nextFrameTop + frame.Height);
            LastRejectReason = "confirmed-bottom";
            return true;
        }

        if (candidate.Direction == ScrollCaptureDirection.Up &&
            _topBoundaryReached &&
            nextFrameTop < _capturedContentTop &&
            _topBoundaryFrame is not null)
        {
            nextFrameTop = _topBoundaryFrameTop;
            nextFrameBottom = checked(nextFrameTop + frame.Height);
            LastRejectReason = "confirmed-top";
            return true;
        }

        // A decisive adjacent-frame match that crosses the edge by only a
        // small strip is normally the safest boundary case. Do not take this
        // shortcut while returning through already captured content: wheel
        // re-anchoring is intentionally approximate, and a periodic code row
        // can make the adjacent match look perfect at the wrong absolute
        // position. The stored boundary frame must correct that accumulated
        // drift before any pixels are prepended or appended.
        var directCrossingRows = candidate.Direction == ScrollCaptureDirection.Up
            ? _capturedContentTop - nextFrameTop
            : nextFrameBottom - _capturedContentBottom;
        var directCrossesBoundary = directCrossingRows > 0;
        if (candidate.ReferenceTopOverride is null &&
            !candidate.IsBridged &&
            _wheelReturnDirection != candidate.Direction &&
            directCrossesBoundary &&
            directCrossingRows <= Math.Max(24, options.MinimumNewRows * 4) &&
            candidate.Match.Confidence >= BoundaryDecisiveMatchConfidence)
        {
            LastBoundaryDriftRows = 0;
            LastBoundaryConfidence = candidate.Match.Confidence;
            return true;
        }

        if (candidate.ReferenceTopOverride is not null)
        {
            // A re-anchored candidate was matched directly against a boundary
            // frame — the absolute evidence this verification exists to
            // consult — so there is no drift to detect.
            var crossesBoundary = candidate.Direction == ScrollCaptureDirection.Up
                ? nextFrameTop < _capturedContentTop
                : nextFrameBottom > _capturedContentBottom;
            if (crossesBoundary)
            {
                var distanceToBoundary = candidate.Direction ==
                    ScrollCaptureDirection.Up
                        ? Math.Max(0, _currentFrameTop - _capturedContentTop)
                        : Math.Max(
                            0,
                            _capturedContentBottom -
                            (_currentFrameTop + frame.Height));
                var wheelPredictsCrossing = requestedDirection ==
                        candidate.Direction &&
                    wheelExpectedRows is { } expectedRows &&
                    expectedRows - distanceToBoundary >=
                        options.MinimumNewRows;
                var predictedBeyondBoundary = wheelExpectedRows is { } crossingRows
                    ? crossingRows - distanceToBoundary
                    : 0;
                var boundaryFrame = candidate.Direction ==
                    ScrollCaptureDirection.Up
                        ? _topBoundaryFrame
                        : _bottomBoundaryFrame;
                var oppositeMatch = boundaryFrame is null
                    ? null
                    : candidate.Direction == ScrollCaptureDirection.Up
                        ? AutomaticImageOverlapMatcher.FindVerticalOverlap(
                            boundaryFrame,
                            frame,
                            options.MinimumOverlapRows,
                            options.MinimumOverlapConfidence,
                            options.MinimumNewRows,
                            frame.Height - candidate.Match.OverlapRows,
                            preferredNeighborhoodOnly: false)
                        : AutomaticImageOverlapMatcher.FindVerticalOverlap(
                            frame,
                            boundaryFrame,
                            options.MinimumOverlapRows,
                            options.MinimumOverlapConfidence,
                            options.MinimumNewRows,
                            frame.Height - candidate.Match.OverlapRows,
                            preferredNeighborhoodOnly: false);
                var oppositeIsStronger = oppositeMatch is not null &&
                    oppositeMatch.Confidence >=
                        candidate.Match.Confidence + 0.01;
                var hasExactWheelBoundaryEvidence =
                    wheelPredictsCrossing &&
                    candidate.ReferenceTopOverride is not null;
                var isShortExactWheelBoundary =
                    hasExactWheelBoundaryEvidence &&
                    predictedBeyondBoundary <= Math.Max(96, frame.Height / 3);
                var minimumBoundaryConfidence = hasExactWheelBoundaryEvidence
                    ? isShortExactWheelBoundary
                        ? ExactWheelBoundaryMinimumConfidence
                        : BoundaryDecisiveMatchConfidence
                    : DecisiveMatchConfidence;
                LastBoundaryDriftRows = 0;
                LastBoundaryConfidence = candidate.Match.Confidence;
                if (!wheelPredictsCrossing ||
                    candidate.Match.Confidence < minimumBoundaryConfidence ||
                    oppositeIsStronger)
                {
                    LastRejectReason = oppositeIsStronger
                        ? "boundary-opposite-direction-veto"
                        : "boundary-confidence-veto";
                    return false;
                }
            }

            return true;
        }

        if (candidate.Direction == ScrollCaptureDirection.Up &&
            _currentFrameTop > _capturedContentTop &&
            nextFrameTop < _capturedContentTop &&
            _topBoundaryFrame is not null)
        {
            previousFrame = frame;
            currentFrame = _topBoundaryFrame;
            candidateBoundaryNewRows = _topBoundaryFrameTop - nextFrameTop;

            if (requestedDirection == ScrollCaptureDirection.Up &&
                wheelExpectedRows is { } wheelRows)
            {
                var rowsToBoundary = _currentFrameTop - _capturedContentTop;
                var predictedBeyondBoundary = wheelRows - rowsToBoundary;
                if (predictedBeyondBoundary >= options.MinimumNewRows)
                {
                    wheelBoundaryNewRows = predictedBeyondBoundary;
                }
            }
        }
        else if (candidate.Direction == ScrollCaptureDirection.Down &&
                 _currentFrameTop + frame.Height < _capturedContentBottom &&
                 nextFrameBottom > _capturedContentBottom &&
                 _bottomBoundaryFrame is not null)
        {
            previousFrame = _bottomBoundaryFrame;
            currentFrame = frame;
            candidateBoundaryNewRows = nextFrameTop - _bottomBoundaryFrameTop;

            if (requestedDirection == ScrollCaptureDirection.Down &&
                wheelExpectedRows is { } wheelRows)
            {
                var rowsToBoundary = _capturedContentBottom -
                    (_currentFrameTop + frame.Height);
                var predictedBeyondBoundary = wheelRows - rowsToBoundary;
                if (predictedBeyondBoundary >= options.MinimumNewRows)
                {
                    wheelBoundaryNewRows = predictedBeyondBoundary;
                }
            }
        }
        else
        {
            return true;
        }

        var preferredBoundaryNewRows = wheelBoundaryNewRows is { } wheelBoundary &&
            wheelBoundary <= frame.Height - options.MinimumOverlapRows
                ? wheelBoundary
                : candidateBoundaryNewRows;
        // A reverse fling can cross the stored edge on an inertia frame that
        // carries no fresh wheel tick.  The adjacent-frame match still gives
        // us the predicted displacement, and the boundary frame below is the
        // absolute verification.  Do not reject this case before consulting
        // that evidence; otherwise a legitimate 0.96-0.998 seam match stalls
        // permanently at the starting viewport.
        var expectedOverlapRows = frame.Height - preferredBoundaryNewRows;
        if (preferredBoundaryNewRows < options.MinimumNewRows ||
            expectedOverlapRows < options.MinimumOverlapRows)
        {
            // A jump larger than one viewport cannot be verified safely. Drop
            // this transitional frame and wait for the next captured viewport.
            LastRejectReason = "boundary-unverifiable";
            return false;
        }

        // The stored edge is an absolute reference, so the trailing-band
        // (minimap) retry is safe here and prevents editor reverse walks
        // from stalling for seconds on minimap-poisoned misses.
        var boundaryMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            options.MinimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows,
            preferredBoundaryNewRows,
            preferredNeighborhoodOnly: true,
            retryWithoutTrailingBand: true);
        if (boundaryMatch is null && wheelBoundaryNewRows is not null)
        {
            // The wheel prediction can be approximate during a fling. Search
            // globally once at the actual crossing rather than accepting the
            // first periodic table-row peak or repeating global work on every
            // frame while returning through captured content.
            boundaryMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
                previousFrame,
                currentFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                preferredBoundaryNewRows,
                preferredNeighborhoodOnly: false);
        }
        if (boundaryMatch is null)
        {
            // A fast crossing leaves only a narrow strip shared with the
            // boundary frame. If that strip cannot be verified, the primary
            // match is not sufficient: repeated code/chat rows can produce a
            // decisive-looking periodic peak while the live viewport is still
            // on the captured side of the seam. Keep the frame unlocated and
            // wait for a later sample with independent boundary evidence.
            // After a reverse walk the wheel estimate is often several frames
            // late. One global boundary probe is cheaper than stalling the
            // whole chain until a later sample happens to cross cleanly.
            boundaryMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
                previousFrame,
                currentFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                preferredBoundaryNewRows,
                preferredNeighborhoodOnly: false);
            if (boundaryMatch is null)
            {
                _suspectedReturnBoundaryDirection = candidate.Direction;
                LastRejectReason = "boundary-no-match";
                return false;
            }
        }

        // Boundary matching is directional. A periodic code block can produce
        // a plausible Up peak even while the live frame is still below the
        // stored top boundary (the wheel has reversed, but compositor inertia
        // has not). Probe the opposite orientation at the same seam; a clearly
        // stronger Down match is direct evidence that this is not a crossing
        // and must not prepend the apparent strip.
        var oppositeBoundaryMatch = candidate.Direction ==
            ScrollCaptureDirection.Up
            ? AutomaticImageOverlapMatcher.FindVerticalOverlap(
                currentFrame,
                previousFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                candidateBoundaryNewRows,
                preferredNeighborhoodOnly: true)
            : AutomaticImageOverlapMatcher.FindVerticalOverlap(
                currentFrame,
                previousFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                candidateBoundaryNewRows,
                preferredNeighborhoodOnly: true);
        if (oppositeBoundaryMatch is not null &&
            oppositeBoundaryMatch.Confidence >=
                boundaryMatch.Confidence + 0.01)
        {
            LastRejectReason = "boundary-opposite-direction-veto";
            return false;
        }

        var verifiedNewRows = frame.Height - boundaryMatch.OverlapRows;
        var driftRows = verifiedNewRows - candidateBoundaryNewRows;
        LastBoundaryDriftRows = driftRows;
        LastBoundaryConfidence = boundaryMatch.Confidence;

        if (Math.Abs(driftRows) <= AlignmentToleranceRows)
        {
            var hasReturnBoundaryEvidence =
                candidate.ReferenceTopOverride is not null ||
                _wheelReturnDirection == candidate.Direction;
            var hasExactWheelBoundaryEvidence =
                hasReturnBoundaryEvidence &&
                (wheelBoundaryNewRows is not null ||
                 candidate.Match.Confidence >= ExactWheelBoundaryMinimumConfidence);
            var minimumConfidence = hasExactWheelBoundaryEvidence
                ? ExactWheelBoundaryMinimumConfidence
                : BoundaryDecisiveMatchConfidence;
            if (boundaryMatch.Confidence < minimumConfidence)
            {
                _suspectedReturnBoundaryDirection = candidate.Direction;
                LastRejectReason = "boundary-confidence-veto";
                return false;
            }

            return true;
        }

        var agreesWithWheel = wheelBoundaryNewRows is { } predictedBoundaryRows &&
            Math.Abs(verifiedNewRows - predictedBoundaryRows) <=
                Math.Max(24, predictedBoundaryRows / 4);
        var distanceFromCapturedBoundary = candidate.Direction ==
            ScrollCaptureDirection.Up
                ? Math.Max(0, _currentFrameTop - _capturedContentTop)
                : Math.Max(
                    0,
                    _capturedContentBottom -
                    (_currentFrameTop + frame.Height));
        var agreesWithReturnBoundary =
            _wheelReturnDirection == candidate.Direction &&
            distanceFromCapturedBoundary <= Math.Max(64, frame.Height / 4) &&
            boundaryMatch.Confidence >= options.MinimumOverlapConfidence;
        var agreesWithCandidate = Math.Abs(driftRows) <=
            Math.Max(BoundaryReanchorMaximumRows, candidateBoundaryNewRows / 2);
        if (boundaryMatch.Confidence < BoundaryDecisiveMatchConfidence - 0.015 &&
            !agreesWithWheel &&
            !agreesWithReturnBoundary)
        {
            // Not decisive enough to move the anchor, or so far away it is
            // more likely a periodic repetition than accumulated drift.
            return false;
        }

        if (Math.Abs(driftRows) > BoundaryReanchorMaximumRows &&
            !agreesWithWheel &&
            !agreesWithReturnBoundary &&
            !agreesWithCandidate)
        {
            return false;
        }

        if (candidate.Direction == ScrollCaptureDirection.Up)
        {
            nextFrameTop = _topBoundaryFrameTop - verifiedNewRows;
        }
        else
        {
            nextFrameTop = _bottomBoundaryFrameTop + verifiedNewRows;
        }

        nextFrameBottom = checked(nextFrameTop + frame.Height);
        return true;
    }

    private bool TryWalkInsideCapturedRangeByWheel(
        Bitmap frame,
        ScrollCaptureDirection direction,
        int? expectedNewRows,
        ScrollCaptureOptions options)
    {
        if (_currentFrame is null ||
            expectedNewRows is not { } expected ||
            expected < options.MinimumNewRows)
        {
            return false;
        }

        if (_wheelReturnDirection is { } activeReturnDirection &&
            activeReturnDirection != direction)
        {
            // A reversal ends the predicted walk. Carrying its approximate
            // anchor into the opposite leg can make a previously confirmed
            // edge look like new content and append it again.
            _wheelReturnDirection = null;
            return false;
        }

        if (_wheelReturnDirection is null &&
            _lastMatchedDirection == direction)
        {
            return false;
        }

        if (_wheelReturnDirection is null)
        {
            var edgeTolerance = Math.Max(
                AlignmentToleranceRows * 4,
                Math.Max(options.MinimumNewRows * 4, frame.Height / 6));
            var startsAtCapturedEdge = direction == ScrollCaptureDirection.Up
                ? _currentFrameTop + frame.Height >=
                    _capturedContentBottom - edgeTolerance
                : _currentFrameTop <= _capturedContentTop + edgeTolerance;
            // Also allow a reverse walk to begin from anywhere inside the
            // already-stitched range once the wheel disagrees with the last
            // expansion direction. Waiting for the exact bottom/top edge left
            // reverse flings doing expensive full matches for every retrace.
            var isDirectionReversal = _lastMatchedDirection is { } lastDirection &&
                lastDirection != direction;
            var remainsInsideAfterStep = direction == ScrollCaptureDirection.Up
                ? _currentFrameTop - expected > _capturedContentTop
                : _currentFrameTop + frame.Height + expected < _capturedContentBottom;
            if (!startsAtCapturedEdge &&
                !(isDirectionReversal && remainsInsideAfterStep))
            {
                return false;
            }
        }

        int distanceToBoundary;
        int nextTop;
        if (direction == ScrollCaptureDirection.Up)
        {
            distanceToBoundary = _currentFrameTop - _capturedContentTop;
            if (expected >= distanceToBoundary)
            {
                return false;
            }

            nextTop = _currentFrameTop - expected;
        }
        else
        {
            distanceToBoundary = _capturedContentBottom -
                (_currentFrameTop + frame.Height);
            if (expected >= distanceToBoundary)
            {
                return false;
            }

            nextTop = _currentFrameTop + expected;
        }

        ReplaceCurrentFrame(frame, nextTop);
        var fingerprint = AutomaticViewportFingerprint.Create(frame);
        RememberViewport(fingerprint, nextTop);
        RememberRecentViewport(frame, nextTop);
        _lastSuccessfulNewRows = expected;
        _lastMatchedDirection = direction;
        _wheelReturnDirection = direction;
        LastFrameMovementRows = expected;
        LastFrameWasBridged = true;
        LastRejectReason = "wheel-return-reanchor";
        OnViewportLocated();
        return true;
    }

    private AlignmentCandidate? TryCreateWheelBoundaryCrossingCandidate(
        Bitmap frame,
        ScrollCaptureDirection direction,
        int? expectedNewRows,
        ScrollCaptureOptions options)
    {
        if (_wheelReturnDirection != direction ||
            expectedNewRows is not { } expected ||
            expected < options.MinimumNewRows)
        {
            return null;
        }

        Bitmap? previousFrame;
        Bitmap? currentFrame;
        int referenceTop;
        int distanceToBoundary;
        if (direction == ScrollCaptureDirection.Up)
        {
            distanceToBoundary = _currentFrameTop - _capturedContentTop;
            if (expected <= distanceToBoundary)
            {
                return null;
            }

            previousFrame = frame;
            currentFrame = _topBoundaryFrame;
            referenceTop = _topBoundaryFrameTop;
        }
        else
        {
            distanceToBoundary = _capturedContentBottom -
                (_currentFrameTop + frame.Height);
            if (expected <= distanceToBoundary)
            {
                return null;
            }

            previousFrame = _bottomBoundaryFrame;
            currentFrame = frame;
            referenceTop = _bottomBoundaryFrameTop;
        }

        if (previousFrame is null || currentFrame is null)
        {
            return null;
        }

        var predictedBeyondBoundary = expected - distanceToBoundary;
        var maximumNewRows = frame.Height - options.MinimumOverlapRows;
        if (predictedBeyondBoundary < options.MinimumNewRows ||
            predictedBeyondBoundary > maximumNewRows)
        {
            return null;
        }

        var isShortWheelBoundaryApproach =
            _wheelReturnDirection == direction &&
            distanceToBoundary <= Math.Max(64, frame.Height / 3) &&
            expected <= Math.Max(96, frame.Height / 3);
        var boundaryMinimumConfidence = isShortWheelBoundaryApproach
            ? Math.Min(
                options.MinimumOverlapConfidence,
                ExactWheelBoundaryMinimumConfidence)
            : options.MinimumOverlapConfidence;

        // Boundary-anchored crossing verification: the stored edge is an
        // absolute reference, so the trailing-band (minimap) retry is safe
        // and keeps editor crossings from missing for seconds.
        var boundaryMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            options.MinimumOverlapRows,
            boundaryMinimumConfidence,
            options.MinimumNewRows,
            predictedBeyondBoundary,
            preferredNeighborhoodOnly: true,
            retryWithoutTrailingBand: true);
        if (boundaryMatch is null)
        {
            boundaryMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
                previousFrame,
                currentFrame,
                options.MinimumOverlapRows,
                boundaryMinimumConfidence,
                options.MinimumNewRows,
                predictedBeyondBoundary,
                preferredNeighborhoodOnly: false);
        }

        if (boundaryMatch is null)
        {
            return null;
        }

        var verifiedNewRows = frame.Height - boundaryMatch.OverlapRows;
        if (Math.Abs(verifiedNewRows - predictedBeyondBoundary) >
                Math.Max(24, predictedBeyondBoundary / 4) &&
            boundaryMatch.Confidence < DecisiveMatchConfidence)
        {
            return null;
        }

        return new AlignmentCandidate(
            direction,
            boundaryMatch,
            ReferenceTopOverride: referenceTop);
    }

    private bool IsSmallVerifiedBoundaryCrossing(
        Bitmap frame,
        AlignmentCandidate candidate,
        ScrollCaptureOptions options)
    {
        if (candidate.ReferenceTopOverride is not null ||
            candidate.IsBridged ||
            candidate.Match.Confidence < BoundaryDecisiveMatchConfidence)
        {
            return false;
        }

        var newRows = frame.Height - candidate.Match.OverlapRows;
        if (newRows < options.MinimumNewRows)
        {
            return false;
        }

        var nextTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(_currentFrameTop + newRows)
            : checked(_currentFrameTop - newRows);
        var crossesBoundary = candidate.Direction == ScrollCaptureDirection.Up
            ? nextTop < _capturedContentTop
            : nextTop + frame.Height > _capturedContentBottom;
        if (!crossesBoundary)
        {
            return false;
        }

        var crossingRows = candidate.Direction == ScrollCaptureDirection.Up
            ? _capturedContentTop - nextTop
            : nextTop + frame.Height - _capturedContentBottom;
        return crossingRows <= Math.Max(24, options.MinimumNewRows * 4);
    }

    private void OnViewportLocated()
    {
        _unlocatedRunLength = 0;
        _unlocatedTravelRows = 0;
        _unlocatedNetTravelRows = 0;
        _lastLostExpectedRows = 0;
        _lastLostDirection = null;
    }

    private bool ShouldProbeLostRecovery(int phase)
    {
        const int recoveryCycleLength = 3;
        return _unlocatedRunLength >= phase &&
               (_unlocatedRunLength - phase) % recoveryCycleLength == 0;
    }

    private void OnViewportLost(
        int? expectedNewRows,
        ScrollCaptureDirection direction,
        int frameHeight,
        ScrollCaptureOptions options)
    {
        _unlocatedRunLength = Math.Min(_unlocatedRunLength + 1, 1_000_000);

        // The service folds the wheel motion of every unlocated sample into
        // the next one, so a fresh estimate already describes the whole travel
        // of the current run since the anchor was last located. Without wheel
        // evidence the viewport can still be gliding on smooth-scroll inertia;
        // grow the travel conservatively so the stale-anchor guard eventually
        // engages.
        int frameTravelRows;
        if (expectedNewRows is { } expected && expected > 0)
        {
            _unlocatedTravelRows = Math.Max(_unlocatedTravelRows, expected);
            // The carry resets whenever the wheel reverses, so a same-direction
            // estimate that grew describes this frame's share as the growth;
            // anything else starts a fresh run.
            frameTravelRows = direction == _lastLostDirection &&
                expected >= _lastLostExpectedRows
                ? expected - _lastLostExpectedRows
                : expected;
            _lastLostExpectedRows = expected;
        }
        else
        {
            var glide = Math.Max(
                options.MinimumNewRows,
                (_lastSuccessfulNewRows ?? frameHeight / 6) / 2);
            _unlocatedTravelRows = (int)Math.Min(
                (long)_unlocatedTravelRows + glide,
                1_000_000L);
            frameTravelRows = glide;
            _lastLostExpectedRows += glide;
        }

        _lastLostDirection = direction;
        var signedTravel = direction == ScrollCaptureDirection.Down
            ? frameTravelRows
            : -frameTravelRows;
        _unlocatedNetTravelRows = (int)Math.Clamp(
            (long)_unlocatedNetTravelRows + signedTravel,
            -1_000_000L,
            1_000_000L);
    }

    /// <summary>
    /// While the chain is broken the anchor content and the live viewport may
    /// no longer overlap at all. Any pixel match found in that state is
    /// structurally suspect — repetitive content produces peaks just above
    /// the acceptance threshold, and blank-separated identical structures
    /// produce them at 0.999 — so a resume needs evidence proportional to how
    /// far the wheel says the viewport has strayed. The current frame's own
    /// wheel displacement counts toward that travel: a single wheel burst
    /// larger than the viewport severs the overlap just as surely as an
    /// accumulated run of misses.
    /// </summary>
    private bool IsAnchorCandidateTrustworthy(
        Bitmap frame,
        AlignmentCandidate candidate,
        int estimatedTravelRows,
        int? expectedNewRows,
        ScrollCaptureOptions options)
    {
        if (candidate.IsBridged || candidate.ReferenceTopOverride is not null)
        {
            return true;
        }

        if (estimatedTravelRows > frame.Height + (frame.Height / 8))
        {
            // More travel than one whole viewport: the frame shares no pixels
            // with the anchor, so every anchor match — however confident — is
            // a repetition artifact.
            return false;
        }

        var searchMinimumOverlapRows = GetSearchMinimumOverlapRows(
            frame.Height,
            options);
        var anchorOverlapStillPossible =
            estimatedTravelRows <= frame.Height - searchMinimumOverlapRows;
        if (anchorOverlapStillPossible)
        {
            return true;
        }

        // Thin-overlap zone: a legitimate match exists but so do repetition
        // peaks. A candidate the wheel corroborates is trustworthy — a false
        // peak sits at a repetition distance, well away from the measured
        // displacement.
        if (expectedNewRows is { } expected &&
            candidate.Direction == (_lastLostDirection ?? candidate.Direction) &&
            Math.Abs(frame.Height - candidate.Match.OverlapRows - expected) <=
                Math.Max(24, expected / 4))
        {
            return true;
        }

        return candidate.Match.Confidence >= DecisiveMatchConfidence ||
               IsCandidateVerifiedByIndependentReference(
                   frame,
                   candidate,
                   options);
    }

    /// <summary>
    /// Lost-state recovery: match the frame directly against a stored boundary
    /// frame. This is how the capture reconnects when the user scrolls back
    /// toward content that is already stitched after an unmatchable gap — the
    /// anchor may be anywhere, but the boundary frames are exactly the strips
    /// the returning viewport will overlap first.
    /// </summary>
    private AlignmentCandidate? TryReanchorAtBoundary(
        Bitmap frame,
        ScrollCaptureOptions options)
    {
        // The chain usually breaks while expanding, so the boundary in the
        // last matched direction is the most likely reconnect point. The other
        // boundary is probed on later attempts so an overshoot past the whole
        // capture can still reconnect.
        var likelyBottom =
            _lastMatchedDirection is null or ScrollCaptureDirection.Down;
        var probes = new List<(Bitmap Reference, int ReferenceTop)>();

        void AddProbe(Bitmap? reference, int referenceTop)
        {
            if (reference is not null &&
                probes.All(probe => probe.ReferenceTop != referenceTop))
            {
                probes.Add((reference, referenceTop));
            }
        }

        if (likelyBottom)
        {
            AddProbe(_bottomBoundaryFrame, _bottomBoundaryFrameTop);
        }
        else
        {
            AddProbe(_topBoundaryFrame, _topBoundaryFrameTop);
        }

        if (_unlocatedRunLength >= 4)
        {
            AddProbe(_topBoundaryFrame, _topBoundaryFrameTop);
            AddProbe(_bottomBoundaryFrame, _bottomBoundaryFrameTop);
        }

        var minimumOverlapRows = GetSearchMinimumOverlapRows(
            frame.Height,
            options);
        // A lost-state global match against a single reference is exactly the
        // situation that produces marginal false peaks on repetitive content,
        // so a reconnect must be clearly stronger than the bare acceptance
        // threshold.
        var minimumReanchorConfidence = Math.Max(
            options.MinimumOverlapConfidence + 0.02,
            0.965);

        foreach (var (reference, referenceTop) in probes)
        {
            if (reference.Width != frame.Width ||
                reference.Height != frame.Height)
            {
                continue;
            }

            // Below the reference (frame shows content further down).
            var downMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
                reference,
                frame,
                minimumOverlapRows,
                minimumReanchorConfidence,
                options.MinimumNewRows,
                _lastSuccessfulNewRows);

            if (downMatch is not null)
            {
                var candidate = new AlignmentCandidate(
                    ScrollCaptureDirection.Down,
                    downMatch,
                    ReferenceTopOverride: referenceTop);
                if (IsReanchorConsistentWithWheelTravel(
                        frame,
                        candidate,
                        referenceTop) &&
                    IsAlignmentCandidateConsistent(frame, candidate, options))
                {
                    return candidate;
                }
            }

            // Above the reference (frame shows content further up).
            var upMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
                frame,
                reference,
                minimumOverlapRows,
                minimumReanchorConfidence,
                options.MinimumNewRows,
                _lastSuccessfulNewRows);

            if (upMatch is not null)
            {
                var candidate = new AlignmentCandidate(
                    ScrollCaptureDirection.Up,
                    upMatch,
                    ReferenceTopOverride: referenceTop);
                if (IsReanchorConsistentWithWheelTravel(
                        frame,
                        candidate,
                        referenceTop) &&
                    IsAlignmentCandidateConsistent(frame, candidate, options))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// On repetitive content a global re-anchor search can lock onto a
    /// repetition with near-perfect confidence, and pairwise verification
    /// agrees because every stored reference is shifted by the same period.
    /// The wheel's integrated travel is the one signal a repetition cannot
    /// fake, so a reconnect may not contradict it wildly.
    /// </summary>
    private bool IsReanchorConsistentWithWheelTravel(
        Bitmap frame,
        AlignmentCandidate candidate,
        int referenceTop)
    {
        var newRows = frame.Height - candidate.Match.OverlapRows;
        var proposedTop = candidate.Direction == ScrollCaptureDirection.Down
            ? referenceTop + newRows
            : referenceTop - newRows;
        var proposedBottom = proposedTop + frame.Height;
        var crossesCapturedBoundary = candidate.Direction ==
            ScrollCaptureDirection.Up
                ? proposedTop < _capturedContentTop
                : proposedBottom > _capturedContentBottom;
        if (crossesCapturedBoundary)
        {
            // A direct match against a stored edge is especially ambiguous on
            // repeated code/chat rows. It may not teleport an anchor that is
            // still deep inside the captured range across that edge unless
            // accumulated wheel travel independently reaches the same edge in
            // the same direction. Adjacent live-frame crossings use the normal
            // path and are unaffected by this lost-state safeguard.
            var distanceToBoundary = candidate.Direction ==
                ScrollCaptureDirection.Up
                    ? Math.Max(0, _currentFrameTop - _capturedContentTop)
                    : Math.Max(
                        0,
                        _capturedContentBottom -
                        (_currentFrameTop + frame.Height));
            var reachesBoundary = candidate.Direction ==
                ScrollCaptureDirection.Up
                    ? _unlocatedNetTravelRows <= -(distanceToBoundary + 1)
                    : _unlocatedNetTravelRows >= distanceToBoundary + 1;
            if (!reachesBoundary)
            {
                return false;
            }
        }

        // Weak or short travel estimates cannot adjudicate; let image
        // evidence stand on its own there.
        if (Math.Abs(_unlocatedNetTravelRows) < frame.Height / 2)
        {
            return true;
        }

        var proposedNet = proposedTop - _currentFrameTop;
        var divergence = Math.Abs(proposedNet - _unlocatedNetTravelRows);
        var tolerance = Math.Max(
            (frame.Height * 3) / 4,
            (Math.Abs(_unlocatedNetTravelRows) * 11) / 20);
        return divergence <= tolerance;
    }

    // A bridge accepts the wheel estimate through content too flat to match on
    // pixels. Both join strips must be horizontally featureless and agree on
    // per-row color at the estimated alignment: within such content any
    // placement error is invisible by construction, while content with real
    // texture never qualifies and keeps taking the pixel-matched path.
    private const double BridgeMaximumHorizontalTexture = 1.6;
    private const double BridgeMaximumAverageRowDifference = 9d;
    private const double BridgeMaximumRowDifference = 26d;

    private AlignmentCandidate? TryCreateBridgeCandidate(
        Bitmap frame,
        ScrollCaptureDirection direction,
        int? estimatedNewRows,
        ScrollCaptureOptions options)
    {
        if (_currentFrame is null ||
            estimatedNewRows is not { } estimate ||
            estimate < options.MinimumNewRows)
        {
            return null;
        }

        // Keep a measurable join band. Uniform stretches longer than one
        // viewport are covered by consecutive bridges.
        var maximumBridgeRows = frame.Height - Math.Max(
            16,
            options.MinimumOverlapRows);
        var newRows = Math.Min(estimate, maximumBridgeRows);
        if (newRows < options.MinimumNewRows)
        {
            return null;
        }

        // What a bridge must keep invisible is the pixels it actually welds
        // together in the output. When the placement expands the capture, that
        // weld is at the captured-content boundary — NOT at the anchor, which
        // may have walked away from it — so the flatness requirement applies
        // to the output's edge strip and to the frame rows around the cut.
        var overlapRows = frame.Height - newRows;
        var nextFrameTop = direction == ScrollCaptureDirection.Down
            ? _currentFrameTop + newRows
            : _currentFrameTop - newRows;

        if (direction == ScrollCaptureDirection.Down &&
            nextFrameTop + frame.Height > _capturedContentBottom)
        {
            var cutRow = Math.Clamp(
                _capturedContentBottom - nextFrameTop,
                0,
                frame.Height);
            if (frame.Height - cutRow < options.MinimumNewRows ||
                _bottomBoundaryFrame is null ||
                !IsBridgeJunctionFlat(
                    outputEdgeFrame: _bottomBoundaryFrame,
                    outputEdgeIsBottom: true,
                    frame: frame,
                    frameCutRow: cutRow))
            {
                return null;
            }
        }
        else if (direction == ScrollCaptureDirection.Up &&
                 nextFrameTop < _capturedContentTop)
        {
            var cutRow = Math.Clamp(
                _capturedContentTop - nextFrameTop,
                0,
                frame.Height);
            if (cutRow < options.MinimumNewRows ||
                _topBoundaryFrame is null ||
                !IsBridgeJunctionFlat(
                    outputEdgeFrame: _topBoundaryFrame,
                    outputEdgeIsBottom: false,
                    frame: frame,
                    frameCutRow: cutRow))
            {
                return null;
            }
        }
        else
        {
            // A pure walk moves only the anchor. Its placement error is
            // bounded by the estimate and never touches the output, but the
            // move is only plausible when the pixels genuinely could not be
            // matched — which is what flat strips prove.
            var isFlatJoin = direction == ScrollCaptureDirection.Down
                ? AreStripsFlatAndAligned(
                    _currentFrame,
                    newRows,
                    frame,
                    0,
                    overlapRows)
                : AreStripsFlatAndAligned(
                    frame,
                    newRows,
                    _currentFrame,
                    0,
                    overlapRows);
            if (!isFlatJoin)
            {
                return null;
            }
        }

        // Confidence must clear DecisiveMatchConfidence so a bridge that
        // crosses the capture boundary is not rejected by the boundary
        // verification, which cannot get a verdict from flat pixels either.
        return new AlignmentCandidate(
            direction,
            new ImageOverlapMatch(overlapRows, 0.995, 0),
            IsBridged: true);
    }

    // Rows inspected on each side of a bridge weld.
    private const int BridgeJunctionBandRows = 24;

    /// <summary>
    /// Validates the weld a boundary-expanding bridge would create: the strip
    /// the output currently ends with and the frame strips on both sides of
    /// the cut must all be flat, and the two sides of the weld must agree on
    /// color. Within such pixels any placement error is invisible.
    /// </summary>
    private static bool IsBridgeJunctionFlat(
        Bitmap outputEdgeFrame,
        bool outputEdgeIsBottom,
        Bitmap frame,
        int frameCutRow)
    {
        var edgeBand = Math.Min(BridgeJunctionBandRows, outputEdgeFrame.Height);
        var edgeTop = outputEdgeIsBottom
            ? outputEdgeFrame.Height - edgeBand
            : 0;

        // The frame side of the weld: the strip that will sit against the
        // output edge, i.e. below the cut when appending, above it when
        // prepending.
        var frameBandTop = outputEdgeIsBottom
            ? frameCutRow
            : Math.Max(0, frameCutRow - BridgeJunctionBandRows);
        var frameBand = Math.Min(
            BridgeJunctionBandRows,
            outputEdgeIsBottom
                ? frame.Height - frameCutRow
                : frameCutRow);

        if (edgeBand < 8 || frameBand < 8)
        {
            return false;
        }

        return AreStripsFlatAndAligned(
            outputEdgeFrame,
            edgeTop,
            frame,
            frameBandTop,
            Math.Min(edgeBand, frameBand));
    }

    /// <summary>
    /// Measures whether <paramref name="upperFrame"/> starting at
    /// <paramref name="upperTop"/> and <paramref name="lowerFrame"/> starting
    /// at <paramref name="lowerTop"/> are both horizontally featureless across
    /// <paramref name="bandRows"/> rows and agree on per-row mean color.
    /// </summary>
    private static bool AreStripsFlatAndAligned(
        Bitmap upperFrame,
        int upperTop,
        Bitmap lowerFrame,
        int lowerTop,
        int bandRows)
    {
        if (bandRows < 8)
        {
            return false;
        }

        using var upperPixels = StripPixels.Copy(upperFrame, upperTop, bandRows);
        using var lowerPixels = StripPixels.Copy(lowerFrame, lowerTop, bandRows);

        {
            var width = upperFrame.Width;
            var ignoredRight = width >= 80
                ? Math.Clamp(width / 80, 10, 24)
                : 0;
            var comparisonRight = width - ignoredRight;
            var columnStep = Math.Max(1, (comparisonRight - 0) / 64);
            var rowStep = Math.Max(1, bandRows / 96);
            double totalRowDifference = 0;
            double maximumRowDifference = 0;
            double upperTextureTotal = 0;
            double lowerTextureTotal = 0;
            var comparedRows = 0;

            for (var y = 0; y < bandRows; y += rowStep)
            {
                double upperR = 0, upperG = 0, upperB = 0;
                double lowerR = 0, lowerG = 0, lowerB = 0;
                double upperTexture = 0, lowerTexture = 0;
                var samples = 0;
                byte previousUpperLuma = 0, previousLowerLuma = 0;
                var hasPrevious = false;

                for (var x = 0; x < comparisonRight; x += columnStep)
                {
                    upperPixels.GetRgb(x, y, out var ur, out var ug, out var ub);
                    lowerPixels.GetRgb(x, y, out var lr, out var lg, out var lb);
                    upperR += ur; upperG += ug; upperB += ub;
                    lowerR += lr; lowerG += lg; lowerB += lb;
                    var upperLuma = (byte)((ur * 30 + ug * 59 + ub * 11) / 100);
                    var lowerLuma = (byte)((lr * 30 + lg * 59 + lb * 11) / 100);

                    if (hasPrevious)
                    {
                        upperTexture += Math.Abs(upperLuma - previousUpperLuma);
                        lowerTexture += Math.Abs(lowerLuma - previousLowerLuma);
                    }

                    previousUpperLuma = upperLuma;
                    previousLowerLuma = lowerLuma;
                    hasPrevious = true;
                    samples++;
                }

                if (samples < 8)
                {
                    continue;
                }

                var rowDifference =
                    Math.Abs((upperR - lowerR) / samples) +
                    Math.Abs((upperG - lowerG) / samples) +
                    Math.Abs((upperB - lowerB) / samples);
                totalRowDifference += rowDifference;
                maximumRowDifference = Math.Max(
                    maximumRowDifference,
                    rowDifference);
                upperTextureTotal += upperTexture / Math.Max(1, samples - 1);
                lowerTextureTotal += lowerTexture / Math.Max(1, samples - 1);
                comparedRows++;
            }

            if (comparedRows == 0)
            {
                return false;
            }

            return upperTextureTotal / comparedRows <=
                       BridgeMaximumHorizontalTexture &&
                   lowerTextureTotal / comparedRows <=
                       BridgeMaximumHorizontalTexture &&
                   totalRowDifference / comparedRows <=
                       BridgeMaximumAverageRowDifference &&
                   maximumRowDifference <= BridgeMaximumRowDifference;
        }
    }

    private sealed class StripPixels : IDisposable
    {
        private readonly byte[] _pixels;
        private readonly int _stride;

        private StripPixels(byte[] pixels, int stride)
        {
            _pixels = pixels;
            _stride = stride;
        }

        public static StripPixels Copy(Bitmap bitmap, int top, int rows)
        {
            var data = bitmap.LockBits(
                new Rectangle(0, top, bitmap.Width, rows),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);

            try
            {
                var stride = Math.Abs(data.Stride);
                var pixels = new byte[stride * rows];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                return new StripPixels(pixels, stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        public void GetRgb(int x, int y, out byte r, out byte g, out byte b)
        {
            var offset = (y * _stride) + (x * 4);
            b = _pixels[offset];
            g = _pixels[offset + 1];
            r = _pixels[offset + 2];
        }

        public void Dispose()
        {
        }
    }

    private bool TryFindKnownViewport(
        Bitmap frame,
        AutomaticViewportFingerprint fingerprint,
        ScrollCaptureDirection direction,
        int? preferredNewRows,
        ScrollCaptureOptions options,
        out int frameTop)
    {
        var matchingLocations = _viewportHistory
            .Where(anchor => anchor.Fingerprint.IsSimilarTo(fingerprint))
            .Select(anchor => anchor.FrameTop)
            .ToList();

        if (matchingLocations.Count == 0)
        {
            // Returned dashboard and chat viewports are rarely pixel-identical:
            // timers, token counts, hover states and the scrollbar can all
            // change while the user reverses. A conservative structural match
            // prevents those already-covered frames from extending the output.
            matchingLocations.AddRange(_viewportHistory
                .Where(anchor =>
                    anchor.Fingerprint.IsPreviouslySeenComparedTo(fingerprint))
                .Select(anchor => anchor.FrameTop));

            if (matchingLocations.Count == 0)
            {
                // Return samples almost never land on the exact pixel row that
                // was captured on the outward pass. Search a bounded shift
                // around the anchors nearest the predicted position and turn
                // the match into an absolute document coordinate. Because the
                // coordinate comes from a historical anchor, error does not
                // accumulate across frames as it does with wheel integration.
                var predictedTop = direction == ScrollCaptureDirection.Down
                    ? _currentFrameTop + Math.Max(1, preferredNewRows ?? 1)
                    : _currentFrameTop - Math.Max(1, preferredNewRows ?? 1);
                var maximumShift = Math.Max(
                    48,
                    Math.Min(
                        fingerprint.SampledPixelSpan / 3,
                        Math.Max(96, (preferredNewRows ?? 48) * 2)));
                var nearbyAnchors = _viewportHistory
                    .OrderBy(anchor => Math.Abs(anchor.FrameTop - predictedTop))
                    .Take(4);

                foreach (var anchor in nearbyAnchors)
                {
                    if (!anchor.Fingerprint.TryLocatePreviouslySeenComparedTo(
                            fingerprint,
                            maximumShift,
                            out var pixelShift,
                            out _))
                    {
                        continue;
                    }

                    var locatedTop = anchor.FrameTop + pixelShift;
                    var lastCapturedFrameTop =
                        _capturedContentBottom - fingerprint.SourceHeight;
                    if (locatedTop < _capturedContentTop ||
                        locatedTop > lastCapturedFrameTop)
                    {
                        // A partially new boundary-crossing frame must still
                        // go through pixel overlap verification; historical
                        // recognition is only allowed to walk inside content
                        // that is already present in the output.
                        continue;
                    }

                    locatedTop = RefineHistoricalLocation(
                        frame,
                        locatedTop,
                        options);
                    matchingLocations.Add(locatedTop);
                }

                if (matchingLocations.Count == 0)
                {
                    frameTop = default;
                    return false;
                }
            }
        }

        // Only recognize anchors that lie strictly further in the requested
        // scroll direction. Including FrameTop == _currentFrameTop (or falling
        // back to every similar fingerprint) permanently blocks expansion at
        // the capture boundary: at the top, scrolling further up keeps matching
        // the initial viewport and never prepends new content.
        const int minimumStep = 1;
        var candidatesInRequestedDirection = (direction == ScrollCaptureDirection.Down
            ? matchingLocations.Where(location =>
                location >= _currentFrameTop + minimumStep)
            : matchingLocations.Where(location =>
                location <= _currentFrameTop - minimumStep))
            .Distinct()
            .ToArray();

        if (candidatesInRequestedDirection.Length == 0)
        {
            // No known viewport further in this direction — let alignment expand
            // past the captured boundary (or re-match mid-content by pixels).
            frameTop = default;
            return false;
        }

        var requestedTop = direction == ScrollCaptureDirection.Down
            ? _currentFrameTop + Math.Max(1, preferredNewRows ?? 1)
            : _currentFrameTop - Math.Max(1, preferredNewRows ?? 1);
        frameTop = candidatesInRequestedDirection
            .OrderBy(location => Math.Abs(location - requestedTop))
            .ThenBy(location => Math.Abs(location - _currentFrameTop))
            .First();

        // A historical viewport can be visually identical at several logical
        // coordinates (repeated code functions, chat rows, list items). Do not
        // let that absolute lookup jump the anchor unless the frame also has
        // a temporally consistent overlap with the viewport we just processed.
        // This matters most after a wheel reversal: the input direction can be
        // Up while compositor inertia is still painting a frame from below the
        // starting position. In that case the history match may otherwise move
        // the anchor to zero even though the pixels are still moving Down.
        if (!IsHistoricalLocationTemporallyConsistent(
                frame,
                frameTop,
                options))
        {
            frameTop = default;
            return false;
        }

        var predictedCrossesCapturedBoundary = direction == ScrollCaptureDirection.Up
            ? requestedTop < _capturedContentTop
            : requestedTop + fingerprint.SourceHeight > _capturedContentBottom;
        if (preferredNewRows is not null && predictedCrossesCapturedBoundary)
        {
            // Once fresh wheel evidence carries the viewport beyond a captured
            // edge, an in-range historical lookalike is not allowed to absorb
            // that motion. Repetitive code/chat rows otherwise keep the anchor
            // inside the old image and the new rows appear only seconds later,
            // after a subsequent frame happens to disambiguate the match.
            frameTop = default;
            return false;
        }

        return true;
    }

    private bool IsHistoricalLocationTemporallyConsistent(
        Bitmap frame,
        int historicalTop,
        ScrollCaptureOptions options)
    {
        if (_currentFrame is null)
        {
            return true;
        }

        var movementRows = Math.Abs(historicalTop - _currentFrameTop);
        if (movementRows < options.MinimumNewRows)
        {
            return false;
        }

        // When the historical jump is larger than one viewport there is no
        // direct pixel bridge to verify. The normal lost-anchor safeguards will
        // require boundary evidence in that case, so retain the lookup here.
        var minimumOverlapRows = GetSearchMinimumOverlapRows(
            frame.Height,
            options);
        if (movementRows > frame.Height - minimumOverlapRows)
        {
            return true;
        }

        var temporalDirection = historicalTop > _currentFrameTop
            ? ScrollCaptureDirection.Down
            : ScrollCaptureDirection.Up;
        var previousFrame = temporalDirection == ScrollCaptureDirection.Down
            ? _currentFrame
            : frame;
        var currentFrame = temporalDirection == ScrollCaptureDirection.Down
            ? frame
            : _currentFrame;
        var temporalMatch = AutomaticImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows,
            movementRows,
            preferredNeighborhoodOnly: true);
        if (temporalMatch is null)
        {
            return false;
        }

        var verifiedMovementRows = frame.Height - temporalMatch.OverlapRows;
        return Math.Abs(verifiedMovementRows - movementRows) <=
                   Math.Max(8, movementRows / 3) &&
               temporalMatch.Confidence >= options.MinimumOverlapConfidence;
    }

    private int RefineHistoricalLocation(
        Bitmap frame,
        int approximateTop,
        ScrollCaptureOptions options)
    {
        var references = new List<(Bitmap Frame, int Top)>();

        void AddReference(Bitmap? reference, int top)
        {
            if (reference is not null &&
                references.All(candidate => candidate.Top != top))
            {
                references.Add((reference, top));
            }
        }

        foreach (var viewport in _recentViewports)
        {
            AddReference(viewport.Frame, viewport.FrameTop);
        }

        AddReference(_topBoundaryFrame, _topBoundaryFrameTop);
        AddReference(_bottomBoundaryFrame, _bottomBoundaryFrameTop);

        foreach (var (reference, referenceTop) in references
                     .OrderBy(candidate =>
                         Math.Abs(candidate.Top - approximateTop)))
        {
            var expectedRows = Math.Abs(approximateTop - referenceTop);
            if (expectedRows < options.MinimumNewRows ||
                expectedRows > frame.Height - options.MinimumOverlapRows)
            {
                continue;
            }

            var match = approximateTop > referenceTop
                ? AutomaticImageOverlapMatcher.FindVerticalOverlap(
                    reference,
                    frame,
                    options.MinimumOverlapRows,
                    options.MinimumOverlapConfidence,
                    options.MinimumNewRows,
                    expectedRows,
                    preferredNeighborhoodOnly: true)
                : AutomaticImageOverlapMatcher.FindVerticalOverlap(
                    frame,
                    reference,
                    options.MinimumOverlapRows,
                    options.MinimumOverlapConfidence,
                    options.MinimumNewRows,
                    expectedRows,
                    preferredNeighborhoodOnly: true);
            if (match is null)
            {
                continue;
            }

            var verifiedRows = frame.Height - match.OverlapRows;
            var refinedTop = approximateTop > referenceTop
                ? referenceTop + verifiedRows
                : referenceTop - verifiedRows;
            if (Math.Abs(refinedTop - approximateTop) <= 4)
            {
                return refinedTop;
            }
        }

        return approximateTop;
    }

    private void ReplaceCurrentFrame(Bitmap frame, int frameTop)
    {
        Bitmap? nextCurrentFrame = null;

        try
        {
            nextCurrentFrame = (Bitmap)frame.Clone();
            _currentFrame!.Dispose();
            _currentFrame = nextCurrentFrame;
            nextCurrentFrame = null;
            _currentFrameTop = frameTop;
        }
        finally
        {
            nextCurrentFrame?.Dispose();
        }
    }

    private void RememberViewport(AutomaticViewportFingerprint fingerprint, int frameTop)
    {
        var existingAnchor = _viewportHistory.LastOrDefault(
            anchor => anchor.FrameTop == frameTop);

        if (existingAnchor is not null)
        {
            _viewportHistory.Remove(existingAnchor);
        }

        _viewportHistory.AddLast(new ViewportAnchor(fingerprint, frameTop));

        while (_viewportHistory.Count > MaximumViewportHistory)
        {
            _viewportHistory.RemoveFirst();
        }
    }

    private void RememberRecentViewport(Bitmap frame, int frameTop)
    {
        var existingViewport = _recentViewports.LastOrDefault(
            viewport => viewport.FrameTop == frameTop);

        if (existingViewport is not null)
        {
            _recentViewports.Remove(existingViewport);
            existingViewport.Frame.Dispose();
        }

        Bitmap? frameClone = null;

        try
        {
            frameClone = (Bitmap)frame.Clone();
            _recentViewports.AddLast(new RecentViewport(frameClone, frameTop));
            frameClone = null;
        }
        finally
        {
            frameClone?.Dispose();
        }

        while (_recentViewports.Count > MaximumRecentViewports)
        {
            var oldestViewport = _recentViewports.First!.Value;
            _recentViewports.RemoveFirst();
            oldestViewport.Frame.Dispose();
        }
    }

    private static Bitmap CreateSegment(
        Bitmap frame,
        int sourceTop,
        int height,
        int horizontalOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (horizontalOffset == 0)
        {
            return frame.Clone(
                new Rectangle(0, sourceTop, frame.Width, height),
                PixelFormat.Format32bppPArgb);
        }

        // AutomaticImageOverlapMatcher reports previous[x] ~ current[x + horizontalOffset].
        // Remap the extracted strip into the composite's x basis so a 1px compositor
        // drift does not leave a jagged vertical seam at the stitch boundary.
        var segment = new Bitmap(frame.Width, height, PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(segment);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;

            var sourceX = Math.Clamp(horizontalOffset, -frame.Width + 1, frame.Width - 1);
            var destX = sourceX >= 0 ? 0 : -sourceX;
            var copyWidth = frame.Width - Math.Abs(sourceX);
            var sourceRect = new Rectangle(
                Math.Max(0, sourceX),
                sourceTop,
                copyWidth,
                height);
            var destRect = new Rectangle(destX, 0, copyWidth, height);
            graphics.DrawImage(
                frame,
                destRect,
                sourceRect,
                GraphicsUnit.Pixel);

            // Fill the 1px edge exposed by the shift with the nearest column so the
            // long image stays full-width without a transparent gutter.
            if (sourceX > 0)
            {
                graphics.DrawImage(
                    frame,
                    new Rectangle(frame.Width - sourceX, 0, sourceX, height),
                    new Rectangle(frame.Width - 1, sourceTop, 1, height),
                    GraphicsUnit.Pixel);
            }
            else if (sourceX < 0)
            {
                var gap = -sourceX;
                graphics.DrawImage(
                    frame,
                    new Rectangle(0, 0, gap, height),
                    new Rectangle(0, sourceTop, 1, height),
                    GraphicsUnit.Pixel);
            }

            return segment;
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    private bool IsAlreadyCapturedSegment(Bitmap segment)
    {
        if (segment.Height < 64 || _segmentAnchorHashCounts.Count == 0)
        {
            return false;
        }

        var hash = ComputeLeadingBandHash(segment);
        return hash != 0 &&
               _segmentAnchorHashCounts.TryGetValue(hash, out var occurrences) &&
               occurrences == 1;
    }

    private void AddSegmentAnchorHashes(Bitmap segment)
    {
        var bandWidth = segment.Width >= 80
            ? segment.Width - Math.Clamp(segment.Width / 80, 10, 24)
            : segment.Width;
        var bandHeight = Math.Min(64, segment.Height);
        var rectangle = new Rectangle(0, 0, bandWidth, segment.Height);
        var data = segment.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            segment.PixelFormat);

        try
        {
            var bytesPerPixel = Image.GetPixelFormatSize(segment.PixelFormat) / 8;
            if (bytesPerPixel < 3)
            {
                return;
            }

            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * segment.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var stepX = Math.Max(1, bandWidth / 96);
            var lastTop = Math.Max(0, segment.Height - bandHeight);
            var rowHashes = new ulong[segment.Height];
            var rowMinimumLumas = new byte[segment.Height];
            var rowMaximumLumas = new byte[segment.Height];

            for (var y = 0; y < segment.Height; y++)
            {
                rowHashes[y] = ComputeRowHash(
                    buffer,
                    y * stride,
                    bytesPerPixel,
                    bandWidth,
                    stepX,
                    out rowMinimumLumas[y],
                    out rowMaximumLumas[y]);
            }

            for (var top = 0; top <= lastTop; top++)
            {
                var hash = 1469598103934665603UL;
                byte minimumLuma = byte.MaxValue;
                byte maximumLuma = byte.MinValue;
                for (var y = top; y < top + bandHeight; y++)
                {
                    minimumLuma = Math.Min(minimumLuma, rowMinimumLumas[y]);
                    maximumLuma = Math.Max(maximumLuma, rowMaximumLumas[y]);
                    hash ^= rowHashes[y];
                    hash *= 1099511628211UL;
                }

                if (maximumLuma - minimumLuma >= 12)
                {
                    _segmentAnchorHashCounts[hash] =
                        _segmentAnchorHashCounts.GetValueOrDefault(hash) + 1;
                }
            }
        }
        finally
        {
            segment.UnlockBits(data);
        }
    }

    private static ulong ComputeLeadingBandHash(Bitmap bitmap)
    {
        var bandWidth = bitmap.Width >= 80
            ? bitmap.Width - Math.Clamp(bitmap.Width / 80, 10, 24)
            : bitmap.Width;
        var bandHeight = Math.Min(64, bitmap.Height);
        var rectangle = new Rectangle(0, 0, bandWidth, bandHeight);
        var data = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            bitmap.PixelFormat);

        try
        {
            var bytesPerPixel = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
            if (bytesPerPixel < 3)
            {
                return 0;
            }

            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bandHeight];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var hash = 1469598103934665603UL;
            byte minimumLuma = byte.MaxValue;
            byte maximumLuma = byte.MinValue;
            var stepX = Math.Max(1, bandWidth / 96);
            for (var y = 0; y < bandHeight; y++)
            {
                var rowHash = ComputeRowHash(
                    buffer,
                    y * stride,
                    bytesPerPixel,
                    bandWidth,
                    stepX,
                    out var rowMinimumLuma,
                    out var rowMaximumLuma);
                minimumLuma = Math.Min(minimumLuma, rowMinimumLuma);
                maximumLuma = Math.Max(maximumLuma, rowMaximumLuma);
                hash ^= rowHash;
                hash *= 1099511628211UL;
            }

            return maximumLuma - minimumLuma >= 12 ? hash : 0;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static ulong ComputeRowHash(
        byte[] buffer,
        int rowOffset,
        int bytesPerPixel,
        int width,
        int stepX,
        out byte minimumLuma,
        out byte maximumLuma)
    {
        var hash = 1469598103934665603UL;
        minimumLuma = byte.MaxValue;
        maximumLuma = byte.MinValue;

        for (var x = 0; x < width; x += stepX)
        {
            var offset = rowOffset + (x * bytesPerPixel);
            var b = buffer[offset];
            var g = buffer[offset + 1];
            var r = buffer[offset + 2];
            var luma = (byte)((r * 30 + g * 59 + b * 11) / 100);
            minimumLuma = Math.Min(minimumLuma, luma);
            maximumLuma = Math.Max(maximumLuma, luma);
            hash ^= (uint)(luma >> 3);
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static void ValidateOptions(ScrollCaptureOptions options, int frameHeight)
    {
        if (options.MaximumFrames < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ScrollDelta == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MinimumOverlapRows <= 0 ||
            options.MinimumOverlapRows >= frameHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MinimumNewRows <= 0 ||
            options.MinimumNewRows >= frameHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.FrameDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <param name="ReferenceTopOverride">
    /// Logical top of the frame this candidate was matched against when that
    /// frame is not the current anchor (boundary re-anchoring). Null for
    /// ordinary anchor-relative candidates.
    /// </param>
    /// <param name="IsBridged">
    /// True when the displacement is the wheel estimate accepted through
    /// pixel-unmatchable flat content rather than an image match.
    /// </param>
    private sealed record AlignmentCandidate(
        ScrollCaptureDirection Direction,
        ImageOverlapMatch Match,
        int? ReferenceTopOverride = null,
        bool IsBridged = false)
    {
        // Retain the two-argument shape used by the direction-selection tests
        // and ordinary alignment candidates while recovery candidates can add
        // their absolute reference or bridge marker.
        public AlignmentCandidate(
            ScrollCaptureDirection direction,
            ImageOverlapMatch match)
            : this(direction, match, ReferenceTopOverride: null, IsBridged: false)
        {
        }
    }

    private sealed record ViewportAnchor(
        AutomaticViewportFingerprint Fingerprint,
        int FrameTop);

    private sealed record RecentViewport(Bitmap Frame, int FrameTop);
}
