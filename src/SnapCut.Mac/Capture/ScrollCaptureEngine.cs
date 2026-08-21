using System.Diagnostics;
using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

/// <summary>
/// The macOS scroll-capture loop: samples the selected region, gates unchanged
/// frames, and stitches the backlog strictly in capture order through the
/// shared <see cref="ScrollCaptureComposer"/>.
/// </summary>
/// <remarks>
/// This mirrors the Windows sampler's proven rules in compact form:
/// chain-sequential stitching (consecutive samples always overlap regardless
/// of scroll speed); sampling backpressure — when the backlog exceeds a
/// quarter of its capacity, sampling skips a beat so retained frames stay one
/// processing period apart, except that an estimated displacement of a quarter
/// viewport or 120ms without a sample forces one; over-capacity decimation
/// drops a middle frame and merges its wheel motion into the successor; and a
/// frame that cannot be located forfeits its pixels but carries its wheel
/// motion into the following sample (via the backlog head, or a carried slot
/// when the backlog is empty).
/// </remarks>
internal sealed class ScrollCaptureEngine
{
    private const int SampleIntervalMilliseconds = 16;
    private const int ForcedSampleIntervalMilliseconds = 120;
    private const int BacklogCapacity = 48;
    private const int DrainBudgetMilliseconds = 1200;

    private readonly ScrollCaptureOptions _options;
    private readonly ScrollWheelMotionTracker _tracker = new();
    private readonly CapturedFrameGate _gate = new();
    private readonly List<PendingFrame> _backlog = [];
    private ScrollWheelMotionSample? _carriedMotion;
    private ScrollCaptureDirection _lastDirection = ScrollCaptureDirection.Down;
    private int _frameHeight;

    public ScrollCaptureEngine(ScrollCaptureOptions options)
    {
        _options = options;
    }

    private readonly record struct PendingFrame(
        PixelImage Image,
        ScrollWheelMotionSample Motion);

    public sealed record Progress(
        int StitchedFrames,
        int OutputHeight,
        int BacklogCount,
        string? LastRejectReason);

    public PixelImage Run(
        CGRect region,
        CancellationToken cancellationToken,
        Action<Progress>? onProgress = null)
    {
        using var composer = new ScrollCaptureComposer();
        using var monitor = new MacScrollWheelMonitor(_tracker);

        if (!monitor.TryStart())
        {
            Console.Error.WriteLine(
                "警告：无法监听滚轮（缺少输入监控权限）。仍可拼接，方向完全由图像证据决定。");
        }

        var firstFrame = MacScreenCaptureService.CaptureRegion(region);
        _frameHeight = firstFrame.Height;
        _gate.Accept(firstFrame);
        composer.TryAddFrame(firstFrame, _options, out _);

        var clock = Stopwatch.StartNew();
        var lastSampleAt = clock.ElapsedMilliseconds;
        var nextSampleAt = lastSampleAt + SampleIntervalMilliseconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = clock.ElapsedMilliseconds;

            if (now >= nextSampleAt)
            {
                nextSampleAt = now + SampleIntervalMilliseconds;
                var backpressure = _backlog.Count > BacklogCapacity / 4;
                var expectedRows = _tracker.GetExpectedRows(
                    _frameHeight,
                    _options);
                var forced =
                    now - lastSampleAt >= ForcedSampleIntervalMilliseconds ||
                    (expectedRows is { } rows && rows >= _frameHeight / 4);

                if (!backpressure || forced)
                {
                    Sample(region);
                    lastSampleAt = now;
                }
            }

            if (_backlog.Count > 0)
            {
                StitchOne(composer);
                onProgress?.Invoke(new Progress(
                    composer.FrameCount,
                    composer.OutputHeight,
                    _backlog.Count,
                    composer.LastRejectReason));
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        // Completion: keep stitching what is already captured, within a budget,
        // so a fast final fling is not discarded.
        var drainDeadline = clock.ElapsedMilliseconds + DrainBudgetMilliseconds;
        while (_backlog.Count > 0 && clock.ElapsedMilliseconds < drainDeadline)
        {
            StitchOne(composer);
        }

        return composer.Compose();
    }

    private void Sample(CGRect region)
    {
        var frame = MacScreenCaptureService.CaptureRegion(region);

        if (!_gate.HasChanged(frame))
        {
            return;
        }

        _gate.AcceptPending();
        var motion = _tracker.TakePendingMotion(
            _frameHeight,
            _options,
            _lastDirection);

        if (_carriedMotion is { } carried)
        {
            motion = _tracker.MergeMotion(carried, motion, _frameHeight, _options);
            _carriedMotion = null;
        }

        _backlog.Add(new PendingFrame(frame, motion));

        while (_backlog.Count > BacklogCapacity)
        {
            var victim = ScrollFrameSelection.SelectDecimationIndex(_backlog.Count);
            var successorIndex = victim + 1 < _backlog.Count ? victim + 1 : victim - 1;
            var merged = _tracker.MergeMotion(
                _backlog[victim].Motion,
                _backlog[successorIndex].Motion,
                _frameHeight,
                _options);
            _backlog[successorIndex] = _backlog[successorIndex] with { Motion = merged };
            _backlog.RemoveAt(victim);
        }
    }

    private void StitchOne(ScrollCaptureComposer composer)
    {
        var pending = _backlog[0];
        _backlog.RemoveAt(0);
        var direction = pending.Motion.HasFreshInput
            ? pending.Motion.Direction
            : _lastDirection;

        composer.TryAddFrame(
            pending.Image,
            direction,
            _options,
            pending.Motion.ExpectedRows,
            pending.Motion.HasFreshInput,
            out _);

        if (composer.LastFrameMovementRows is { } movedRows && movedRows > 0)
        {
            _tracker.ObserveMovement(movedRows, pending.Motion.Delta);
            _lastDirection = direction;
            return;
        }

        // The frame could not be located: its pixels are forfeit but its wheel
        // motion must survive into the following sample, or the next expected
        // displacement would be far too small.
        if (!pending.Motion.HasFreshInput)
        {
            return;
        }

        if (_backlog.Count > 0)
        {
            var next = _backlog[0];
            _backlog[0] = next with
            {
                Motion = _tracker.MergeMotion(
                    pending.Motion,
                    next.Motion,
                    _frameHeight,
                    _options),
            };
        }
        else
        {
            _carriedMotion = _carriedMotion is { } carried
                ? _tracker.MergeMotion(
                    carried,
                    pending.Motion,
                    _frameHeight,
                    _options)
                : pending.Motion;
        }
    }
}
