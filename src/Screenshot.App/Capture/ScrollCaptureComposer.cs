using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

public sealed class ScrollCaptureComposer : IDisposable
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
    private const double FreshWheelLocalConfidence = 0.90;
    private const int AlignmentToleranceRows = 3;
    // Largest positional drift a decisive boundary match may correct. Drift
    // accumulates in small mis-steps, while a periodic false peak sits at
    // least one content period away — far beyond this.
    private const int BoundaryReanchorMaximumRows = 64;
    // An image match this strong is direct evidence of where the viewport went.
    // The wheel estimate is only a prior, so it must not be allowed to veto it.
    private const double DecisiveMatchConfidence = 0.99;
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

    public ScrollCaptureComposer()
        : this(detectStationaryLeadingRows: true)
    {
    }

    internal ScrollCaptureComposer(bool detectStationaryLeadingRows)
    {
        _detectStationaryLeadingRows = detectStationaryLeadingRows;
    }

    internal int CurrentFrameTop => _currentFrameTop;

    internal int CapturedContentTop => _capturedContentTop;

    internal int CapturedContentBottom => _capturedContentBottom;

    internal void MarkBoundaryReached(ScrollCaptureDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

        var boundaryFingerprint = ViewportFingerprint.Create(boundaryFrame);
        var markerFingerprint = ViewportFingerprint.Create(frame);
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

        if (_currentFrame is null)
        {
            Bitmap? initialSegment = null;
            Bitmap? initialCurrentFrame = null;
            Bitmap? initialTopBoundaryFrame = null;
            Bitmap? initialBottomBoundaryFrame = null;

            try
            {
                initialSegment = (Bitmap)frame.Clone();
                initialCurrentFrame = (Bitmap)frame.Clone();
                initialTopBoundaryFrame = (Bitmap)frame.Clone();
                initialBottomBoundaryFrame = (Bitmap)frame.Clone();
                _segments.AddLast(initialSegment);
                AddSegmentAnchorHashes(initialSegment);
                _currentFrame = initialCurrentFrame;
                _currentFrameTop = 0;
                _capturedContentTop = 0;
                _capturedContentBottom = frame.Height;
                _initialFrameHeight = frame.Height;
                _topBoundaryFrame = initialTopBoundaryFrame;
                _topBoundaryFrameTop = 0;
                initialTopBoundaryFrame = null;
                _bottomBoundaryFrame = initialBottomBoundaryFrame;
                _bottomBoundaryFrameTop = 0;
                initialBottomBoundaryFrame = null;
                RememberViewport(
                    ViewportFingerprint.Create(frame),
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

        var fingerprint = ViewportFingerprint.Create(frame);

        // At a scroll boundary Windows still emits wheel input, while the
        // viewport stays at the same document position. Detect that exact
        // anchor before searching repetitive code rows for a non-zero shift.
        // This is deliberately stricter than matching any historical viewport:
        // only the current absolute anchor can classify the frame as stationary.
        if (_viewportHistory.Any(anchor =>
                anchor.FrameTop == _currentFrameTop &&
                anchor.Fingerprint.IsStationaryComparedTo(fingerprint)))
        {
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

        // A predicted return walk may cross a captured edge. The wheel only
        // tells us which stored boundary to compare; pixels must still verify
        // the displacement before any new rows are written.
        var boundaryCrossingCandidate = TryCreateWheelBoundaryCrossingCandidate(
            frame,
            direction,
            expectedNewRows,
            options);
        var preferredCandidate = boundaryCrossingCandidate ??
            (expectedNewRows is { } freshExpectedRows
                ? FindAlignmentCandidate(
                      frame,
                      direction,
                      options,
                      freshExpectedRows,
                      preferredNeighborhoodOnly: true,
                      minimumConfidenceOverride: Math.Min(
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
                    preferredNewRows));

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
        if (candidate is not null)
        {
            _wheelReturnDirection = null;
        }

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

        if (candidate is null)
        {
            LastRejectReason = anchorVetoReason ?? "no-candidate";
            OnViewportLost(expectedNewRows, direction, frame.Height, options);
            overlapMatch = null;
            return false;
        }

        _wheelReturnDirection = null;

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

        if (_detectStationaryLeadingRows &&
            !candidate.IsBridged &&
            candidate.ReferenceTopOverride is null)
        {
            var detectedLeadingRows =
                ImageOverlapMatcher.FindStationaryLeadingRows(
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

        var referenceTop = candidate.ReferenceTopOverride ?? _currentFrameTop;
        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(referenceTop + newRows)
            : checked(referenceTop - newRows);
        var nextFrameBottom = checked(nextFrameTop + frame.Height);

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
                    var segmentSourceTop = Math.Clamp(
                        _capturedContentBottom - nextFrameTop,
                        0,
                        frame.Height);
                    newSegment = CreateSegment(
                        frame,
                        segmentSourceTop,
                        frame.Height - segmentSourceTop,
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
                        LastRejectReason = "already-captured-below";
                        newSegment.Dispose();
                        newSegment = null;
                        nextBoundaryFrame.Dispose();
                        nextBoundaryFrame = null;
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

                    if (IsAlreadyCapturedSegment(newSegment))
                    {
                        // Same as the downward branch: skip the duplicate
                        // strip but keep the anchor tracking the viewport so
                        // boundary expansion can never stall on a hash
                        // false positive.
                        LastRejectReason = "already-captured-above";
                        newSegment.Dispose();
                        newSegment = null;
                        nextBoundaryFrame.Dispose();
                        nextBoundaryFrame = null;
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
        var match = ImageOverlapMatcher.FindVerticalOverlap(
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
            ? ImageOverlapMatcher.FindVerticalOverlap(
                anchor.Frame,
                frame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                expectedRows,
                preferredNeighborhoodOnly: true)
            : ImageOverlapMatcher.FindVerticalOverlap(
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

        if (candidate.ReferenceTopOverride is not null)
        {
            // A re-anchored candidate was matched directly against a boundary
            // frame — the absolute evidence this verification exists to
            // consult — so there is no drift to detect.
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
        var expectedOverlapRows = frame.Height - preferredBoundaryNewRows;
        if (preferredBoundaryNewRows < options.MinimumNewRows ||
            expectedOverlapRows < options.MinimumOverlapRows)
        {
            // A jump larger than one viewport cannot be verified safely. Drop
            // this transitional frame and wait for the next captured viewport.
            LastRejectReason = "boundary-unverifiable";
            return false;
        }

        var boundaryMatch = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            options.MinimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows,
            preferredBoundaryNewRows,
            preferredNeighborhoodOnly: true);
        if (boundaryMatch is null && wheelBoundaryNewRows is not null)
        {
            // The wheel prediction can be approximate during a fling. Search
            // globally once at the actual crossing rather than accepting the
            // first periodic table-row peak or repeating global work on every
            // frame while returning through captured content.
            boundaryMatch = ImageOverlapMatcher.FindVerticalOverlap(
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
            // boundary frame, and on sparse content that strip can carry too
            // little texture to verify either way. No verdict is not evidence
            // of a mismatch: with a decisive primary match the crossing must
            // proceed, because rejecting it stalls every following frame at
            // the boundary forever.
            var wheelContradictsCandidate = wheelBoundaryNewRows is { } predicted &&
                Math.Abs(predicted - candidateBoundaryNewRows) >
                    Math.Max(24, predicted / 4);
            if (!wheelContradictsCandidate &&
                candidate.Match.Confidence >= DecisiveMatchConfidence - 0.01)
            {
                return true;
            }

            // After a reverse walk the wheel estimate is often several frames
            // late. One global boundary probe is cheaper than stalling the
            // whole chain until a later sample happens to cross cleanly.
            boundaryMatch = ImageOverlapMatcher.FindVerticalOverlap(
                previousFrame,
                currentFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
                options.MinimumNewRows,
                preferredBoundaryNewRows,
                preferredNeighborhoodOnly: false);
            if (boundaryMatch is null)
            {
                LastRejectReason = "boundary-no-match";
                return false;
            }
        }

        var verifiedNewRows = frame.Height - boundaryMatch.OverlapRows;
        var driftRows = verifiedNewRows - candidateBoundaryNewRows;
        LastBoundaryDriftRows = driftRows;
        LastBoundaryConfidence = boundaryMatch.Confidence;

        if (Math.Abs(driftRows) <= AlignmentToleranceRows)
        {
            return true;
        }

        var agreesWithWheel = wheelBoundaryNewRows is { } predictedBoundaryRows &&
            Math.Abs(verifiedNewRows - predictedBoundaryRows) <=
                Math.Max(24, predictedBoundaryRows / 4);
        var agreesWithCandidate = Math.Abs(driftRows) <=
            Math.Max(BoundaryReanchorMaximumRows, candidateBoundaryNewRows / 2);
        if (boundaryMatch.Confidence < DecisiveMatchConfidence - 0.015 &&
            !agreesWithWheel)
        {
            // Not decisive enough to move the anchor, or so far away it is
            // more likely a periodic repetition than accumulated drift.
            return false;
        }

        if (Math.Abs(driftRows) > BoundaryReanchorMaximumRows &&
            !agreesWithWheel &&
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
        var fingerprint = ViewportFingerprint.Create(frame);
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

        var boundaryMatch = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            options.MinimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows,
            predictedBeyondBoundary,
            preferredNeighborhoodOnly: true);
        if (boundaryMatch is null)
        {
            boundaryMatch = ImageOverlapMatcher.FindVerticalOverlap(
                previousFrame,
                currentFrame,
                options.MinimumOverlapRows,
                options.MinimumOverlapConfidence,
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
            var downMatch = ImageOverlapMatcher.FindVerticalOverlap(
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
            var upMatch = ImageOverlapMatcher.FindVerticalOverlap(
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
        // Weak or short travel estimates cannot adjudicate; let image
        // evidence stand on its own there.
        if (Math.Abs(_unlocatedNetTravelRows) < frame.Height / 2)
        {
            return true;
        }

        var newRows = frame.Height - candidate.Match.OverlapRows;
        var proposedTop = candidate.Direction == ScrollCaptureDirection.Down
            ? referenceTop + newRows
            : referenceTop - newRows;
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
        ViewportFingerprint fingerprint,
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
                ? ImageOverlapMatcher.FindVerticalOverlap(
                    reference,
                    frame,
                    options.MinimumOverlapRows,
                    options.MinimumOverlapConfidence,
                    options.MinimumNewRows,
                    expectedRows,
                    preferredNeighborhoodOnly: true)
                : ImageOverlapMatcher.FindVerticalOverlap(
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

    private void RememberViewport(ViewportFingerprint fingerprint, int frameTop)
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

        // ImageOverlapMatcher reports previous[x] ~ current[x + horizontalOffset].
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
        ViewportFingerprint Fingerprint,
        int FrameTop);

    private sealed record RecentViewport(Bitmap Frame, int FrameTop);
}
