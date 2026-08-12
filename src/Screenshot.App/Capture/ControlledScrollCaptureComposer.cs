using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Screenshot.App.Capture;

/// <summary>
/// Keeps the downward and above-start portions in independent coordinate
/// systems. Both contain the exact initial viewport, which is the deterministic
/// join between them and prevents an upward strip from ever being appended to
/// the bottom of the downward result.
/// </summary>
internal sealed class ControlledScrollCaptureComposer : IDisposable
{
    private readonly AutomaticScrollCaptureComposerCore _downward = new(
        detectStationaryLeadingRows: false);
    private readonly Queue<int> _recentDownwardExpansionRows = new(3);
    private readonly Queue<int> _recentUpwardExpansionRows = new(3);
    private AutomaticScrollCaptureComposerCore? _upward;
    private int _initialFrameHeight;
    private bool _initialized;
    private bool _disposed;

    public int FrameCount => _downward.FrameCount +
        Math.Max(0, (_upward?.FrameCount ?? 1) - 1);

    public int AddedAboveFrameCount =>
        _upward?.AddedAboveFrameCount ?? 0;

    public int AddedBelowFrameCount => _downward.AddedBelowFrameCount;

    public int OutputWidth => _downward.OutputWidth;

    internal int FixedBottomRows => _downward.FixedBottomRows;

    internal int GetTravelFromInitial(
        ScrollCaptureDirection returnDirection)
    {
        EnsureInitialized();
        return returnDirection == ScrollCaptureDirection.Up
            ? Math.Max(0, _downward.CurrentFrameTop)
            : Math.Max(0, -(_upward?.CurrentFrameTop ?? 0));
    }

    public int OutputHeight => _downward.OutputHeight +
        Math.Max(0, (_upward?.OutputHeight ?? SharedInitialHeight) -
            SharedInitialHeight);

    public bool IsUpwardExtensionStarted => _upward is not null;

    public int? LastFrameMovementRows { get; private set; }

    public string? LastRejectReason { get; private set; }

    public int? LastOverlapRows { get; private set; }

    public double? LastOverlapConfidence { get; private set; }

    public int? LastHorizontalOffset { get; private set; }

    internal int? LastPreferredExpectedRows { get; private set; }

    internal int? LastTemporalUndershootRows { get; private set; }

    internal int? LastTemporalReplacementRows { get; private set; }

    public void Initialize(Bitmap initialFrame, ScrollCaptureOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(options);
        if (_initialized)
        {
            throw new InvalidOperationException("受控长截图拼接器已初始化。");
        }

        _ = _downward.TryAddFrame(initialFrame, options, out _);
        _initialFrameHeight = initialFrame.Height;
        _initialized = true;
        CopyDiagnosticsFrom(_downward);
    }

    public bool TryAddDown(
        Bitmap frame,
        ScrollCaptureOptions options,
        int? expectedRows,
        int? maximumAcceptedNewRows = null,
        bool tolerateQuantizedExpectation = false)
    {
        EnsureInitialized();
        var preferredRows = GetPreferredExpectedRows(
            expectedRows,
            _recentDownwardExpansionRows);
        LastPreferredExpectedRows = preferredRows;
        var added = _downward.TryAddFrame(
            frame,
            ScrollCaptureDirection.Down,
            options,
            preferredRows,
            lockDirection: true,
            maximumAcceptedNewRows,
            out var overlapMatch,
            programmaticExpectedRows: expectedRows,
            tolerateQuantizedExpectation: tolerateQuantizedExpectation);
        if (added && _downward.LastFrameMovementRows is > 0)
        {
            RememberExpansionRows(
                _recentDownwardExpansionRows,
                _downward.LastFrameMovementRows.Value);
            _downward.RefreshBoundaryViewport(
                frame,
                ScrollCaptureDirection.Down,
                GetFixedBottomExclusion(frame.Height),
                _downward.LastFrameMovementRows);
        }
        CopyDiagnosticsFrom(_downward, overlapMatch);
        return added;
    }

    public void MarkDownBoundaryReached()
    {
        EnsureInitialized();
        _downward.MarkBoundaryReached(ScrollCaptureDirection.Down);
    }

    public void BeginUpwardExtension(
        Bitmap initialFrame,
        ScrollCaptureOptions options)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(initialFrame);
        ArgumentNullException.ThrowIfNull(options);
        if (_upward is not null)
        {
            return;
        }

        _upward = new AutomaticScrollCaptureComposerCore(
            detectStationaryLeadingRows: false,
            fixedBottomRows: _downward.FixedBottomRows);
        _ = _upward.TryAddFrame(initialFrame, options, out _);
        CopyDiagnosticsFrom(_upward);
    }

    public bool TryAddUp(
        Bitmap frame,
        ScrollCaptureOptions options,
        int? expectedRows,
        int? maximumAcceptedNewRows = null,
        bool tolerateQuantizedExpectation = false)
    {
        EnsureInitialized();
        if (_upward is null)
        {
            throw new InvalidOperationException("尚未开始向上扩展。");
        }

        var preferredRows = GetPreferredExpectedRows(
            expectedRows,
            _recentUpwardExpansionRows);
        LastPreferredExpectedRows = preferredRows;
        var added = _upward.TryAddFrame(
            frame,
            ScrollCaptureDirection.Up,
            options,
            preferredRows,
            lockDirection: true,
            maximumAcceptedNewRows,
            out var overlapMatch,
            programmaticExpectedRows: expectedRows,
            tolerateQuantizedExpectation: tolerateQuantizedExpectation);
        if (added && _upward.LastFrameMovementRows is > 0)
        {
            RememberExpansionRows(
                _recentUpwardExpansionRows,
                _upward.LastFrameMovementRows.Value);
            _upward.RefreshBoundaryViewport(
                frame,
                ScrollCaptureDirection.Up,
                GetFixedBottomExclusion(frame.Height),
                _upward.LastFrameMovementRows);
        }
        CopyDiagnosticsFrom(_upward, overlapMatch);
        return added;
    }

    public Bitmap Compose()
    {
        EnsureInitialized();
        using var downward = _downward.Compose();
        if (_upward is null)
        {
            return (Bitmap)downward.Clone();
        }

        using var upward = _upward.Compose();
        var width = Math.Max(upward.Width, downward.Width);
        var downwardTop = upward.Height - SharedInitialHeight;
        var height = downwardTop + downward.Height;
        var combined = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(combined);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(upward, 0, 0);
        // The two images share the exact initial viewport. Drawing the downward
        // copy over that overlap gives one initial frame and no heuristic seam.
        graphics.DrawImageUnscaled(downward, 0, downwardTop);
        return combined;
    }

    public Bitmap ComposeLivePreview(int maximumWidth, int maximumHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);

        using var composite = Compose();
        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)composite.Width,
                maximumHeight / (double)composite.Height));
        var width = Math.Max(1, (int)Math.Round(composite.Width * scale));
        var height = Math.Max(1, (int)Math.Round(composite.Height * scale));
        var preview = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(preview);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(
            composite,
            new Rectangle(0, 0, width, height),
            new Rectangle(0, 0, composite.Width, composite.Height),
            GraphicsUnit.Pixel);
        return preview;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _upward?.Dispose();
        _downward.Dispose();
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("受控长截图拼接器尚未初始化。");
        }
    }

    private int SharedInitialHeight => _initialized
        ? _downward.InitialContentHeight
        : _initialFrameHeight;

    private void CopyDiagnosticsFrom(
        AutomaticScrollCaptureComposerCore composer,
        ImageOverlapMatch? overlapMatch = null)
    {
        LastFrameMovementRows = composer.LastFrameMovementRows;
        LastRejectReason = composer.LastRejectReason;
        LastOverlapRows = overlapMatch?.OverlapRows;
        LastOverlapConfidence = overlapMatch?.Confidence;
        LastHorizontalOffset = overlapMatch?.HorizontalOffset;
        LastTemporalUndershootRows = composer.LastTemporalUndershootRows;
        LastTemporalReplacementRows = composer.LastTemporalReplacementRows;
    }

    internal static int GetFixedBottomExclusion(int frameHeight)
    {
        return frameHeight >= 120
            ? Math.Clamp(frameHeight / 20, 16, 24)
            : 0;
    }

    internal static int? GetPreferredExpectedRows(
        int? inputExpectedRows,
        IReadOnlyCollection<int> recentExpansionRows)
    {
        if (inputExpectedRows is not > 0 ||
            recentExpansionRows.Count < 2)
        {
            return inputExpectedRows;
        }

        // Fixed wheel packets measure injected input, while the compositor can
        // still be presenting inertia from preceding packets. Preserve the
        // latest confirmed movement so a smaller packet count cannot pull a
        // periodic code match one line behind. Ignore only a clear retry spike,
        // measured against both preceding samples, so it cannot become the next
        // frame's temporal prior.
        var rows = recentExpansionRows.ToArray();
        var movementTrend = rows[^1];
        if (rows.Length >= 3)
        {
            var precedingMaximum = Math.Max(rows[^2], rows[^3]);
            var spikeThreshold = Math.Max(
                precedingMaximum * 2,
                precedingMaximum + 32);
            if (movementTrend > spikeThreshold)
            {
                movementTrend = precedingMaximum;
            }
        }

        return Math.Max(
            inputExpectedRows.Value,
            movementTrend);
    }

    private static void RememberExpansionRows(
        Queue<int> recentExpansionRows,
        int movementRows)
    {
        if (recentExpansionRows.Count == 3)
        {
            _ = recentExpansionRows.Dequeue();
        }

        recentExpansionRows.Enqueue(movementRows);
    }
}
