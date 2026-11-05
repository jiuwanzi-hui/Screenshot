using System.Drawing;

namespace Screenshot.App.Capture;

public sealed partial class ScrollCaptureComposer
{
    private readonly bool _verifiedTracking;
    private ManualScrollFrame? _manualCurrent;
    private ManualScrollFrame? _manualTop;
    private ManualScrollFrame? _manualBottom;
    private ScrollCaptureFixedRegions? _manualFixedRegions;
    private readonly List<(ManualScrollFrame Frame, int Top)> _manualRecoveryAnchors = [];
    private const long ManualRecoveryBudgetBytes = 32L * 1024 * 1024;
    private bool _manualHasEstimatedBlankSpacing;
    private ScrollCapturePosition? _manualContextOrigin;
    internal bool IsCrossingManualBlank
    {
        get { lock (_sync) return _manualContextOrigin is null && _manualCurrent?.IsBlank == true; }
    }

    internal static ScrollCaptureComposer CreateForManualCapture() =>
        new(detectStationaryLeadingRows: false, fixedBottomRows: 0, verifiedTracking: true);

    private bool TryAddVerifiedManualFrame(Bitmap frame, ScrollCaptureDirection direction,
        ScrollCaptureOptions options, int? expectedRows, out ImageOverlapMatch? overlapMatch)
    {
        using var content = _manualFixedRegions?.CropContent(frame);
        frame = content ?? frame;
        ManualScrollFrame? sample = new(frame);
        try
        {
            var minimumOverlap = Math.Max(options.MinimumOverlapRows, Math.Min(48, frame.Height / 6));
            if (_manualFixedRegions is null && _segments.Count == 1 &&
                _manualCurrent!.FindFixedRegions(sample, minimumOverlap) is { } fixedRows)
            {
                _manualFixedRegions = new ScrollCaptureFixedRegions(_currentFrame!, fixedRows.Top, fixedRows.Bottom);
                using var initialContent = _manualFixedRegions.CropContent(_currentFrame!);
                var initial = _segments.First!;
                initial.Value.Dispose();
                initial.Value = (Bitmap)initialContent.Clone();
                _initialFrameHeight = _capturedContentBottom = initialContent.Height;
                DisposeManualFrames();
                SetManualFrame(new ManualScrollFrame(initialContent), atTop: true, atBottom: true);
                InvalidateCompositeCache();
                InvalidatePreviewStripCache();
                // Re-enter once with a consistent body coordinate system for
                // the initial frame, both boundaries and the incoming frame.
                return TryAddVerifiedManualFrame(frame, direction, options, expectedRows, out overlapMatch);
            }
            var contextPosition = ScrollCapturePosition.Read(frame);
            var hasContext = contextPosition is not null && _manualContextOrigin is not null;
            var current = _manualCurrent!;
            ManualScrollFrame.Match? match = hasContext || current.IsBlank || sample.IsBlank
                ? null : current.Locate(sample, minimumOverlap);
            var referenceTop = _currentFrameTop;
            if (hasContext)
            {
                if (contextPosition!.Rows is not { } position || _manualContextOrigin!.Rows is not { } origin ||
                    !ReferenceEquals(contextPosition.Session, _manualContextOrigin.Session))
                {
                    LastRejectReason = "manual-context-unverified";
                    overlapMatch = null;
                    return false;
                }
                var shift = position - origin - _currentFrameTop;
                if (!current.IsConsistentAtShift(sample, shift))
                {
                    LastRejectReason = "manual-context-overlap-disagreement";
                    overlapMatch = null;
                    return false;
                }
                match = new ManualScrollFrame.Match(shift, 1);
            }
            if (match is null && !sample.IsBlank)
            {
                // A repaint can obscure one temporal sample. Reconnect to a
                // saved pixel boundary; never integrate wheel estimates into
                // document coordinates or search a fingerprint with a guessed position.
                foreach (var (reference, top) in new[]
                         { (_manualTop, _topBoundaryFrameTop), (_manualBottom, _bottomBoundaryFrameTop) })
                {
                    if (reference == _manualCurrent) continue;
                    var recovered = reference!.Locate(sample, minimumOverlap);
                    if (recovered is null) continue;
                    // A distant saved boundary can contain the same avatars
                    // or quoted messages. It is not permission to teleport the
                    // active viewport there after one failed animated frame.
                    // Travel inside a blank viewport is tentative. A verified
                    // old edge can correct it after extra wheel turns at the
                    // physical boundary, before those turns duplicate content.
                    if (!current.IsBlank &&
                        Math.Abs(top + recovered.Value.Shift - _currentFrameTop) > frame.Height - minimumOverlap)
                        continue;
                    if (match is not null && referenceTop + match.Value.Shift != top + recovered.Value.Shift)
                    {
                        LastRejectReason = "manual-ambiguous-recovery";
                        overlapMatch = null;
                        return false;
                    }
                    match = recovered;
                    referenceTop = top;
                }
            }
            if (match is null && !sample.IsBlank)
            {
                // A reverse fling can skip the current viewport while landing
                // wholly inside pixels already captured. Top/bottom alone do
                // not cover the middle of a long document. Restore only a
                // strongly verified, unambiguous old position; never add a
                // strip or fill a missing interval from a wheel estimate.
                foreach (var anchor in _manualRecoveryAnchors)
                {
                    if (anchor.Frame == _manualCurrent || anchor.Frame == _manualTop || anchor.Frame == _manualBottom)
                        continue;
                    var recovered = anchor.Frame.Locate(sample, Math.Max(minimumOverlap, frame.Height * 3 / 4));
                    if (recovered is null || recovered.Value.Confidence < 0.98) continue;
                    var recoveredTop = anchor.Top + recovered.Value.Shift;
                    if (recoveredTop < _capturedContentTop || recoveredTop + frame.Height > _capturedContentBottom)
                        continue;
                    if (match is not null && referenceTop + match.Value.Shift != recoveredTop)
                    {
                        LastRejectReason = "manual-ambiguous-recovery";
                        overlapMatch = null;
                        return false;
                    }
                    match = recovered;
                    referenceTop = anchor.Top;
                }
            }
            var crossedBlank = false;
            if (match is null)
            {
                match = current.LocateAcrossBlank(sample, direction, minimumOverlap, expectedRows);
                referenceTop = _currentFrameTop;
                crossedBlank = match is { Shift: not 0 };
            }
            if (match is null)
            {
                LastRejectReason = "manual-unverified-frame";
                overlapMatch = null;
                return false;
            }

            var nextTop = checked(referenceTop + match.Value.Shift);
            var nextBottom = checked(nextTop + frame.Height);
            var addedAbove = Math.Max(0, _capturedContentTop - nextTop);
            var addedBelow = Math.Max(0, nextBottom - _capturedContentBottom);
            if (sample.IsBlank && !hasContext)
            {
                // Repeated blank frames are indistinguishable from a page
                // boundary. Track tentative travel without allocating strips
                // or growing the result until visible content appears again.
                var travelLimit = checked(frame.Height * options.MaximumFrames);
                nextTop = Math.Clamp(nextTop, _capturedContentTop - travelLimit,
                    _capturedContentBottom + travelLimit);
                SetManualFrame(sample, atTop: false, atBottom: false);
                sample = null;
                LastFrameMovementRows = Math.Abs(nextTop - _currentFrameTop);
                LastFrameWasBridged = crossedBlank;
                _currentFrameTop = nextTop;
                LastRejectReason = null;
                overlapMatch = match is { } blankMatch
                    ? new ImageOverlapMatch(frame.Height - Math.Abs(blankMatch.Shift), 1) : null;
                return false;
            }
            // A disconnected viewport is not a new strip. Clamping a large
            // coordinate jump to one frame silently concealed missing rows.
            if (!crossedBlank &&
                (addedAbove > frame.Height - minimumOverlap || addedBelow > frame.Height - minimumOverlap))
            {
                LastRejectReason = "manual-disconnected-frame";
                overlapMatch = null;
                return false;
            }

            // Crossing an old edge during any return leg must agree with that
            // absolute edge, even if the current pair has an attractive periodic peak.
            var boundary = addedAbove > 0 ? _manualTop : addedBelow > 0 ? _manualBottom : null;
            var boundaryTop = addedAbove > 0 ? _topBoundaryFrameTop : _bottomBoundaryFrameTop;
            if (!hasContext && !crossedBlank && boundary is not null && boundary != _manualCurrent && referenceTop != boundaryTop)
            {
                var verified = boundary.Locate(sample, minimumOverlap);
                if (verified is null || boundaryTop + verified.Value.Shift != nextTop)
                {
                    LastRejectReason = "manual-boundary-disagreement";
                    overlapMatch = null;
                    return false;
                }
            }

            var movement = Math.Abs(nextTop - _currentFrameTop);
            if (addedAbove > 0 || addedBelow > 0)
            {
                var rows = Math.Max(addedAbove, addedBelow);
                var sourceTop = addedAbove > 0 ? 0 : frame.Height - rows;
                var segment = rows <= frame.Height
                    ? CreateSegment(frame, sourceTop, rows, horizontalOffset: 0)
                    : CreateBlankContinuation(frame, rows, addedAbove > 0, current.BlankColor);
                // Only interval subtraction decides which pixels are new.
                // Repeated text is legitimate content, never a reason to trim
                // previously captured rows or move the anchor without writing them.
                if (addedAbove > 0)
                {
                    _segments.AddFirst(segment);
                    _capturedContentTop = nextTop;
                    _topBoundaryFrameTop = nextTop;
                    AddedAboveFrameCount++;
                    PrependSegmentToCompositeCache(segment);
                    PrependSegmentToPreviewStripCache(segment);
                }
                else
                {
                    _segments.AddLast(segment);
                    _capturedContentBottom = nextBottom;
                    _bottomBoundaryFrameTop = nextTop;
                    AddedBelowFrameCount++;
                    AppendSegmentToCompositeCache(segment);
                    AppendSegmentToPreviewStripCache(segment);
                }
            }

            SetManualFrame(sample, addedAbove > 0, addedBelow > 0);
            sample = null;
            _currentFrameTop = nextTop;
            _currentFramePositionEstimated = false;
            LastFrameMovementRows = movement;
            LastFrameWasBridged = crossedBlank;
            _manualHasEstimatedBlankSpacing |= crossedBlank && (addedAbove > 0 || addedBelow > 0);
            _lastSuccessfulNewRows = movement > 0 ? movement : _lastSuccessfulNewRows;
            LastRejectReason = null;
            overlapMatch = new ImageOverlapMatch(frame.Height - Math.Abs(match.Value.Shift), match.Value.Confidence);
            if (ContentHeight != _capturedContentBottom - _capturedContentTop)
                throw new InvalidOperationException("手动长截图内容坐标与像素高度不一致。");
            return addedAbove > 0 || addedBelow > 0;
        }
        finally { sample?.Dispose(); }
    }

    private void SetManualFrame(ManualScrollFrame frame, bool atTop, bool atBottom)
    {
        if (_manualCurrent is { IsBlank: false } current && !_manualRecoveryAnchors.Any(anchor =>
                Math.Abs(anchor.Top - _currentFrameTop) < Math.Max(1, current.Height / 2)))
            _manualRecoveryAnchors.Add((current, _currentFrameTop));
        var previous = new[] { _manualCurrent, _manualTop, _manualBottom };
        _manualCurrent = frame;
        if (atTop) _manualTop = frame;
        if (atBottom) _manualBottom = frame;
        foreach (var old in previous.Distinct())
            if (old is not null && old != _manualCurrent && old != _manualTop && old != _manualBottom &&
                !_manualRecoveryAnchors.Any(anchor => anchor.Frame == old))
                old.Dispose();
        while (_manualRecoveryAnchors.Count > 32 ||
               _manualRecoveryAnchors.Sum(anchor => anchor.Frame.RetainedBytes) > ManualRecoveryBudgetBytes)
        {
            var old = _manualRecoveryAnchors[0].Frame;
            _manualRecoveryAnchors.RemoveAt(0);
            if (old != _manualCurrent && old != _manualTop && old != _manualBottom) old.Dispose();
        }
    }

    private static Bitmap CreateBlankContinuation(Bitmap frame, int rows, bool above, Color background)
    {
        var segment = new Bitmap(frame.Width, rows, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(segment);
            graphics.Clear(background);
            graphics.DrawImageUnscaled(frame, 0, above ? 0 : rows - frame.Height);
            return segment;
        }
        catch { segment.Dispose(); throw; }
    }

    private void DisposeManualFrames()
    {
        foreach (var frame in new[] { _manualCurrent, _manualTop, _manualBottom }
                     .Concat(_manualRecoveryAnchors.Select(anchor => anchor.Frame)).Distinct()) frame?.Dispose();
        _manualRecoveryAnchors.Clear();
        _manualCurrent = _manualTop = _manualBottom = null;
    }
}
