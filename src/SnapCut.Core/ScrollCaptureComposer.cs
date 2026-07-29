namespace SnapCut.Core;

public sealed class ScrollCaptureComposer : IDisposable
{
    private const int MaximumViewportHistory = 128;
    private const int MaximumRecentViewports = 4;
    // Wheel direction is the primary motion signal. Repetitive code/chat rows can
    // yield a slightly higher false score in the opposite direction; switching on
    // an 0.8% difference appended reverse-scroll content to the bottom. Keep the
    // opposite fallback for stale inertia events, but require decisive evidence.
    private const double OppositeDirectionConfidenceAdvantage = 0.03;
    private const int AlignmentToleranceRows = 3;
    // Largest positional drift a decisive boundary match may correct. Drift
    // accumulates in small mis-steps, while a periodic false peak sits at
    // least one content period away — far beyond this.
    private const int BoundaryReanchorMaximumRows = 64;
    // An image match this strong is direct evidence of where the viewport went.
    // The wheel estimate is only a prior, so it must not be allowed to veto it.
    private const double DecisiveMatchConfidence = 0.99;
    private readonly LinkedList<PixelImage> _segments = [];
    private readonly Dictionary<ulong, int> _segmentAnchorHashCounts = [];
    private readonly LinkedList<ViewportAnchor> _viewportHistory = [];
    private readonly LinkedList<RecentViewport> _recentViewports = [];
    private PixelImage? _currentFrame;
    private PixelImage? _topBoundaryFrame;
    private int _topBoundaryFrameTop;
    private PixelImage? _bottomBoundaryFrame;
    private int _bottomBoundaryFrameTop;
    // These logical coordinates let a reverse scroll move through content that is
    // already in _segments without adding it to the output a second time.
    private int _currentFrameTop;
    private int _capturedContentTop;
    private int _capturedContentBottom;
    private int? _lastSuccessfulNewRows;
    private ScrollCaptureDirection? _lastMatchedDirection;
    private PixelImage? _compositeCache;
    private int _compositeContentTop;
    private int _compositeUsedHeight;
    private PixelImage? _previewStripCache;
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

    public bool TryAddFrame(
        PixelImage frame,
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
        PixelImage frame,
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
        PixelImage frame,
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

        if (_currentFrame is null)
        {
            var initialSegment = frame.Clone();
            _segments.AddLast(initialSegment);
            AddSegmentAnchorHashes(initialSegment);
            _currentFrame = frame.Clone();
            _currentFrameTop = 0;
            _capturedContentTop = 0;
            _capturedContentBottom = frame.Height;
            _topBoundaryFrame = frame.Clone();
            _topBoundaryFrameTop = 0;
            _bottomBoundaryFrame = frame.Clone();
            _bottomBoundaryFrameTop = 0;
            RememberViewport(
                ViewportFingerprint.Create(frame),
                _currentFrameTop);
            RememberRecentViewport(frame, _currentFrameTop);
            /* cache built lazily */
            overlapMatch = null;
            LastFrameMovementRows = 0;
            return true;
        }

        if (_currentFrame.Width != frame.Width || _currentFrame.Height != frame.Height)
        {
            LastRejectReason = "size-mismatch";
            overlapMatch = null;
            return false;
        }

        var fingerprint = ViewportFingerprint.Create(frame);

        // At a scroll boundary the OS still emits wheel input, while the
        // viewport stays at the same document position. Detect that exact
        // anchor before searching repetitive code rows for a non-zero shift.
        // This is deliberately stricter than matching any historical viewport:
        // only the current absolute anchor can classify the frame as stationary.
        if (_viewportHistory.Any(anchor =>
                anchor.FrameTop == _currentFrameTop &&
                anchor.Fingerprint.IsStationaryComparedTo(fingerprint)))
        {
            overlapMatch = null;
            LastFrameMovementRows = 0;
            return false;
        }

        // A returned viewport is stronger evidence than a wheel direction. It
        // lets the user reverse through captured content without repeatedly
        // adding the same strips at either boundary.
        if (TryFindKnownViewport(fingerprint, direction, out var knownTop))
        {
            LastFrameMovementRows = Math.Abs(knownTop - _currentFrameTop);
            ReplaceCurrentFrame(frame, knownTop);
            RememberRecentViewport(frame, knownTop);
            _lastMatchedDirection = direction;
            overlapMatch = null;
            return false;
        }

        var preferredNewRows = expectedNewRows ?? _lastSuccessfulNewRows;
        var preferredCandidate = FindAlignmentCandidate(
            frame,
            direction,
            options,
            preferredNewRows);
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
        var candidate = OrderAlignmentCandidates(
                preferredCandidate,
                alternateCandidate)
            .FirstOrDefault(candidate =>
                IsAlignmentCandidateConsistent(frame, candidate, options));

        if (candidate is null)
        {
            LastRejectReason = "no-candidate";
            overlapMatch = null;
            return false;
        }

        overlapMatch = candidate.Match;
        var newRows = frame.Height - candidate.Match.OverlapRows;

        if (newRows < options.MinimumNewRows)
        {
            LastRejectReason = "below-minimum";
            return false;
        }

        if (expectedNewRows is { } expected)
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
                candidate.Match.Confidence < DecisiveMatchConfidence)
            {
                LastRejectReason = "expected-veto";
                overlapMatch = null;
                return false;
            }
        }

        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(_currentFrameTop + newRows)
            : checked(_currentFrameTop - newRows);
        var nextFrameBottom = checked(nextFrameTop + frame.Height);

        if (!TryResolveBoundaryExpansion(
                frame,
                candidate,
                ref nextFrameTop,
                ref nextFrameBottom,
                options))
        {
            LastRejectReason ??= "boundary-inconsistent";
            overlapMatch = null;
            return false;
        }

        var expandedCapturedRange = false;

        if (candidate.Direction == ScrollCaptureDirection.Down)
        {
            if (nextFrameBottom > _capturedContentBottom)
            {
                var segmentSourceTop = Math.Clamp(
                    _capturedContentBottom - nextFrameTop,
                    0,
                    frame.Height);
                var newSegment = CreateSegment(
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
                }
                else
                {
                    _segments.AddLast(newSegment);
                    AddSegmentAnchorHashes(newSegment);
                    AppendSegmentToCompositeCache(newSegment);
                    AppendSegmentToPreviewStripCache(newSegment);
                    _capturedContentBottom = nextFrameBottom;
                    _bottomBoundaryFrame = frame.Clone();
                    _bottomBoundaryFrameTop = nextFrameTop;
                    AddedBelowFrameCount++;
                    expandedCapturedRange = true;
                }
            }
        }
        else
        {
            if (nextFrameTop < _capturedContentTop)
            {
                var segmentHeight = Math.Clamp(
                    _capturedContentTop - nextFrameTop,
                    0,
                    frame.Height);
                var newSegment = CreateSegment(
                    frame,
                    0,
                    segmentHeight,
                    -candidate.Match.HorizontalOffset);

                if (IsAlreadyCapturedSegment(newSegment))
                {
                    // Same as the downward branch: skip the duplicate
                    // strip but keep the anchor tracking the viewport so
                    // boundary expansion can never stall on a hash
                    // false positive.
                    LastRejectReason = "already-captured-above";
                }
                else
                {
                    _segments.AddFirst(newSegment);
                    AddSegmentAnchorHashes(newSegment);
                    _capturedContentTop = nextFrameTop;
                    _topBoundaryFrame = frame.Clone();
                    _topBoundaryFrameTop = nextFrameTop;
                    AddedAboveFrameCount++;
                    expandedCapturedRange = true;
                    PrependSegmentToCompositeCache(_segments.First!.Value);
                    PrependSegmentToPreviewStripCache(_segments.First!.Value);
                }
            }
        }

        _currentFrame = frame.Clone();
        _currentFrameTop = nextFrameTop;
        RememberViewport(fingerprint, nextFrameTop);
        RememberRecentViewport(frame, nextFrameTop);

        // Track displacement even when the viewport only walks through already
        // captured content so the next boundary expansion keeps temporal glue.
        _lastSuccessfulNewRows = newRows;
        _lastMatchedDirection = candidate.Direction;
        LastFrameMovementRows = newRows;

        return expandedCapturedRange;
    }

    public PixelImage Compose()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("没有可拼接的长截图帧。");
        }

        EnsureCompositeCache();
        return _compositeCache!.CropRows(
            _compositeContentTop,
            _compositeUsedHeight);
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
        var composite = new PixelImage(width, capacity);
        var y = headroom;

        foreach (var segment in _segments)
        {
            segment.BlitRowsTo(composite, 0, segment.Height, y);
            y += segment.Height;
        }

        _compositeCache = composite;
        _compositeContentTop = headroom;
        _compositeUsedHeight = height;
    }

    private void AppendSegmentToCompositeCache(PixelImage segment)
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
            var expanded = new PixelImage(width, capacity);

            if (_compositeUsedHeight > 0)
            {
                _compositeCache.BlitRowsTo(
                    expanded,
                    _compositeContentTop,
                    _compositeUsedHeight,
                    _compositeContentTop);
            }

            _compositeCache = expanded;
        }

        segment.BlitRowsTo(
            _compositeCache,
            0,
            segment.Height,
            _compositeContentTop + _compositeUsedHeight);
        _compositeUsedHeight = checked(_compositeUsedHeight + segment.Height);
    }

    private void PrependSegmentToCompositeCache(PixelImage segment)
    {
        if (_compositeCache is null)
        {
            return;
        }

        if (_compositeContentTop >= segment.Height)
        {
            _compositeContentTop -= segment.Height;
            segment.BlitRowsTo(
                _compositeCache,
                0,
                segment.Height,
                _compositeContentTop);
            _compositeUsedHeight = checked(
                _compositeUsedHeight + segment.Height);
            return;
        }

        var width = OutputWidth;
        var nextUsedHeight = checked(_compositeUsedHeight + segment.Height);
        var headroom = Math.Min(512, Math.Max(64, nextUsedHeight / 8));
        var tailroom = Math.Min(512, Math.Max(64, nextUsedHeight / 8));
        var expanded = new PixelImage(
            width,
            checked(headroom + nextUsedHeight + tailroom));

        segment.BlitRowsTo(expanded, 0, segment.Height, headroom);
        _compositeCache.BlitRowsTo(
            expanded,
            _compositeContentTop,
            _compositeUsedHeight,
            headroom + segment.Height);
        _compositeCache = expanded;
        _compositeContentTop = headroom;
        _compositeUsedHeight = nextUsedHeight;
    }

    private void InvalidateCompositeCache()
    {
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
        var previewStrip = new PixelImage(targetWidth, capacity);
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

            var scaledSegment = segment.DownscaleTo(targetWidth, segmentHeight);
            scaledSegment.BlitRowsTo(previewStrip, 0, segmentHeight, y);
            y += segmentHeight;
        }

        _previewStripCache = previewStrip;
        _previewStripContentTop = headroom;
        _previewStripWidth = targetWidth;
        _previewStripUsedHeight = y - headroom;
        _previewStripSourceHeight = OutputHeight;
    }

    private void AppendSegmentToPreviewStripCache(PixelImage segment)
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
            var expanded = new PixelImage(_previewStripWidth, capacity);

            _previewStripCache.BlitRowsTo(
                expanded,
                _previewStripContentTop,
                _previewStripUsedHeight,
                _previewStripContentTop);
            _previewStripCache = expanded;
        }

        var scaledSegment = segment.DownscaleTo(_previewStripWidth, segmentHeight);
        scaledSegment.BlitRowsTo(
            _previewStripCache,
            0,
            segmentHeight,
            _previewStripContentTop + _previewStripUsedHeight);
        _previewStripUsedHeight = requiredHeight;
    }

    private void PrependSegmentToPreviewStripCache(PixelImage segment)
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
            var scaledPrefix = segment.DownscaleTo(
                _previewStripWidth,
                segmentHeight);
            scaledPrefix.BlitRowsTo(
                _previewStripCache,
                0,
                segmentHeight,
                _previewStripContentTop);
            _previewStripUsedHeight = nextUsedHeight;
            return;
        }

        var headroom = Math.Min(256, Math.Max(32, nextUsedHeight / 8));
        var expanded = new PixelImage(
            _previewStripWidth,
            checked(
                headroom + nextUsedHeight +
                Math.Min(256, Math.Max(32, nextUsedHeight / 8))));

        var scaledSegment = segment.DownscaleTo(_previewStripWidth, segmentHeight);
        scaledSegment.BlitRowsTo(expanded, 0, segmentHeight, headroom);
        _previewStripCache.BlitRowsTo(
            expanded,
            _previewStripContentTop,
            _previewStripUsedHeight,
            headroom + segmentHeight);
        _previewStripCache = expanded;
        _previewStripContentTop = headroom;
        _previewStripUsedHeight = nextUsedHeight;
    }

    private void InvalidatePreviewStripCache()
    {
        _previewStripCache = null;
        _previewStripContentTop = 0;
        _previewStripUsedHeight = 0;
        _previewStripWidth = 0;
        _previewStripSourceHeight = 0;
    }

    public PixelImage ComposePreview(int maximumWidth, int maximumHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);

        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("没有可预览的滚动截图帧。");
        }

        var composite = Compose();
        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)OutputWidth,
                maximumHeight / (double)OutputHeight));
        var previewWidth = Math.Max(1, (int)Math.Round(OutputWidth * scale));
        var previewHeight = Math.Max(1, (int)Math.Round(OutputHeight * scale));

        return previewWidth == composite.Width &&
               previewHeight == composite.Height
            ? composite
            : composite.DownscaleTo(previewWidth, previewHeight);
    }

    public PixelImage ComposeLivePreview(int maximumWidth, int maximumHeight)
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
        var stripHeight = Math.Max(1, _previewStripUsedHeight);
        var strip = _previewStripCache!.CropRows(
            _previewStripContentTop,
            stripHeight);

        if (strip.Height <= maximumHeight)
        {
            return strip;
        }

        var scale = maximumHeight / (double)strip.Height;
        var scaledWidth = Math.Max(1, (int)Math.Round(strip.Width * scale));
        return strip.DownscaleTo(scaledWidth, maximumHeight);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _segments.Clear();
        _segmentAnchorHashCounts.Clear();
        _viewportHistory.Clear();
        _recentViewports.Clear();
        _currentFrame = null;
        _topBoundaryFrame = null;
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
                Math.Min(96, frameHeight / 12))
            : options.MinimumOverlapRows;
    }

    private AlignmentCandidate? FindAlignmentCandidate(
        PixelImage frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? preferredNewRows,
        bool preferredNeighborhoodOnly = false)
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
            options.MinimumOverlapConfidence,
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
        PixelImage frame,
        AlignmentCandidate candidate,
        ScrollCaptureOptions options)
    {
        var newRows = frame.Height - candidate.Match.OverlapRows;

        if (newRows < options.MinimumNewRows)
        {
            return false;
        }

        var nextFrameTop = candidate.Direction == ScrollCaptureDirection.Down
            ? checked(_currentFrameTop + newRows)
            : checked(_currentFrameTop - newRows);
        var nextFrameBottom = checked(nextFrameTop + frame.Height);
        var remainsInsideCapturedRange =
            nextFrameTop >= _capturedContentTop &&
            nextFrameBottom <= _capturedContentBottom;
        if (!remainsInsideCapturedRange)
        {
            // The current frame is already the temporal anchor for ordinary
            // boundary expansion. Re-matching every recent viewport here made
            // one input frame perform up to five full image searches.
            return true;
        }

        var minimumVerificationOverlap = Math.Max(
            options.MinimumOverlapRows * 2,
            Math.Min(48, frame.Height / 4));
        var verificationViewport = _recentViewports
            .Where(viewport => viewport.FrameTop != _currentFrameTop)
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
            return true;
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
        PixelImage frame,
        AlignmentCandidate candidate,
        ref int nextFrameTop,
        ref int nextFrameBottom,
        ScrollCaptureOptions options)
    {
        PixelImage previousFrame;
        PixelImage currentFrame;
        int expectedNewRows;

        if (candidate.Direction == ScrollCaptureDirection.Up &&
            _currentFrameTop > _capturedContentTop &&
            nextFrameTop < _capturedContentTop &&
            _topBoundaryFrame is not null)
        {
            previousFrame = frame;
            currentFrame = _topBoundaryFrame;
            expectedNewRows = _topBoundaryFrameTop - nextFrameTop;
        }
        else if (candidate.Direction == ScrollCaptureDirection.Down &&
                 _currentFrameTop + frame.Height < _capturedContentBottom &&
                 nextFrameBottom > _capturedContentBottom &&
                 _bottomBoundaryFrame is not null)
        {
            previousFrame = _bottomBoundaryFrame;
            currentFrame = frame;
            expectedNewRows = nextFrameTop - _bottomBoundaryFrameTop;
        }
        else
        {
            return true;
        }

        var expectedOverlapRows = frame.Height - expectedNewRows;
        if (expectedNewRows < options.MinimumNewRows ||
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
            expectedNewRows,
            preferredNeighborhoodOnly: true);
        if (boundaryMatch is null)
        {
            // A fast crossing leaves only a narrow strip shared with the
            // boundary frame, and on sparse content that strip can carry too
            // little texture to verify either way. No verdict is not evidence
            // of a mismatch: with a decisive primary match the crossing must
            // proceed, because rejecting it stalls every following frame at
            // the boundary forever.
            if (candidate.Match.Confidence >= DecisiveMatchConfidence)
            {
                return true;
            }

            LastRejectReason = "boundary-no-match";
            return false;
        }

        var verifiedNewRows = frame.Height - boundaryMatch.OverlapRows;
        var driftRows = verifiedNewRows - expectedNewRows;
        LastBoundaryDriftRows = driftRows;
        LastBoundaryConfidence = boundaryMatch.Confidence;

        if (Math.Abs(driftRows) <= AlignmentToleranceRows)
        {
            return true;
        }

        if (boundaryMatch.Confidence < DecisiveMatchConfidence ||
            Math.Abs(driftRows) > BoundaryReanchorMaximumRows)
        {
            // Not decisive enough to move the anchor, or so far away it is
            // more likely a periodic repetition than accumulated drift.
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

    private bool TryFindKnownViewport(
        ViewportFingerprint fingerprint,
        ScrollCaptureDirection direction,
        out int frameTop)
    {
        var matchingAnchors = _viewportHistory
            .Where(anchor => anchor.Fingerprint.IsSimilarTo(fingerprint))
            .ToArray();

        if (matchingAnchors.Length == 0)
        {
            frameTop = default;
            return false;
        }

        // Only recognize anchors that lie strictly further in the requested
        // scroll direction. Including FrameTop == _currentFrameTop (or falling
        // back to every similar fingerprint) permanently blocks expansion at
        // the capture boundary: at the top, scrolling further up keeps matching
        // the initial viewport and never prepends new content.
        const int minimumStep = 1;
        var candidatesInRequestedDirection = (direction == ScrollCaptureDirection.Down
            ? matchingAnchors.Where(anchor =>
                anchor.FrameTop >= _currentFrameTop + minimumStep)
            : matchingAnchors.Where(anchor =>
                anchor.FrameTop <= _currentFrameTop - minimumStep))
            .ToArray();

        if (candidatesInRequestedDirection.Length == 0)
        {
            // No known viewport further in this direction — let alignment expand
            // past the captured boundary (or re-match mid-content by pixels).
            frameTop = default;
            return false;
        }

        var closestAnchor = candidatesInRequestedDirection
            .OrderBy(anchor => Math.Abs(anchor.FrameTop - _currentFrameTop))
            .First();

        frameTop = closestAnchor.FrameTop;
        return true;
    }

    private void ReplaceCurrentFrame(PixelImage frame, int frameTop)
    {
        _currentFrame = frame.Clone();
        _currentFrameTop = frameTop;
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

    private void RememberRecentViewport(PixelImage frame, int frameTop)
    {
        var existingViewport = _recentViewports.LastOrDefault(
            viewport => viewport.FrameTop == frameTop);

        if (existingViewport is not null)
        {
            _recentViewports.Remove(existingViewport);
        }

        _recentViewports.AddLast(new RecentViewport(frame.Clone(), frameTop));

        while (_recentViewports.Count > MaximumRecentViewports)
        {
            _recentViewports.RemoveFirst();
        }
    }

    private static PixelImage CreateSegment(
        PixelImage frame,
        int sourceTop,
        int height,
        int horizontalOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (horizontalOffset == 0)
        {
            return frame.CropRows(sourceTop, height);
        }

        // ImageOverlapMatcher reports previous[x] ~ current[x + horizontalOffset].
        // Remap the extracted strip into the composite's x basis so a 1px
        // compositor drift does not leave a jagged vertical seam at the stitch
        // boundary. BlitRowsTo maps destination[x] = source[x - offset] and
        // clamps at the edges, which reproduces the nearest-column fill the
        // shifted copy exposes.
        var sourceX = Math.Clamp(
            horizontalOffset,
            -frame.Width + 1,
            frame.Width - 1);
        var segment = new PixelImage(frame.Width, height);
        frame.BlitRowsTo(segment, sourceTop, height, 0, -sourceX);
        return segment;
    }

    private bool IsAlreadyCapturedSegment(PixelImage segment)
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

    private void AddSegmentAnchorHashes(PixelImage segment)
    {
        var bandWidth = segment.Width >= 80
            ? segment.Width - Math.Clamp(segment.Width / 80, 10, 24)
            : segment.Width;
        var bandHeight = Math.Min(64, segment.Height);
        var stride = segment.Stride;
        var buffer = segment.Pixels;
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
                bytesPerPixel: 4,
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

    private static ulong ComputeLeadingBandHash(PixelImage image)
    {
        var bandWidth = image.Width >= 80
            ? image.Width - Math.Clamp(image.Width / 80, 10, 24)
            : image.Width;
        var bandHeight = Math.Min(64, image.Height);
        var stride = image.Stride;
        var buffer = image.Pixels;

        var hash = 1469598103934665603UL;
        byte minimumLuma = byte.MaxValue;
        byte maximumLuma = byte.MinValue;
        var stepX = Math.Max(1, bandWidth / 96);
        for (var y = 0; y < bandHeight; y++)
        {
            var rowHash = ComputeRowHash(
                buffer,
                y * stride,
                bytesPerPixel: 4,
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

    private sealed record AlignmentCandidate(
        ScrollCaptureDirection Direction,
        ImageOverlapMatch Match);

    private sealed record ViewportAnchor(
        ViewportFingerprint Fingerprint,
        int FrameTop);

    private sealed record RecentViewport(PixelImage Frame, int FrameTop);
}
