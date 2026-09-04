using System.Diagnostics;
using System.Threading.Channels;
using System.Drawing;
using System.Drawing.Imaging;

namespace Screenshot.App.Capture;

public static class ScrollCaptureService
{
    // Chromium and many modern desktop controls continue a smooth scroll well
    // after the final wheel message. Keep sampling through that tail so the
    // final strips are not lost when a user reverses direction or stops.
    private const int MinimumActiveScrollWindowMilliseconds = 560;
    private const int SettlingSampleDelayMilliseconds = 16;
    // A wheel can advance a viewport by several hundred pixels in under the
    // public frame delay. Keep the live sampler faster than a common 60 Hz
    // render cadence pair so adjacent captures still overlap during a fling;
    // the bounded queue applies backpressure when matching is slower.
    private const int ActiveScrollSampleDelayMilliseconds = 32;
    // Completion should feel immediate. A bounded tail still captures common
    // smooth-scroll inertia, while the idle break below avoids doing every
    // possible sample when the viewport has already stopped.
    private const int CompletionSettleMilliseconds = 80;
    private const int PreviewMaximumWidth = 260;
    // Tall enough that the whole-image preview keeps real detail while the
    // preview window grows, bounded so each update stays a small bitmap.
    private const int PreviewMaximumHeight = 1600;
    // Sampling is far cheaper than matching, so a fling produces samples
    // faster than the matcher can stitch them and a backlog builds up. The
    // backlog must be deep enough to hold an entire fling: consecutive samples
    // always overlap each other, but once decimation is forced to merge
    // neighbors the chain gap doubles, and a gap beyond one viewport can never
    // be matched again. A fling lasts one to two seconds and sampling is paced
    // to the compositor, so roughly forty retained samples bridge the burst
    // and let the chain drain losslessly once the motion stops.
    private const int ActiveFrameQueueMaximumCapacity = 72;
    // Image matching is intentionally conservative, but a smooth-scroll burst
    // can still produce dozens of same-direction samples while one match is in
    // flight. Replaying that whole run makes the preview seconds behind the
    // actual viewport and can consume the only frames that cross a captured
    // boundary. Once this many same-direction samples are pending, keep the
    // freshest sample and carry the measured wheel motion into it.
    private const int SameDirectionBacklogCollapseThreshold = 12;
    private const int SameDirectionRetainedHeadFrames = 3;
    private const int SameDirectionRetainedTailFrames = 5;
    private const long ActiveFrameQueueMemoryBudgetBytes = 160L * 1024 * 1024;
    // The hand-off channel and the matcher backlog can both be full at once.
    private const int ActiveFrameBufferCount = 2;
    // Finishing has to feel quick, but a deep backlog after a final fling is
    // real content the user scrolled past: at a few tens of milliseconds per
    // chain step, this budget drains a full backlog rather than discarding it.
    // Finish/Edit must feel immediate, but reverse flings and long code
    // scrolls still need their pending chain frames. Drain for a short
    // interactive budget; only then collapse onto the freshest viewport.
    private const int CompletionDrainBudgetMilliseconds = 1800;
    // Longest stretch backpressure may go without taking a sample when the
    // viewport is moving slowly. SampleCadence shrinks the window with the
    // observed scroll speed: at fling speed a 120ms hole moves the screen by
    // more than one viewport, which no stitcher can bridge afterwards.
    private const int DefaultSampleSkipWindowMilliseconds = 120;
    // The fixed-rate driver is sampled frequently enough that a slow matcher
    // still receives a small-overlap frame instead of a large accumulated jump.
    private const int ControlledCaptureSampleDelayMilliseconds = 40;
    private const int ControlledSettleSampleDelayMilliseconds = 90;
    private const int ControlledScrollBoundarySamples = 4;
    // Smooth-wheel presentation can defer visible motion. Zero-motion samples
    // are not boundary evidence until input has continued well beyond the last
    // visible movement.
    private const int ControlledBoundaryConfirmationTravelPixels = 64;
    private const int ControlledBoundaryRecoveryAttempts = 1;
    private const int ControlledSettleSamples = 2;
    private const int ControlledCompletionSettleAttempts = 10;
    private const int ControlledReturnInputOvershootPixels = 48;
    private const int ControlledInitialBoundaryStationarySamples = 2;
    private const double ControlledInitialCrossingMinimumConfidence = 0.965;
    private const double ControlledInputReturnMinimumConfidence = 0.985;
    private const int ControlledReanchorAttempts = 3;
    private const int ControlledReanchorDelayMilliseconds = 110;
    // How many consecutive unlocated samples may trigger the automatic resume
    // re-anchor before the capture gives up and pauses itself.
    private const int ControlledAutomaticReanchorRounds = 2;
    private const int ControlledAlignmentCorrectionAttempts = 12;
    // Chat UIs keep the fingerprint "moving" forever. Cap Aligning* so a
    // confirmed return cannot strand the capture on "正在对齐初始位置".
    private const int ControlledAlignmentMaxSettleAttempts = 8;
    // Precision touchpads and touch screens can move the target without
    // producing a low-level WM_MOUSEWHEEL packet. Keep the fallback sparse;
    // it is only enabled for manual capture and only becomes a motion signal
    // when the existing overlap matcher can identify a direction.
    private const int ManualViewportMotionProbeDelayMilliseconds = 120;
    private const int ManualViewportMotionSignalCooldownMilliseconds = 240;

    public static async Task<ScrollCaptureResult> CaptureOnWheelAsync(
        ScrollCaptureTarget target,
        Task completionRequested,
        ChannelReader<int> wheelEvents,
        ScrollCaptureOptions? options = null,
        Func<bool, CancellationToken, Task>? setPreviewVisibilityAsync = null,
        Action<ScrollCapturePreviewState>? previewChanged = null,
        bool throttleWheelInput = false,
        bool enableViewportMotionFallback = false,
        Bitmap? initialFrame = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(completionRequested);
        ArgumentNullException.ThrowIfNull(wheelEvents);
        options ??= ScrollCaptureOptions.Default;

        if (target.WindowHandle == IntPtr.Zero || target.CaptureRegion.IsEmpty)
        {
            return ScrollCaptureResult.Failure("无法识别滚动目标窗口。");
        }

        using var composer = new ScrollCaptureComposer();
        using var preparedCache = new PreparedCaptureCache();
        var frameGate = new CapturedFrameGate();
        var frameDump = ScrollCaptureFrameDump.Create();
        var diagnostics = new ScrollCaptureDiagnostics();
        Channel<QueuedScrollFrame>? activeFrameQueue = null;
        Task? activeFrameProcessor = null;
        ManualScrollDriver? throttledScrollDriver = null;
        Bitmap? lastMotionProbeFrame = null;

        try
        {
            diagnostics.Record(
                "capture-start",
                ("width", target.CaptureRegion.Width),
                ("height", target.CaptureRegion.Height),
                ("frameDelayMs", options.FrameDelayMilliseconds),
                ("queueCapacity", GetActiveFrameQueueCapacity(
                    target.CaptureRegion)));
            using (var capturedInitialFrame = initialFrame ?? await CaptureFrameAsync(
                       target,
                       setPreviewVisibilityAsync,
                       cancellationToken))
            {
                if (enableViewportMotionFallback)
                {
                    lastMotionProbeFrame = new Bitmap(capturedInitialFrame);
                }

                frameGate.Accept(capturedInitialFrame);
                frameDump.SaveInitial(capturedInitialFrame);
                _ = await Task.Run(
                    () => composer.TryAddFrame(capturedInitialFrame, options, out _),
                    cancellationToken);
            }

            await preparedCache.PrepareIfDueAsync(
                composer,
                force: true,
                cancellationToken);

            await ReportPreviewAsync(
                composer,
                previewChanged,
                cancellationToken);

            ScrollCaptureDirection? activeDirection = null;
            var motionTracker = new ScrollWheelMotionTracker();
            var frameQueueState = new ActiveFrameQueueState(diagnostics);
            var sampleCadence = new SampleCadence(target.CaptureRegion.Height);
            var boundaryDetector = new StationaryScrollBoundaryDetector();
            if (throttleWheelInput)
            {
                throttledScrollDriver = new ManualScrollDriver(target);
                diagnostics.Record(
                    "wheel-input-throttled",
                    ("tickIntervalMs", ManualScrollDriver.TickIntervalMilliseconds),
                    ("pixelsPerTick", ManualScrollDriver.CapturePixelsPerTick));
            }
            var lastWheelEventTimestamp = 0L;
            var lastDiagnosticWheelTimestamp = 0L;
            var completionRequestedTimestamp = 0L;
            var previewTimestampSlot = new long[1];
            var directManualWheel = !throttleWheelInput;
            var activeScrollWindow = TimeSpan.FromMilliseconds(Math.Max(
                directManualWheel
                    ? MinimumActiveScrollWindowMilliseconds + 240
                    : MinimumActiveScrollWindowMilliseconds,
                options.FrameDelayMilliseconds * 4));
            var nextWheelEvent = wheelEvents.ReadAsync(
                cancellationToken).AsTask();
            var nextSample = Task.Delay(
                options.FrameDelayMilliseconds,
                cancellationToken);
            var nextMotionProbe = enableViewportMotionFallback
                ? Task.Delay(
                    ManualViewportMotionProbeDelayMilliseconds,
                    cancellationToken)
                : Task.Delay(Timeout.Infinite, cancellationToken);
            var activeFrameCapacity = GetActiveFrameQueueCapacity(
                target.CaptureRegion);
            // Start pacing before a large high-resolution queue is a quarter
            // full. At that point the matcher can already be several seconds
            // behind the visible viewport, so it is stitching stale smooth-
            // scroll transition frames. The cadence guard below still forces
            // enough samples to preserve overlap continuity.
            var backpressureThreshold = GetBackpressureThreshold(
                activeFrameCapacity);
            var throttledPausedForBackpressure = false;
            var lastSampleTimestamp = 0L;
            activeFrameQueue = Channel.CreateBounded<QueuedScrollFrame>(
                new BoundedChannelOptions(activeFrameCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    // TryWrite must fail when full so we can preserve the older
                    // overlap chain and carry the rejected wheel delta forward.
                    // DropOldest silently removed the only bridge between two
                    // viewports and was the primary source of large false seams.
                    FullMode = BoundedChannelFullMode.Wait,
                });
            activeFrameProcessor = ProcessQueuedFramesAsync(
                activeFrameQueue.Reader,
                composer,
                options,
                previewChanged,
                previewTimestampSlot,
                preparedCache,
                motionTracker,
                frameQueueState,
                diagnostics,
                activeFrameCapacity,
                sampleCadence,
                throttledScrollDriver,
                cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completedTask = await Task.WhenAny(
                    completionRequested,
                    nextWheelEvent,
                    nextMotionProbe,
                    nextSample);

                if (completedTask == completionRequested)
                {
                    await completionRequested;
                    throttledScrollDriver?.SetDirection(null);
                    var finalInjectedWheelDelta =
                        throttledScrollDriver?.TakeInjectedWheelDelta() ?? 0;
                    if (finalInjectedWheelDelta != 0)
                    {
                        motionTracker.AddDelta(finalInjectedWheelDelta);
                        activeDirection = motionTracker.Direction;
                        lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                    }
                    var completionTimestamp = Stopwatch.GetTimestamp();
                    completionRequestedTimestamp = completionTimestamp;
                    diagnostics.Record(
                        "completion-requested",
                        ("queuedFrames", frameQueueState.QueueCount),
                        ("latestSequence", frameQueueState.LatestSequence),
                        ("outputHeight", composer.OutputHeight));

                    // In direct mode, late events describe movement that the
                    // target already received. In throttled mode they are only
                    // unexecuted requests, so do not turn them into fake motion
                    // after the driver has stopped.
                    while (wheelEvents.TryRead(out var lateDelta))
                    {
                        if (!throttleWheelInput && lateDelta != 0)
                        {
                            motionTracker.AddDelta(lateDelta);
                            activeDirection = motionTracker.Direction;
                            lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                        }
                    }

                    activeFrameQueue.Writer.TryComplete();
                    // Prefer ending where the user is now. If the matcher is
                    // still draining a fling backlog when Finish/Edit is
                    // clicked, collapsing intermediate frames onto the latest
                    // sample keeps the final image continuous without making
                    // the UI wait several seconds for every queued match.
                    await activeFrameProcessor;
                    activeFrameProcessor = null;
                    diagnostics.Record(
                        "queue-drained",
                        ("durationMs", Stopwatch.GetElapsedTime(
                            completionTimestamp).TotalMilliseconds),
                        ("frameCount", composer.FrameCount),
                        ("outputHeight", composer.OutputHeight));
                    // Publish the post-drain counts even when the last few
                    // expansion frames were throttled during the fling.
                    await ReportPreviewAsync(
                        composer,
                        previewChanged,
                        cancellationToken);

                    if (activeDirection is not null &&
                        composer.FrameCount < options.MaximumFrames)
                    {
                        var settleOptions = CreateSettleOptions(
                            options,
                            target.CaptureRegion.Height);
                        // WeChat-like: keep sampling through inertia after the
                        // user taps Finish so the last strips are not lost.
                        var settleSteps = Math.Max(
                            2,
                            CompletionSettleMilliseconds /
                                Math.Max(16, SettlingSampleDelayMilliseconds));

                        var idleSettleSteps = 0;
                        for (var step = 0;
                             step < settleSteps &&
                             composer.FrameCount < options.MaximumFrames;
                             step++)
                        {
                            var wasAdded = await TryAddCurrentFrameForDirectionAsync(
                                target,
                                composer,
                                activeDirection.Value,
                                settleOptions,
                                setPreviewVisibilityAsync,
                                previewChanged,
                                cancellationToken,
                                frameDump,
                                forcePreview: step == settleSteps - 1,
                                previewTimestampSlot: previewTimestampSlot,
                                preparedCache: preparedCache,
                                motionTracker: motionTracker);

                            idleSettleSteps = wasAdded
                                ? 0
                                : idleSettleSteps + 1;
                            if (step >= 2 && idleSettleSteps >= 3)
                            {
                                break;
                            }

                            if (step + 1 < settleSteps)
                            {
                                await Task.Delay(
                                    SettlingSampleDelayMilliseconds,
                                    cancellationToken);
                            }
                        }

                        // The chain can end unlocated: the page's last hop
                        // lands at its physical edge and that final frame
                        // matched nothing (field trace lost the top ~2 text
                        // lines this way). The stored edge frame is an
                        // absolute reference for exactly this case.
                        if (composer.LastRejectReason is not null)
                        {
                            using var finalFrame = await CaptureFrameAsync(
                                target,
                                null,
                                cancellationToken);
                            var reanchored = await Task.Run(
                                () => composer.TryReanchorAtBoundary(
                                    finalFrame,
                                    activeDirection.Value,
                                    settleOptions),
                                cancellationToken);
                            diagnostics.Record(
                                "completion-reanchor",
                                ("direction", activeDirection.Value.ToString()),
                                ("succeeded", reanchored),
                                ("outputHeight", composer.OutputHeight));
                            if (reanchored)
                            {
                                await ReportPreviewAsync(
                                    composer,
                                    previewChanged,
                                    cancellationToken);
                            }
                        }
                    }

                    break;
                }

                if (completedTask == nextWheelEvent)
                {
                    var wheelDelta = await nextWheelEvent;
                    var observedWheelDelta = wheelDelta;
                    ScrollCaptureDirection? throttledRequestedDirection = null;

                    if (wheelDelta != 0 && throttleWheelInput)
                    {
                        // Keep manual-wheel input attached to the window that
                        // was selected for capture.  The progress preview is
                        // topmost and non-activating, but a focus change can
                        // still leave the editor stationary while the global
                        // hook continues to report wheel messages.
                        _ = ForegroundWindowCaptureService.TryFocusScrollTarget(
                            target);
                    }

                    if (wheelDelta != 0)
                    {
                        if (throttleWheelInput)
                        {
                            throttledScrollDriver?.QueueCaptureInput(wheelDelta);
                            throttledRequestedDirection = wheelDelta > 0
                                ? ScrollCaptureDirection.Up
                                : ScrollCaptureDirection.Down;
                        }
                        else
                        {
                            motionTracker.AddDelta(wheelDelta);
                        }
                    }

                    while (wheelEvents.TryRead(out var additionalDelta))
                    {
                        if (additionalDelta != 0)
                        {
                            if (throttleWheelInput)
                            {
                                throttledScrollDriver?.QueueCaptureInput(
                                    additionalDelta);
                                throttledRequestedDirection = additionalDelta > 0
                                    ? ScrollCaptureDirection.Up
                                    : ScrollCaptureDirection.Down;
                            }
                            else
                            {
                                motionTracker.AddDelta(additionalDelta);
                            }
                            observedWheelDelta = (int)Math.Clamp(
                                (long)observedWheelDelta + additionalDelta,
                                int.MinValue,
                                int.MaxValue);
                        }
                    }

                    if (motionTracker.HasPendingInput ||
                        throttledRequestedDirection is not null)
                    {
                        activeDirection = throttledRequestedDirection ??
                            motionTracker.Direction;
                        throttledScrollDriver?.SetDirection(activeDirection);
                        lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                        boundaryDetector.ObserveWheel(
                            activeDirection.Value,
                            lastWheelEventTimestamp,
                            observedWheelDelta);
                        var wheelInterval = lastDiagnosticWheelTimestamp == 0L
                            ? 0d
                            : Stopwatch.GetElapsedTime(
                                lastDiagnosticWheelTimestamp).TotalMilliseconds;
                        lastDiagnosticWheelTimestamp = lastWheelEventTimestamp;
                        diagnostics.Record(
                            "wheel",
                            ("direction", activeDirection.Value.ToString()),
                            ("pendingDelta", motionTracker.PendingDelta),
                            ("queuedInputSteps",
                                throttledScrollDriver?.PendingCaptureStepCount ?? 0),
                            ("intervalMs", wheelInterval));
                    }

                    nextWheelEvent = wheelEvents.ReadAsync(
                        cancellationToken).AsTask();
                    continue;
                }

                if (completedTask == nextMotionProbe)
                {
                    try
                    {
                        using var probeFrame = await CaptureFrameAsync(
                            target,
                            null,
                            cancellationToken);
                        var previousProbeFrame = lastMotionProbeFrame;
                        lastMotionProbeFrame = new Bitmap(probeFrame);

                        var probeElapsed = lastWheelEventTimestamp == 0L
                            ? TimeSpan.MaxValue
                            : Stopwatch.GetElapsedTime(lastWheelEventTimestamp);
                        if (previousProbeFrame is not null &&
                            probeElapsed.TotalMilliseconds >=
                                ManualViewportMotionSignalCooldownMilliseconds &&
                            TryInferViewportDirection(
                                previousProbeFrame,
                                probeFrame,
                                options,
                                out var inferredDirection))
                        {
                            var inferredWheelDelta = inferredDirection ==
                                ScrollCaptureDirection.Up
                                ? 120
                                : -120;
                            motionTracker.AddDelta(inferredWheelDelta);
                            activeDirection = inferredDirection;
                            lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                            boundaryDetector.ObserveWheel(
                                inferredDirection,
                                lastWheelEventTimestamp,
                                inferredWheelDelta);
                            diagnostics.Record(
                                "viewport-motion-fallback",
                                ("direction", inferredDirection.ToString()),
                                ("wheelDelta", inferredWheelDelta));
                        }

                        previousProbeFrame?.Dispose();
                    }
                    finally
                    {
                        nextMotionProbe = Task.Delay(
                            ManualViewportMotionProbeDelayMilliseconds,
                            cancellationToken);
                    }

                    continue;
                }

                var elapsedSinceWheel = lastWheelEventTimestamp == 0L
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(lastWheelEventTimestamp);
                var sampleDelayMilliseconds = GetActiveSampleDelayMilliseconds(
                    options.FrameDelayMilliseconds,
                    elapsedSinceWheel,
                    directManualWheel);
                nextSample = Task.Delay(
                    sampleDelayMilliseconds,
                    cancellationToken);

                if (activeDirection is null ||
                    elapsedSinceWheel > activeScrollWindow)
                {
                    var queuedDirection = throttleWheelInput
                        ? throttledScrollDriver?.PendingCaptureDirection
                        : null;
                    if (queuedDirection is not null)
                    {
                        activeDirection = queuedDirection;
                        throttledScrollDriver?.SetDirection(activeDirection);
                    }
                    else
                    {
                        throttledScrollDriver?.SetDirection(null);
                        continue;
                    }
                }

                // Backpressure: when the stitcher falls behind, sampling any
                // faster only forces backlog decimation, and decimated gaps
                // are what break the chain. Skipping the tick makes retained
                // samples arrive at the stitching rate instead. The skip is
                // bounded by travel, not just count: once the wheel says the
                // viewport moved a quarter frame — or enough time has passed
                // that smooth-scroll inertia could have — the sample must be
                // taken anyway, because a gap beyond one viewport can never be
                // matched no matter how deep the buffers are.
                var estimatedPendingRows = motionTracker.GetExpectedRowsForDelta(
                    target.CaptureRegion.Height,
                    options,
                    motionTracker.PendingDelta) ?? 0;
                if (throttleWheelInput &&
                    frameQueueState.PendingStitchCount >= backpressureThreshold)
                {
                    if (!throttledPausedForBackpressure)
                    {
                        throttledPausedForBackpressure = true;
                        throttledScrollDriver?.SetDirection(null);
                        diagnostics.Record(
                            "wheel-input-backpressure-paused",
                            ("pending", frameQueueState.PendingStitchCount),
                            ("threshold", backpressureThreshold));
                    }

                    continue;
                }

                if (throttleWheelInput &&
                    throttledPausedForBackpressure &&
                    frameQueueState.PendingStitchCount <=
                        Math.Max(1, backpressureThreshold / 2))
                {
                    throttledPausedForBackpressure = false;
                    throttledScrollDriver?.SetDirection(activeDirection);
                    diagnostics.Record(
                        "wheel-input-backpressure-resumed",
                        ("pending", frameQueueState.PendingStitchCount));
                }

                var mustSample =
                    estimatedPendingRows >= target.CaptureRegion.Height / 4 ||
                    lastSampleTimestamp == 0L ||
                    Stopwatch.GetElapsedTime(lastSampleTimestamp) >=
                        sampleCadence.MaximumSkipWindow;
                if (!mustSample &&
                    frameQueueState.PendingStitchCount >= backpressureThreshold)
                {
                    diagnostics.Record(
                        "frame-sample-skipped-backpressure",
                        ("pending", frameQueueState.PendingStitchCount));
                    continue;
                }

                lastSampleTimestamp = Stopwatch.GetTimestamp();
                await CaptureAndQueueFrameAsync(
                    target,
                    activeFrameQueue.Writer,
                    activeDirection.Value,
                    options,
                    setPreviewVisibilityAsync,
                    frameDump,
                    motionTracker,
                    frameQueueState,
                    frameGate,
                    boundaryDetector,
                    diagnostics,
                    throttledScrollDriver,
                    // Do not build a second full-size result while the user is
                    // still scrolling. On long captures this competed with the
                    // matcher and let the live preview fall seconds behind. The
                    // bounded completion settle/final compose still prepares the
                    // finished image before the editor or clipboard receives it.
                    prepareResult: false,
                    cancellationToken: cancellationToken);
            }

            // Prefer a usable result over hard failure. A single frame is still
            // the selected region; partial stitches are better than an error tip.
            if (composer.FrameCount < 1)
            {
                return ScrollCaptureResult.Failure("滚动截图失败。");
            }

            // Materialize the final bitmap off the UI thread while the progress
            // window is still up. Preparing here (after matching has stopped)
            // makes Edit/clipboard hand-off a transfer instead of a multi-
            // second full-size conversion on the first Preview access.
            await preparedCache.PrepareIfDueAsync(
                composer,
                force: true,
                cancellationToken);

            var composeTimestamp = Stopwatch.GetTimestamp();
            var result = await ComposeResultAsync(
                composer,
                cancellationToken,
                preparedCache);
            diagnostics.Record(
                "result-ready",
                ("durationMs", Stopwatch.GetElapsedTime(
                    composeTimestamp).TotalMilliseconds),
                ("totalAfterClickMs", completionRequestedTimestamp == 0L
                    ? 0d
                    : Stopwatch.GetElapsedTime(
                        completionRequestedTimestamp).TotalMilliseconds),
                ("success", result.IsSuccess),
                ("frameCount", composer.FrameCount),
                ("outputWidth", composer.OutputWidth),
                ("outputHeight", composer.OutputHeight));
            return result;
        }
        catch (OperationCanceledException)
        {
            return ScrollCaptureResult.Failure("滚动截图已取消。");
        }
        catch (ChannelClosedException)
        {
            return ScrollCaptureResult.Failure("滚动截图已取消。");
        }
        catch (Exception exception)
        {
            diagnostics.Record(
                "capture-failed",
                ("exceptionType", exception.GetType().FullName),
                ("message", exception.Message),
                ("stackTrace", exception.StackTrace));
            return ScrollCaptureResult.Failure("滚动截图失败。");
        }
        finally
        {
            activeFrameQueue?.Writer.TryComplete();
            if (activeFrameProcessor is not null)
            {
                try
                {
                    await activeFrameProcessor;
                }
                catch
                {
                    // The public result above already reports cancellation or
                    // failure; still drain/dispose queued bitmaps before exit.
                }
            }

            if (throttledScrollDriver is not null)
            {
                try
                {
                    await throttledScrollDriver.DisposeAsync();
                }
                catch
                {
                }
            }

            lastMotionProbeFrame?.Dispose();

            diagnostics.FlushInBackground();
        }
    }

    public static async Task<ScrollCaptureResult> CaptureManualAsync(
        ScrollCaptureTarget target,
        Task completionRequested,
        ScrollCaptureOptions? options = null,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync = null,
        Action<int>? acceptedFrameCountChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(completionRequested);
        options ??= ScrollCaptureOptions.Default;

        if (target.WindowHandle == IntPtr.Zero || target.CaptureRegion.IsEmpty)
        {
            return ScrollCaptureResult.Failure("无法识别滚动目标窗口。");
        }

        using var composer = new ScrollCaptureComposer();

        try
        {
            using (var initialFrame = await CaptureFrameAsync(
                       target,
                       setProgressVisibilityAsync,
                       cancellationToken))
            {
                _ = await Task.Run(
                    () => composer.TryAddFrame(initialFrame, options, out _),
                    cancellationToken);
            }

            acceptedFrameCountChanged?.Invoke(composer.FrameCount);
            var shouldCaptureFinalFrame = false;

            while (composer.FrameCount < options.MaximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sampleDelay = Task.Delay(
                    options.FrameDelayMilliseconds,
                    cancellationToken);
                var completedTask = await Task.WhenAny(
                    completionRequested,
                    sampleDelay);

                if (completedTask == completionRequested)
                {
                    await completionRequested;
                    shouldCaptureFinalFrame = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await TryAddCurrentFrameAsync(
                    target,
                    composer,
                    options,
                    setProgressVisibilityAsync,
                    acceptedFrameCountChanged,
                    cancellationToken);
            }

            if (shouldCaptureFinalFrame &&
                composer.FrameCount < options.MaximumFrames)
            {
                await TryAddCurrentFrameAsync(
                    target,
                    composer,
                    options,
                    setProgressVisibilityAsync,
                    acceptedFrameCountChanged,
                    cancellationToken);
            }

            // Prefer a usable result over hard failure. A single frame is still
            // the selected region; partial stitches are better than an error tip.
            if (composer.FrameCount < 1)
            {
                return ScrollCaptureResult.Failure("滚动截图失败。");
            }

            return await ComposeResultAsync(composer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScrollCaptureResult.Failure("长截图已取消。");
        }
        catch (Exception)
        {
            return ScrollCaptureResult.Failure("滚动截图失败。");
        }
    }

    private static bool TryInferViewportDirection(
        Bitmap previousFrame,
        Bitmap currentFrame,
        ScrollCaptureOptions options,
        out ScrollCaptureDirection direction)
    {
        direction = default;
        if (previousFrame.Width != currentFrame.Width ||
            previousFrame.Height != currentFrame.Height)
        {
            return false;
        }

        // The existing matcher is used only as an input adapter here. The
        // normal composer still receives the same wheel direction and runs the
        // unchanged manual stitching path.
        var downMatch = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            options.MinimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows);
        var upMatch = ImageOverlapMatcher.FindVerticalOverlap(
            currentFrame,
            previousFrame,
            options.MinimumOverlapRows,
            options.MinimumOverlapConfidence,
            options.MinimumNewRows);

        if (downMatch is null && upMatch is null)
        {
            return false;
        }

        if (upMatch is null ||
            (downMatch is not null &&
             downMatch.Confidence >= upMatch.Confidence + 0.02))
        {
            direction = ScrollCaptureDirection.Down;
            return true;
        }

        if (downMatch is null ||
            upMatch.Confidence >= downMatch.Confidence + 0.02)
        {
            direction = ScrollCaptureDirection.Up;
            return true;
        }

        // A near tie is ambiguous (for example a repeated list row). Let the
        // next probe establish a decisive direction instead of feeding a
        // potentially wrong motion signal into the capture pipeline.
        return false;
    }

    public static async Task<ScrollCaptureResult> CaptureControlledAsync(
        ScrollCaptureTarget target,
        Task completionRequested,
        ChannelReader<ScrollCapturePointerAction> pointerActions,
        ScrollCaptureOptions? options = null,
        Action<ControlledScrollCaptureState>? stateChanged = null,
        Action<ScrollCapturePreviewState>? previewChanged = null,
        Bitmap? initialFrame = null,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(completionRequested);
        ArgumentNullException.ThrowIfNull(pointerActions);
        options ??= ScrollCaptureOptions.Default;

        if (target.WindowHandle == IntPtr.Zero || target.CaptureRegion.IsEmpty)
        {
            return ScrollCaptureResult.Failure("无法识别滚动目标窗口。");
        }

        using var composer = new ControlledScrollCaptureComposer();
        var diagnostics = new ScrollCaptureDiagnostics();
        var hasOriginalCursorPosition = ForegroundWindowCaptureService
            .TryGetCursorPosition(out var originalCursorPosition);

        void NotifyState(ControlledScrollCaptureState state)
        {
            diagnostics.Record(
                "controlled-state",
                ("state", state.ToString()),
                ("frameCount", composer.FrameCount),
                ("outputHeight", composer.OutputHeight));
            stateChanged?.Invoke(state);
        }

        try
        {
            diagnostics.Record(
                "controlled-capture-start",
                ("width", target.CaptureRegion.Width),
                ("height", target.CaptureRegion.Height),
                ("inputTickIntervalMs",
                    ControlledScrollDriver.TickIntervalMilliseconds),
                ("controlledInputUnitsPerSecond",
                    ControlledScrollDriver.CapturePixelsPerTick *
                    (1000 / ControlledScrollDriver.TickIntervalMilliseconds)),
                ("returnInputUnitsPerSecond",
                    ControlledScrollDriver.ReturnPixelsPerTick *
                    (1000 / ControlledScrollDriver.TickIntervalMilliseconds)),
                ("sampleDelayMs", ControlledCaptureSampleDelayMilliseconds),
                ("presentationSettleMs",
                    ControlledScrollDriver.PresentationSettleMilliseconds),
                ("settleDelayMs", ControlledSettleSampleDelayMilliseconds));
            using var capturedInitialFrame = initialFrame ??
                await CaptureFrameAsync(target, null, cancellationToken);
            var initialFingerprint = AutomaticViewportFingerprint.Create(
                capturedInitialFrame);
            var previousFingerprint = initialFingerprint;
            await Task.Run(
                () => composer.Initialize(capturedInitialFrame, options),
                cancellationToken);
            await ReportControlledPreviewAsync(
                composer,
                previewChanged,
                cancellationToken);
            await using var scrollDriver = new ControlledScrollDriver(target);

            var state = ControlledScrollCaptureState.WaitingToStart;
            NotifyState(state);
            var stationarySamples = 0;
            var settleStationarySamples = 0;
            var sequence = 0;
            var returnSteps = 0;
            var returnStationarySamples = 0;
            long outboundInputMagnitude = 0;
            var outboundVisualTravelRows = 0;
            long returnInputMagnitude = 0;
            int? pendingInitialCrossingRows = null;
            long pendingInitialCrossingInputMagnitude = 0;
            var pendingInitialCrossingConfidence = 0d;
            var resumeAnchorPending = false;
            var legHasVisibleMovement = false;
            var outboundHadVisibleMovement = false;
            var unlocatedProgramSteps = 0;
            var boundaryRecoveryAttempts = 0;
            long locatedInputMagnitude = 0;
            long lastSampledInputMagnitude = 0;
            long lastVisibleMovementInputMagnitude = 0;
            var alignmentFailureSamples = 0;
            var alignmentSettleAttempts = 0;
            ScrollCaptureDirection? alignmentCorrectionDirection = null;
            // Once a movement-cap retry succeeds, the target is a
            // notch-quantizing scroller (editors jump a whole wheel notch,
            // ~150+ rows, against a small per-frame expectation). Keep the
            // resume-sized budget from then on so every later jump does not
            // pay its own stop-retry-resume round — that cycle is what made
            // editor captures visibly stop and go.
            var notchJumpTarget = false;

            void SetState(ControlledScrollCaptureState nextState)
            {
                state = nextState;
                if (resumeAnchorPending)
                {
                    scrollDriver.SetDirection(direction: null);
                    NotifyState(nextState);
                    return;
                }

                var returnDirection = GetControlledReturnDirection(nextState);
                scrollDriver.SetDirection(
                    GetControlledDriveDirection(nextState) ??
                        returnDirection,
                    fastReturn: returnDirection is not null);
                NotifyState(nextState);
            }

            void ApplyPointerAction(ScrollCapturePointerAction action)
            {
                var previousState = state;
                var nextState = ApplyControlledPointerAction(state, action);
                if (nextState == state)
                {
                    return;
                }

                boundaryRecoveryAttempts = 0;

                if (previousState == ControlledScrollCaptureState.WaitingToStart)
                {
                    scrollDriver.ResetDistance();
                    locatedInputMagnitude = 0;
                    lastSampledInputMagnitude = 0;
                    lastVisibleMovementInputMagnitude = 0;
                    unlocatedProgramSteps = 0;
                    legHasVisibleMovement = false;
                    if (nextState == ControlledScrollCaptureState.ScrollingUpFirst)
                    {
                        composer.BeginUpwardExtension(
                            capturedInitialFrame,
                            options);
                    }
                }

                if (BeginsControlledReturnJourney(previousState, nextState))
                {
                    outboundInputMagnitude = scrollDriver.TotalInputMagnitude;
                    var returnDirection = GetControlledReturnDirection(nextState);
                    outboundVisualTravelRows = returnDirection is { } value
                        ? composer.GetTravelFromInitial(value)
                        : 0;
                    outboundHadVisibleMovement = legHasVisibleMovement;
                    returnInputMagnitude = 0;
                    returnSteps = 0;
                    returnStationarySamples = 0;
                    pendingInitialCrossingRows = null;
                    pendingInitialCrossingInputMagnitude = 0;
                    pendingInitialCrossingConfidence = 0;
                    settleStationarySamples = 0;
                    scrollDriver.ResetDistance();
                    locatedInputMagnitude = 0;
                    lastSampledInputMagnitude = 0;
                    lastVisibleMovementInputMagnitude = 0;
                    unlocatedProgramSteps = 0;
                }
                else if (IsControlledSettleState(nextState))
                {
                    settleStationarySamples = 0;
                    alignmentSettleAttempts = 0;
                }

                resumeAnchorPending = IsControlledResumeTransition(
                    previousState,
                    nextState);
                SetState(nextState);
            }

            while (!completionRequested.IsCompleted &&
                   composer.FrameCount < options.MaximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (pointerActions.TryRead(out var queuedAction))
                {
                    ApplyPointerAction(queuedAction);
                }

                if (state is ControlledScrollCaptureState.WaitingToStart or
                    ControlledScrollCaptureState.PausedDown or
                    ControlledScrollCaptureState.PausedReturning or
                    ControlledScrollCaptureState.PausedUp or
                    ControlledScrollCaptureState.BottomReached or
                    ControlledScrollCaptureState.PausedUpFirst or
                    ControlledScrollCaptureState.TopReached or
                    ControlledScrollCaptureState.PausedReturningDown or
                    ControlledScrollCaptureState.PausedDownSecond or
                    ControlledScrollCaptureState.FinalTopReached or
                    ControlledScrollCaptureState.FinalBottomReached or
                    ControlledScrollCaptureState.InputUnavailable)
                {
                    var actionTask = pointerActions.ReadAsync(
                        cancellationToken).AsTask();
                    var resumedBy = await Task.WhenAny(
                        completionRequested,
                        actionTask);
                    if (resumedBy == completionRequested)
                    {
                        break;
                    }

                    ApplyPointerAction(await actionTask);
                    continue;
                }

                if (resumeAnchorPending)
                {
                    resumeAnchorPending = false;
                    stationarySamples = 0;

                    // Smooth scrolling can keep moving while the user pauses.
                    // Re-observe and reconnect the current viewport before
                    // sending another wheel input, otherwise one missed frame
                    // permanently breaks every later overlap.
                    if (GetControlledReturnDirection(state) is not null)
                    {
                        using var returnAnchor = await CaptureControlledFrameAsync(
                            target,
                            scrollDriver,
                            setProgressVisibilityAsync,
                            cancellationToken);
                        previousFingerprint = AutomaticViewportFingerprint.Create(
                            returnAnchor);
                        diagnostics.Record(
                            "controlled-resume-anchor",
                            ("state", state.ToString()),
                            ("attempt", 1),
                            ("located", true),
                            ("added", false));
                        var resumedReturnDirection =
                            GetControlledReturnDirection(state);
                        scrollDriver.SetDirection(
                            resumedReturnDirection,
                            fastReturn: true);
                        continue;
                    }

                    var resumeDirection = GetControlledCaptureDirection(state)
                        ?? throw new InvalidOperationException(
                            "恢复状态没有采集方向。");
                    var resumeLocated = false;
                    for (var attempt = 1;
                         attempt <= ControlledReanchorAttempts;
                         attempt++)
                    {
                        if (attempt > 1)
                        {
                            await Task.Delay(
                                ControlledReanchorDelayMilliseconds,
                                cancellationToken);
                        }

                        using var resumeFrame = await CaptureControlledFrameAsync(
                            target,
                            scrollDriver,
                            setProgressVisibilityAsync,
                            cancellationToken);
                        var resumeFingerprint = AutomaticViewportFingerprint.Create(
                            resumeFrame);
                        var resumeStationary = previousFingerprint
                            .IsStationaryComparedTo(resumeFingerprint);
                        previousFingerprint = resumeFingerprint;
                        var resumeExpectedRows =
                            GetControlledExpectedRowsForDriver(
                                scrollDriver,
                                resumeFrame.Height,
                                locatedInputMagnitude);
                        var resumeMaximumAcceptedRows =
                            GetControlledResumeMaximumMovementRows(
                                resumeFrame.Height,
                                resumeExpectedRows,
                                options.MinimumOverlapRows);
                        // The accumulated driver travel is least trustworthy
                        // exactly here: presentation collapsed several stalled
                        // ticks onto one frame, so the linear estimate vetoed
                        // every pixel-verified reconnect and the capture
                        // paused itself. After the first failed attempt let
                        // pixels decide alone with the full matchable budget;
                        // the duplicate and reflow guards still protect the
                        // result.
                        var resumeAdded = await TryAddControlledFrameAsync(
                            composer,
                            resumeFrame,
                            resumeDirection,
                            options,
                            attempt >= 2 ? null : resumeExpectedRows,
                            cancellationToken,
                            maximumAcceptedNewRows: attempt >= 2
                                ? GetControlledSettleMaximumMovementRows(
                                    resumeFrame.Height,
                                    options.MinimumOverlapRows)
                                : resumeMaximumAcceptedRows,
                            tolerateQuantizedExpectation: notchJumpTarget);
                        if (resumeAdded &&
                            composer.LastFrameMovementRows is { } resumeMovement &&
                            resumeMovement >
                                Math.Max(96, (resumeExpectedRows ?? 0) * 3))
                        {
                            // The viewport jumps a whole wheel notch at once;
                            // keep the larger movement budget from now on so
                            // every later jump does not pay this stop-retry
                            // detour again.
                            notchJumpTarget = true;
                        }

                        resumeLocated = IsControlledFrameLocated(
                            resumeAdded,
                            composer.LastFrameMovementRows,
                            composer.LastRejectReason);
                        diagnostics.Record(
                            "controlled-resume-anchor",
                            ("state", state.ToString()),
                            ("attempt", attempt),
                            ("located", resumeLocated),
                            ("added", resumeAdded),
                            ("stationary", resumeStationary),
                            ("expectedRows", resumeExpectedRows),
                            ("maximumAcceptedRows", resumeMaximumAcceptedRows),
                            ("movementRows", composer.LastFrameMovementRows),
                            ("overlapRows", composer.LastOverlapRows),
                            ("confidence", composer.LastOverlapConfidence),
                            ("horizontalOffset", composer.LastHorizontalOffset),
                            ("reject", composer.LastRejectReason));

                        if (resumeAdded)
                        {
                            await ReportControlledPreviewAsync(
                                composer,
                                previewChanged,
                                cancellationToken);
                        }

                        if (ShouldAdvanceControlledInputAnchor(
                                resumeLocated,
                                composer.LastFrameMovementRows))
                        {
                            locatedInputMagnitude =
                                scrollDriver.TotalInputMagnitude;
                        }

                        if (resumeLocated)
                        {
                            break;
                        }
                    }

                    if (!resumeLocated)
                    {
                        // Never scroll away from an unlocated viewport. Moving
                        // again here used to skip the exact rows the matcher
                        // could not reconnect after a pause.
                        SetState(GetControlledPausedState(state));
                    }
                    else
                    {
                        unlocatedProgramSteps = 0;
                        scrollDriver.SetDirection(resumeDirection);
                    }

                    continue;
                }

                if (IsControlledSettleState(state))
                {
                    var isInitialAlignment = state is
                        ControlledScrollCaptureState.AligningUpwardStart or
                        ControlledScrollCaptureState.AligningDownwardStart;
                    await Task.Delay(
                        ControlledSettleSampleDelayMilliseconds,
                        cancellationToken);
                    using var settleFrame = await CaptureControlledFrameAsync(
                        target,
                        scrollDriver,
                        setProgressVisibilityAsync,
                        cancellationToken);
                    var settleDirection = GetControlledSettleDirection(state);
                    var settleFingerprint = AutomaticViewportFingerprint.Create(
                        settleFrame);
                    var settleStationary = previousFingerprint
                        .IsStationaryComparedTo(settleFingerprint);
                    previousFingerprint = settleFingerprint;
                    // Pause/return settle waits for stillness. Aligning* must
                    // not: chat/AI pages keep the fingerprint jittering, and
                    // the confirming return frame was already thrown away once
                    // — gating here left "正在对齐初始位置" up forever while the
                    // viewport had already scrolled past the start.
                    if (!settleStationary && !isInitialAlignment)
                    {
                        settleStationarySamples = 0;
                        sequence++;
                        diagnostics.Record(
                            "controlled-frame-deferred",
                            ("sequence", sequence),
                            ("state", state.ToString()),
                            ("direction", settleDirection.ToString()),
                            ("reason", "viewport-moving"));
                        continue;
                    }

                    if (isInitialAlignment &&
                        alignmentCorrectionDirection is not null &&
                        !settleStationary)
                    {
                        scrollDriver.SetDirection(direction: null);
                    }

                    // Pause/return settle must not inherit leftover driver travel
                    // as a programmatic expectation: after an unlocated glide the
                    // matcher often finds a near-viewport displacement while the
                    // stopped driver reports only a few rows, and
                    // automatic-expectation-veto then rejects every sample. Pass
                    // null so the stitcher can absorb the inertia strip. Aligning
                    // uses the confirmed crossing once, then the same open cap.
                    var settleExpectedRows = isInitialAlignment
                        ? pendingInitialCrossingRows
                        : null;
                    // The first strip of the opposite leg decides the seam:
                    // demand a decisive match there (repetitive intros admit
                    // near-perfect periodic lookalikes that duplicated whole
                    // header blocks).
                    var settleOptionsForAdd = isInitialAlignment
                        ? options with
                          {
                              MinimumOverlapConfidence = Math.Max(
                                  options.MinimumOverlapConfidence,
                                  0.985),
                          }
                        : options;
                    var settleAdded = await TryAddControlledFrameAsync(
                        composer,
                        settleFrame,
                        settleDirection,
                        settleOptionsForAdd,
                        settleExpectedRows,
                        cancellationToken,
                        maximumAcceptedNewRows:
                            GetControlledSettleMaximumMovementRows(
                                settleFrame.Height,
                                options.MinimumOverlapRows));
                    if (isInitialAlignment && settleAdded)
                    {
                        pendingInitialCrossingRows = null;
                    }

                    var settleLocated = IsControlledFrameLocated(
                        settleAdded,
                        composer.LastFrameMovementRows,
                        composer.LastRejectReason);
                    if (isInitialAlignment)
                    {
                        alignmentSettleAttempts++;
                        if (settleAdded &&
                            composer.LastFrameMovementRows is > 0)
                        {
                            // Already writing content past the initial point.
                            settleStationarySamples = ControlledSettleSamples;
                        }
                        else if (settleStationary &&
                                 IsControlledBoundarySample(
                                     beganUpwardExtension: false,
                                     settleAdded,
                                     settleStationary,
                                     composer.LastFrameMovementRows,
                                     composer.LastRejectReason))
                        {
                            settleStationarySamples++;
                        }
                        else if (!settleLocated)
                        {
                            settleStationarySamples = 0;
                        }
                    }
                    else
                    {
                        // Fingerprint already proved the viewport is still.
                        settleStationarySamples++;
                    }

                    sequence++;
                    diagnostics.Record(
                        "controlled-settle-frame",
                        ("sequence", sequence),
                        ("direction", settleDirection.ToString()),
                        ("located", settleLocated),
                        ("added", settleAdded),
                        ("stationary", settleStationary),
                        ("movementRows", composer.LastFrameMovementRows),
                        ("overlapRows", composer.LastOverlapRows),
                        ("confidence", composer.LastOverlapConfidence),
                        ("horizontalOffset", composer.LastHorizontalOffset),
                        ("reject", composer.LastRejectReason),
                        ("stableSamples", settleStationarySamples),
                        ("alignmentAttempts", alignmentSettleAttempts));

                    if (settleAdded ||
                        composer.LastFrameMovementRows is not null and not 0)
                    {
                        legHasVisibleMovement = true;
                    }

                    if (ShouldAdvanceControlledInputAnchor(
                            settleLocated,
                            composer.LastFrameMovementRows))
                    {
                        locatedInputMagnitude =
                            scrollDriver.TotalInputMagnitude;
                    }

                    if (settleLocated)
                    {
                        alignmentFailureSamples = 0;
                    }
                    else if (isInitialAlignment &&
                             alignmentCorrectionDirection is { } correctionDirection)
                    {
                        alignmentFailureSamples++;
                        if (alignmentFailureSamples <=
                            ControlledAlignmentCorrectionAttempts)
                        {
                            if (alignmentFailureSamples ==
                                ControlledAlignmentCorrectionAttempts / 2)
                            {
                                alignmentCorrectionDirection =
                                    correctionDirection == ScrollCaptureDirection.Up
                                        ? ScrollCaptureDirection.Down
                                        : ScrollCaptureDirection.Up;
                            }

                            scrollDriver.SetDirection(
                                alignmentCorrectionDirection,
                                fastReturn: false);
                        }
                        else
                        {
                            SetState(ControlledScrollCaptureState.InputUnavailable);
                            continue;
                        }
                    }

                    if (settleAdded)
                    {
                        await ReportControlledPreviewAsync(
                            composer,
                            previewChanged,
                            cancellationToken);
                    }

                    var settleReady = isInitialAlignment
                        ? settleStationarySamples >= ControlledSettleSamples ||
                          alignmentSettleAttempts >=
                              ControlledAlignmentMaxSettleAttempts
                        : settleStationarySamples >= ControlledSettleSamples;
                    if (settleReady)
                    {
                        var settledState = GetControlledSettledState(state);
                        if (settledState is
                            ControlledScrollCaptureState.ReturningToStart or
                            ControlledScrollCaptureState.ReturningDownToStart)
                        {
                            outboundHadVisibleMovement =
                                legHasVisibleMovement;
                            outboundInputMagnitude +=
                                scrollDriver.TotalInputMagnitude;
                            scrollDriver.ResetDistance();
                            locatedInputMagnitude = 0;
                            lastSampledInputMagnitude = 0;
                            lastVisibleMovementInputMagnitude = 0;
                            returnInputMagnitude = 0;
                            returnSteps = 0;
                            returnStationarySamples = 0;
                        }

                        stationarySamples = 0;
                        unlocatedProgramSteps = 0;
                        if (isInitialAlignment)
                        {
                            alignmentFailureSamples = 0;
                            alignmentSettleAttempts = 0;
                            alignmentCorrectionDirection = null;
                            pendingInitialCrossingRows = null;
                        }

                        SetState(settledState);
                    }

                    continue;
                }

                var returnDirection = GetControlledReturnDirection(state);
                var isReturning = returnDirection is not null;
                var direction = returnDirection ??
                    GetControlledCaptureDirection(state) ??
                    throw new InvalidOperationException(
                        "活动状态没有滚动方向。");
                if (isReturning &&
                    returnSteps == 0 &&
                    ShouldSkipControlledReturn(
                        outboundHadVisibleMovement,
                        initialFingerprint.IsPreviouslySeenComparedTo(
                            previousFingerprint)))
                {
                    if (direction == ScrollCaptureDirection.Up)
                    {
                        await Task.Run(
                            () => composer.BeginUpwardExtension(
                                capturedInitialFrame,
                                options),
                            cancellationToken);
                        state = ControlledScrollCaptureState.ScrollingUp;
                    }
                    else
                    {
                        state = ControlledScrollCaptureState.ScrollingDownSecond;
                    }

                    diagnostics.Record(
                        "controlled-return-skipped",
                        ("direction", direction.ToString()),
                        ("reason", "initial-viewport-never-left"));
                    stationarySamples = 0;
                    legHasVisibleMovement = false;
                    alignmentFailureSamples = 0;
                    alignmentCorrectionDirection =
                        direction == ScrollCaptureDirection.Up
                            ? ScrollCaptureDirection.Down
                            : ScrollCaptureDirection.Up;
                    scrollDriver.ResetDistance();
                    lastSampledInputMagnitude = 0;
                    lastVisibleMovementInputMagnitude = 0;
                    NotifyState(state);
                    scrollDriver.SetDirection(direction);
                    continue;
                }

                if (isReturning)
                {
                    returnSteps++;
                    returnInputMagnitude = scrollDriver.TotalInputMagnitude;
                    var slowReturnThreshold = Math.Max(
                        0,
                        outboundInputMagnitude -
                        (target.CaptureRegion.Height * 3L) / 4L);
                    if (returnInputMagnitude >= slowReturnThreshold &&
                        pendingInitialCrossingRows is null)
                    {
                        // The first part of a return journey can be fast. Near
                        // the initial viewport, switch to the fine capture
                        // cadence so one sample cannot jump beyond the region
                        // that still overlaps the initial frame.
                        scrollDriver.SetDirection(direction, fastReturn: false);
                    }
                }

                var stepDelay = isReturning
                    ? ControlledSettleSampleDelayMilliseconds
                    : ControlledCaptureSampleDelayMilliseconds;
                await Task.Delay(
                    stepDelay,
                    cancellationToken);
                if (scrollDriver.HasInputFailure)
                {
                    diagnostics.Record(
                        "controlled-input-failed",
                        ("state", state.ToString()),
                        ("stage", scrollDriver.InputFailureStage),
                        ("errorCode", scrollDriver.InputFailureCode));
                    SetState(ControlledScrollCaptureState.InputUnavailable);
                    continue;
                }

                using var frame = await CaptureControlledFrameAsync(
                    target,
                    scrollDriver,
                    setProgressVisibilityAsync,
                    cancellationToken);
                var sampledInputMagnitude = scrollDriver.TotalInputMagnitude;
                if (isReturning)
                {
                    returnInputMagnitude = sampledInputMagnitude;
                }
                var inputAdvancedSincePreviousSample =
                    sampledInputMagnitude > lastSampledInputMagnitude;
                lastSampledInputMagnitude = sampledInputMagnitude;
                var fingerprint = AutomaticViewportFingerprint.Create(frame);
                var isStationary = previousFingerprint
                    .IsStationaryComparedTo(fingerprint);
                previousFingerprint = fingerprint;
                returnStationarySamples = isReturning && isStationary
                    ? returnStationarySamples + 1
                    : 0;

                var processingTimestamp = Stopwatch.GetTimestamp();
                var expectedRows = GetControlledExpectedRowsForDriver(
                    scrollDriver,
                    frame.Height,
                    locatedInputMagnitude);
                bool added;
                var initialReached = false;
                var initialCrossingConfirmed = false;
                ImageOverlapMatch? initialOverlap = null;
                if (isReturning)
                {
                    var minimumEvidenceMagnitude =
                        GetControlledMinimumReturnMagnitude(
                            outboundInputMagnitude);
                    var isStrictInitialViewport = initialFingerprint
                        .IsSimilarTo(fingerprint);
                    var isLooseInitialViewport = initialFingerprint
                        .IsPreviouslySeenComparedTo(fingerprint);
                    initialReached = returnInputMagnitude >=
                            minimumEvidenceMagnitude &&
                        IsControlledInitialViewportReached(
                            isStrictInitialViewport,
                            isLooseInitialViewport,
                            returnStationarySamples);
                    var crossedInitial = initialReached;
                    if (initialReached)
                    {
                        pendingInitialCrossingRows = null;
                        pendingInitialCrossingInputMagnitude = 0;
                        pendingInitialCrossingConfidence = 0;
                    }
                    else if (returnInputMagnitude >= outboundInputMagnitude)
                    {
                        var expectedCrossingRows = (int)Math.Clamp(
                            GetControlledExpectedCrossingRows(
                                returnInputMagnitude,
                                outboundInputMagnitude,
                                outboundVisualTravelRows),
                            1,
                            Math.Max(
                                1,
                                frame.Height - options.MinimumOverlapRows));
                        initialOverlap = await Task.Run(
                            () => FindControlledInitialOverlap(
                                capturedInitialFrame,
                                frame,
                                direction,
                                expectedCrossingRows,
                                options),
                            cancellationToken);
                        if (initialOverlap is null)
                        {
                            // Editors glide a full viewport between samples.
                            // A decisive crossing measured on the previous
                            // sample followed by a vanished overlap band means
                            // the page has certainly moved past the start —
                            // clearing the evidence here made the driver
                            // scroll away from the document forever.
                            if (pendingInitialCrossingRows is > 0 &&
                                pendingInitialCrossingConfidence >=
                                    ControlledInputReturnMinimumConfidence)
                            {
                                initialCrossingConfirmed = true;
                                crossedInitial = true;
                            }
                            else
                            {
                                pendingInitialCrossingRows = null;
                                pendingInitialCrossingInputMagnitude = 0;
                                pendingInitialCrossingConfidence = 0;
                            }
                        }
                        else
                        {
                            var crossingRows = frame.Height -
                                initialOverlap.OverlapRows;
                            var inputReturnIsDecisive =
                                Math.Abs(
                                    returnInputMagnitude -
                                    outboundInputMagnitude) <=
                                    Math.Max(64, frame.Height / 6) &&
                                crossingRows <= Math.Max(64, frame.Height / 4) &&
                                initialOverlap.Confidence >=
                                    ControlledInputReturnMinimumConfidence;
                            initialCrossingConfirmed =
                                inputReturnIsDecisive ||
                                pendingInitialCrossingRows is { } previousRows &&
                                    (IsControlledInitialCrossingConsistent(
                                         previousRows,
                                         pendingInitialCrossingInputMagnitude,
                                         crossingRows,
                                         returnInputMagnitude) ||
                                     IsControlledInitialCrossingStable(
                                         previousRows,
                                         pendingInitialCrossingInputMagnitude,
                                         crossingRows,
                                         returnInputMagnitude,
                                         initialOverlap.Confidence) ||
                                     IsControlledInitialCrossingGlide(
                                         previousRows,
                                         pendingInitialCrossingConfidence,
                                         crossingRows,
                                         initialOverlap.Confidence));
                            crossedInitial = initialCrossingConfirmed;
                            pendingInitialCrossingRows = crossingRows;
                            pendingInitialCrossingInputMagnitude =
                                returnInputMagnitude;
                            pendingInitialCrossingConfidence =
                                initialOverlap.Confidence;
                            // Once a frame has a decisive overlap with the
                            // initial viewport, stop the return driver while
                            // waiting for the confirming sample. Continuing to
                            // inject wheel messages here caused the first
                            // confirming frame to overshoot the initial point
                            // by hundreds of rows; the upward leg then stitched
                            // that overshoot as duplicated content.
                            scrollDriver.SetDirection(direction: null);
                        }
                    }
                    else
                    {
                        pendingInitialCrossingRows = null;
                        pendingInitialCrossingInputMagnitude = 0;
                        pendingInitialCrossingConfidence = 0;
                    }

                    diagnostics.Record(
                        "controlled-return-frame",
                        ("sequence", sequence + 1),
                        ("returnStep", returnSteps),
                        ("returnMode", "fixed-wheel-message"),
                        ("outboundInputMagnitude", outboundInputMagnitude),
                        ("outboundVisualTravelRows", outboundVisualTravelRows),
                        ("returnInputMagnitude", returnInputMagnitude),
                        ("minimumEvidenceMagnitude", minimumEvidenceMagnitude),
                        ("strictInitialViewport", isStrictInitialViewport),
                        ("looseInitialViewport", isLooseInitialViewport),
                        ("returnStationarySamples", returnStationarySamples),
                        ("initialReached", initialReached),
                        ("crossedInitial", crossedInitial),
                        ("initialCrossingConfirmed", initialCrossingConfirmed),
                        ("initialOverlapRows", initialOverlap?.OverlapRows),
                        ("initialOverlapConfidence", initialOverlap?.Confidence));

                    if (!crossedInitial)
                    {
                        sequence++;
                        continue;
                    }

                    if (direction == ScrollCaptureDirection.Up)
                    {
                        await Task.Run(
                            () => composer.BeginUpwardExtension(
                                capturedInitialFrame,
                                options),
                            cancellationToken);
                    }

                    // The overlap that confirmed the crossing is the first
                    // strip past the initial viewport. Seeding it here keeps
                    // that content (and the logical anchor) instead of waiting
                    // for Aligning settle while inertia scrolls further away.
                    // The very first strip of the opposite leg decides the
                    // seam against everything captured so far, and repetitive
                    // intros admit periodic lookalikes — demand a decisive
                    // match and cap the strip near the measured crossing, or
                    // the whole page header gets prepended twice.
                    var seededCrossing = false;
                    if (pendingInitialCrossingRows is { } confirmedCrossingRows &&
                        confirmedCrossingRows > 0)
                    {
                        seededCrossing = await TryAddControlledFrameAsync(
                            composer,
                            frame,
                            direction == ScrollCaptureDirection.Up
                                ? ScrollCaptureDirection.Up
                                : ScrollCaptureDirection.Down,
                            options with
                            {
                                MinimumOverlapConfidence = Math.Max(
                                    options.MinimumOverlapConfidence,
                                    0.985),
                            },
                            confirmedCrossingRows,
                            cancellationToken,
                            maximumAcceptedNewRows: Math.Min(
                                frame.Height - options.MinimumOverlapRows,
                                confirmedCrossingRows + 96));
                        if (seededCrossing)
                        {
                            pendingInitialCrossingRows = null;
                            await ReportControlledPreviewAsync(
                                composer,
                                previewChanged,
                                cancellationToken);
                        }
                    }

                    legHasVisibleMovement = seededCrossing;
                    scrollDriver.ResetDistance();
                    locatedInputMagnitude = 0;
                    lastSampledInputMagnitude = 0;
                    lastVisibleMovementInputMagnitude = 0;
                    unlocatedProgramSteps = 0;
                    stationarySamples = 0;
                    settleStationarySamples = 0;
                    alignmentSettleAttempts = 0;

                    // Always settle through Aligning before the capture leg.
                    // Starting the wheel immediately after a seeded crossing
                    // looked attractive, but the page is often still gliding
                    // there; the leg then opened with unlocatable frames and
                    // auto-paused itself within seconds. Aligning exits after
                    // one located moving add, two stationary samples, or the
                    // attempt cap — it is cheap when the page is truly ready.
                    state = direction == ScrollCaptureDirection.Up
                        ? ControlledScrollCaptureState.AligningUpwardStart
                        : ControlledScrollCaptureState.AligningDownwardStart;
                    alignmentCorrectionDirection = seededCrossing
                        ? null
                        // Nudge hunting if the seed could not connect; without
                        // this Aligning failures never started correction.
                        : direction == ScrollCaptureDirection.Up
                            ? ScrollCaptureDirection.Down
                            : ScrollCaptureDirection.Up;

                    SetState(state);
                    sequence++;
                    continue;
                }
                else
                {
                    added = await TryAddControlledFrameAsync(
                        composer,
                        frame,
                        direction,
                        options,
                        expectedRows,
                        cancellationToken,
                        maximumAcceptedNewRows: notchJumpTarget
                            ? GetControlledResumeMaximumMovementRows(
                                frame.Height,
                                expectedRows,
                                options.MinimumOverlapRows)
                            : null,
                        tolerateQuantizedExpectation: notchJumpTarget);
                }

                var frameLocated = IsControlledFrameLocated(
                        added,
                        composer.LastFrameMovementRows,
                        composer.LastRejectReason);
                if (!frameLocated)
                {
                    scrollDriver.SetDirection(direction: null);
                    var wasMovementCapVeto =
                        composer.LastRejectReason == "movement-cap-veto";
                    var retryMaximumAcceptedRows =
                        GetControlledRetryMaximumMovementRows(
                            composer.LastRejectReason,
                            frame.Height,
                            expectedRows,
                            options.MinimumOverlapRows);
                    // Do not issue another wheel step after an unlocated
                    // viewport. Let animations settle and retry this exact
                    // position so a single torn frame cannot create a gap. A
                    // clean movement-cap rejection gets one bounded re-anchor
                    // allowance while the driver is stopped; otherwise a real
                    // compositor jump can repeat the same rejection three
                    // times and pause automatic scrolling by itself.
                    for (var attempt = 1;
                         attempt <= ControlledReanchorAttempts;
                         attempt++)
                    {
                        await Task.Delay(
                            ControlledReanchorDelayMilliseconds,
                            cancellationToken);
                        using var retryFrame = await CaptureControlledFrameAsync(
                            target,
                            scrollDriver,
                            setProgressVisibilityAsync,
                            cancellationToken);
                        var retryFingerprint = AutomaticViewportFingerprint.Create(
                            retryFrame);
                        isStationary = previousFingerprint
                            .IsStationaryComparedTo(retryFingerprint);
                        previousFingerprint = retryFingerprint;
                        // The driver keeps injecting while matching runs, so a
                        // capped rejection often only means the accumulated
                        // jump outgrew the resume budget too. Escalate to the
                        // full matchable range after the first failed retry —
                        // duplicate and expectation guards still apply.
                        var escalatedMaximumRows = wasMovementCapVeto &&
                            attempt >= 2
                            ? GetControlledSettleMaximumMovementRows(
                                retryFrame.Height,
                                options.MinimumOverlapRows)
                            : retryMaximumAcceptedRows;
                        var retryAdded = await TryAddControlledFrameAsync(
                            composer,
                            retryFrame,
                            direction,
                            options,
                            expectedRows,
                            cancellationToken,
                            escalatedMaximumRows,
                            tolerateQuantizedExpectation: notchJumpTarget ||
                                wasMovementCapVeto);
                        added |= retryAdded;
                        frameLocated = IsControlledFrameLocated(
                            retryAdded,
                            composer.LastFrameMovementRows,
                            composer.LastRejectReason);
                        diagnostics.Record(
                            "controlled-frame-retry",
                            ("sequence", sequence + 1),
                            ("direction", direction.ToString()),
                            ("attempt", attempt),
                            ("maximumAcceptedRows",
                                retryMaximumAcceptedRows),
                            ("located", frameLocated),
                            ("added", retryAdded),
                            ("stationary", isStationary),
                            ("movementRows", composer.LastFrameMovementRows),
                            ("overlapRows", composer.LastOverlapRows),
                            ("confidence", composer.LastOverlapConfidence),
                            ("horizontalOffset", composer.LastHorizontalOffset),
                            ("reject", composer.LastRejectReason));

                        if (frameLocated)
                        {
                            if (wasMovementCapVeto && retryAdded)
                            {
                                notchJumpTarget = true;
                            }

                            locatedInputMagnitude =
                                scrollDriver.TotalInputMagnitude;
                            // Matching pauses the driver so a torn frame cannot
                            // move farther away from the last known viewport.
                            // Once the retry reconnects, resume immediately;
                            // leaving it stopped makes three stationary frames
                            // look exactly like a physical scroll boundary.
                            scrollDriver.SetDirection(direction);
                            break;
                        }
                    }

                }

                if (frameLocated)
                {
                    unlocatedProgramSteps = 0;
                    // The wheel keeps advancing while image matching runs.
                    // Anchor the accepted bitmap to the input distance at
                    // capture time, not the later distance after matching;
                    // otherwise that unobserved travel is discarded from the
                    // next expected displacement and periodic rows can win at
                    // a shorter, incorrect overlap. A visually stationary
                    // frame is located, but it has not presented the new wheel
                    // input yet; advancing the input anchor there loses that
                    // pending travel and makes the next real movement hit a
                    // spuriously small movement cap.
                    if (ShouldAdvanceControlledInputAnchor(
                            frameLocated,
                            composer.LastFrameMovementRows))
                    {
                        locatedInputMagnitude = sampledInputMagnitude;
                    }
                }
                sequence++;
                if (added ||
                    composer.LastFrameMovementRows is not null and not 0)
                {
                    legHasVisibleMovement = true;
                    lastVisibleMovementInputMagnitude = sampledInputMagnitude;
                    boundaryRecoveryAttempts = 0;
                }
                var inputTravelSinceVisibleMovement = Math.Max(
                    0,
                    sampledInputMagnitude -
                        lastVisibleMovementInputMagnitude);
                diagnostics.Record(
                    "controlled-frame-processed",
                    ("sequence", sequence),
                    ("direction", direction.ToString()),
                    ("durationMs", Stopwatch.GetElapsedTime(
                        processingTimestamp).TotalMilliseconds),
                    ("added", added),
                    ("stationary", isStationary),
                    ("expectedRows", expectedRows),
                    ("preferredRows", composer.LastPreferredExpectedRows),
                    ("movementRows", composer.LastFrameMovementRows),
                    ("temporalUndershootRows",
                        composer.LastTemporalUndershootRows),
                    ("temporalReplacementRows",
                        composer.LastTemporalReplacementRows),
                    ("overlapRows", composer.LastOverlapRows),
                    ("confidence", composer.LastOverlapConfidence),
                    ("horizontalOffset", composer.LastHorizontalOffset),
                    ("reject", composer.LastRejectReason),
                    ("inputMode", "fixed-wheel-message"),
                    ("inputMagnitude", sampledInputMagnitude),
                    ("inputStepCount", scrollDriver.InputStepCount),
                    ("inputStepsSincePreviousFrame",
                        scrollDriver.LastCapturedInputStepCount),
                    ("backpressureWaitTicks",
                        scrollDriver.LastCapturedBackpressureWaitTicks),
                    ("inputAdvanced", inputAdvancedSincePreviousSample),
                    ("inputTravelSinceMovement",
                        inputTravelSinceVisibleMovement),
                    ("fixedBottomRows", composer.FixedBottomRows),
                    ("frameCount", composer.FrameCount),
                    ("outputHeight", composer.OutputHeight));

                var canConfirmBoundary = CanConfirmControlledBoundary(
                    legHasVisibleMovement,
                    inputAdvancedSincePreviousSample,
                    inputTravelSinceVisibleMovement);
                var boundaryStationary = canConfirmBoundary &&
                    IsControlledBoundarySample(
                        beganUpwardExtension: false,
                        added,
                        isStationary,
                        composer.LastFrameMovementRows,
                        composer.LastRejectReason);
                stationarySamples = boundaryStationary
                    ? stationarySamples + 1
                    : 0;
                if (added)
                {
                    await ReportControlledPreviewAsync(
                        composer,
                        previewChanged,
                        cancellationToken);
                }

                if (!frameLocated)
                {
                    unlocatedProgramSteps++;
                    if (unlocatedProgramSteps <=
                        ControlledAutomaticReanchorRounds)
                    {
                        // The quick in-place retries above reuse the original
                        // sample's expectation and its small movement cap, so
                        // leftover return-leg inertia at the start of a new
                        // capture leg fails all three and used to pause the
                        // capture by itself. The dedicated resume re-anchor
                        // (wider allowance, boundary-aware) reconnects that
                        // exact situation when the user resumes manually —
                        // run it automatically first, and only surrender to a
                        // pause when even that cannot locate the viewport.
                        diagnostics.Record(
                            "controlled-unlocated-reanchor",
                            ("state", state.ToString()),
                            ("direction", direction.ToString()),
                            ("unlocatedSteps", unlocatedProgramSteps));
                        resumeAnchorPending = true;
                        stationarySamples = 0;
                        continue;
                    }

                    diagnostics.Record(
                        "controlled-unlocated-paused",
                        ("state", state.ToString()),
                        ("direction", direction.ToString()),
                        ("unlocatedSteps", unlocatedProgramSteps));
                    SetState(GetControlledPausedState(state));
                    stationarySamples = 0;
                    continue;
                }

                if (canConfirmBoundary &&
                    stationarySamples >= ControlledScrollBoundarySamples &&
                    boundaryRecoveryAttempts <
                        ControlledBoundaryRecoveryAttempts)
                {
                    boundaryRecoveryAttempts++;
                    stationarySamples = 0;
                    lastVisibleMovementInputMagnitude = sampledInputMagnitude;
                    scrollDriver.RequestBoundaryProbe();
                    diagnostics.Record(
                        "controlled-boundary-recovery",
                        ("state", state.ToString()),
                        ("direction", direction.ToString()),
                        ("attempt", boundaryRecoveryAttempts),
                        ("inputStepCount", scrollDriver.InputStepCount),
                        ("inputMagnitude", sampledInputMagnitude));
                    continue;
                }

                if (canConfirmBoundary &&
                    direction == ScrollCaptureDirection.Down &&
                    stationarySamples >= ControlledScrollBoundarySamples)
                {
                    composer.MarkDownBoundaryReached();
                    stationarySamples = 0;
                    if (state == ControlledScrollCaptureState.ScrollingDownSecond)
                    {
                        SetState(ControlledScrollCaptureState.FinalBottomReached);
                        continue;
                    }

                    SetState(ControlledScrollCaptureState.BottomReached);
                    continue;
                }

                if (canConfirmBoundary &&
                    direction == ScrollCaptureDirection.Up &&
                    stationarySamples >= ControlledScrollBoundarySamples)
                {
                    stationarySamples = 0;
                    if (state == ControlledScrollCaptureState.ScrollingUp)
                    {
                        SetState(ControlledScrollCaptureState.FinalTopReached);
                        continue;
                    }

                    SetState(ControlledScrollCaptureState.TopReached);
                    continue;
                }

            }

            var finalSettleDirection = composer.FrameCount <
                    options.MaximumFrames
                ? GetControlledCaptureDirection(state)
                : null;
            SetState(ControlledScrollCaptureState.Completing);
            if (finalSettleDirection is { } completionDirection)
            {
                var completionStationarySamples = 0;
                for (var attempt = 1;
                     attempt <= ControlledCompletionSettleAttempts;
                     attempt++)
                {
                    await Task.Delay(
                        ControlledSettleSampleDelayMilliseconds,
                        cancellationToken);
                    using var completionFrame = await CaptureControlledFrameAsync(
                        target,
                        scrollDriver,
                        setProgressVisibilityAsync,
                        cancellationToken);
                    var completionFingerprint = AutomaticViewportFingerprint.Create(
                        completionFrame);
                    var completionStationary = previousFingerprint
                        .IsStationaryComparedTo(completionFingerprint);
                    previousFingerprint = completionFingerprint;
                    var completionAdded = await TryAddControlledFrameAsync(
                        composer,
                        completionFrame,
                        completionDirection,
                        options,
                        GetControlledExpectedRowsForDriver(
                            scrollDriver,
                            completionFrame.Height,
                            locatedInputMagnitude),
                        cancellationToken);
                    var completionSettled = IsControlledBoundarySample(
                        beganUpwardExtension: false,
                        completionAdded,
                        completionStationary,
                        composer.LastFrameMovementRows,
                        composer.LastRejectReason);
                    completionStationarySamples = completionSettled
                        ? completionStationarySamples + 1
                        : 0;
                    diagnostics.Record(
                        "controlled-completion-settle-frame",
                        ("attempt", attempt),
                        ("direction", completionDirection.ToString()),
                        ("added", completionAdded),
                        ("stationary", completionStationary),
                        ("movementRows", composer.LastFrameMovementRows),
                        ("overlapRows", composer.LastOverlapRows),
                        ("confidence", composer.LastOverlapConfidence),
                        ("horizontalOffset", composer.LastHorizontalOffset),
                        ("reject", composer.LastRejectReason),
                        ("stableSamples", completionStationarySamples));

                    if (completionStationarySamples >=
                        ControlledSettleSamples)
                    {
                        break;
                    }
                }
            }

            return composer.FrameCount < 1
                ? ScrollCaptureResult.Failure("滚动截图失败。")
                : await ComposeControlledResultAsync(
                    composer,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScrollCaptureResult.Failure("长截图已取消。");
        }
        catch (Exception exception)
        {
            diagnostics.Record(
                "controlled-capture-exception",
                ("type", exception.GetType().FullName),
                ("message", exception.Message),
                ("stackTrace", exception.StackTrace));
            return ScrollCaptureResult.Failure("滚动截图失败。");
        }
        finally
        {
            diagnostics.FlushInBackground();
            if (hasOriginalCursorPosition)
            {
                ForegroundWindowCaptureService.RestoreCursorPosition(
                    originalCursorPosition);
            }
        }
    }

    internal static ControlledScrollCaptureState ApplyControlledPointerAction(
        ControlledScrollCaptureState state,
        ScrollCapturePointerAction action)
    {
        if (action == ScrollCapturePointerAction.DoubleClick)
        {
            return state switch
            {
                ControlledScrollCaptureState.ScrollingDown =>
                    ControlledScrollCaptureState.PreparingReturnFromDown,
                ControlledScrollCaptureState.PreparingPauseDown =>
                    ControlledScrollCaptureState.PreparingReturnFromDown,
                ControlledScrollCaptureState.PausedDown or
                ControlledScrollCaptureState.BottomReached =>
                    ControlledScrollCaptureState.ReturningToStart,
                ControlledScrollCaptureState.WaitingToStart =>
                    ControlledScrollCaptureState.ScrollingUpFirst,
                ControlledScrollCaptureState.ScrollingUpFirst =>
                    ControlledScrollCaptureState.PreparingReturnFromUp,
                ControlledScrollCaptureState.PreparingPauseUpFirst =>
                    ControlledScrollCaptureState.PreparingReturnFromUp,
                ControlledScrollCaptureState.PausedUpFirst or
                ControlledScrollCaptureState.TopReached =>
                    ControlledScrollCaptureState.ReturningDownToStart,
                ControlledScrollCaptureState.PreparingPauseUp or
                ControlledScrollCaptureState.PausedUp =>
                    ControlledScrollCaptureState.ScrollingUp,
                ControlledScrollCaptureState.PreparingPauseDownSecond or
                ControlledScrollCaptureState.PausedDownSecond =>
                    ControlledScrollCaptureState.ScrollingDownSecond,
                _ => state,
            };
        }

        return state switch
        {
            ControlledScrollCaptureState.WaitingToStart =>
                ControlledScrollCaptureState.ScrollingDown,
            ControlledScrollCaptureState.ScrollingDown =>
                ControlledScrollCaptureState.PreparingPauseDown,
            ControlledScrollCaptureState.PreparingPauseDown =>
                ControlledScrollCaptureState.ScrollingDown,
            ControlledScrollCaptureState.PausedDown =>
                ControlledScrollCaptureState.ScrollingDown,
            ControlledScrollCaptureState.ReturningToStart =>
                ControlledScrollCaptureState.PausedReturning,
            ControlledScrollCaptureState.PausedReturning =>
                ControlledScrollCaptureState.ReturningToStart,
            ControlledScrollCaptureState.ScrollingUp =>
                ControlledScrollCaptureState.PreparingPauseUp,
            ControlledScrollCaptureState.PreparingPauseUp =>
                ControlledScrollCaptureState.ScrollingUp,
            ControlledScrollCaptureState.PausedUp =>
                ControlledScrollCaptureState.ScrollingUp,
            ControlledScrollCaptureState.ScrollingUpFirst =>
                ControlledScrollCaptureState.PreparingPauseUpFirst,
            ControlledScrollCaptureState.PreparingPauseUpFirst =>
                ControlledScrollCaptureState.ScrollingUpFirst,
            ControlledScrollCaptureState.PausedUpFirst =>
                ControlledScrollCaptureState.ScrollingUpFirst,
            ControlledScrollCaptureState.ReturningDownToStart =>
                ControlledScrollCaptureState.PausedReturningDown,
            ControlledScrollCaptureState.PausedReturningDown =>
                ControlledScrollCaptureState.ReturningDownToStart,
            ControlledScrollCaptureState.ScrollingDownSecond =>
                ControlledScrollCaptureState.PreparingPauseDownSecond,
            ControlledScrollCaptureState.PreparingPauseDownSecond =>
                ControlledScrollCaptureState.ScrollingDownSecond,
            ControlledScrollCaptureState.PausedDownSecond =>
                ControlledScrollCaptureState.ScrollingDownSecond,
            _ => state,
        };
    }

    /// <summary>
    /// Whether a single click must wait out the double-click interval before
    /// it is delivered. In idle states a click starts motion while a double
    /// click starts a different motion, so acting on the first half would
    /// briefly drive the wrong way. In motion states a click means "stop":
    /// the state machine maps a following double-click from the resulting
    /// pause-preparing state to the same action it has from the motion state,
    /// so the click can be delivered immediately and pausing feels instant.
    /// </summary>
    internal static bool ShouldDeferControlledPointerClicks(
        ControlledScrollCaptureState state)
    {
        return state is
            ControlledScrollCaptureState.WaitingToStart or
            ControlledScrollCaptureState.PausedDown or
            ControlledScrollCaptureState.PausedReturning or
            ControlledScrollCaptureState.PausedUp or
            ControlledScrollCaptureState.PausedUpFirst or
            ControlledScrollCaptureState.PausedReturningDown or
            ControlledScrollCaptureState.PausedDownSecond or
            ControlledScrollCaptureState.BottomReached or
            ControlledScrollCaptureState.TopReached or
            ControlledScrollCaptureState.FinalTopReached or
            ControlledScrollCaptureState.FinalBottomReached or
            ControlledScrollCaptureState.InputUnavailable or
            ControlledScrollCaptureState.Completing;
    }

    internal static bool IsControlledResumeTransition(
        ControlledScrollCaptureState previousState,
        ControlledScrollCaptureState currentState)
    {
        var wasPaused = previousState is
            ControlledScrollCaptureState.PausedDown or
            ControlledScrollCaptureState.PausedReturning or
            ControlledScrollCaptureState.PausedUp or
            ControlledScrollCaptureState.PausedUpFirst or
            ControlledScrollCaptureState.PausedReturningDown or
            ControlledScrollCaptureState.PausedDownSecond;
        var isActive = currentState is
            ControlledScrollCaptureState.ScrollingDown or
            ControlledScrollCaptureState.ReturningToStart or
            ControlledScrollCaptureState.ScrollingUp or
            ControlledScrollCaptureState.ScrollingUpFirst or
            ControlledScrollCaptureState.ReturningDownToStart or
            ControlledScrollCaptureState.ScrollingDownSecond;
        return wasPaused && isActive;
    }

    internal static ScrollCaptureDirection? GetControlledCaptureDirection(
        ControlledScrollCaptureState state)
    {
        return state switch
        {
            ControlledScrollCaptureState.ScrollingDown or
            ControlledScrollCaptureState.ScrollingDownSecond =>
                ScrollCaptureDirection.Down,
            ControlledScrollCaptureState.ScrollingUp or
            ControlledScrollCaptureState.ScrollingUpFirst =>
                ScrollCaptureDirection.Up,
            _ => null,
        };
    }

    internal static ScrollCaptureDirection? GetControlledReturnDirection(
        ControlledScrollCaptureState state)
    {
        return state switch
        {
            ControlledScrollCaptureState.ReturningToStart =>
                ScrollCaptureDirection.Up,
            ControlledScrollCaptureState.ReturningDownToStart =>
                ScrollCaptureDirection.Down,
            _ => null,
        };
    }

    internal static ScrollCaptureDirection? GetControlledDriveDirection(
        ControlledScrollCaptureState state)
    {
        return GetControlledCaptureDirection(state);
    }

    internal static ControlledScrollCaptureState GetControlledPausedState(
        ControlledScrollCaptureState state)
    {
        return state switch
        {
            ControlledScrollCaptureState.ScrollingDown =>
                ControlledScrollCaptureState.PausedDown,
            ControlledScrollCaptureState.ScrollingUp =>
                ControlledScrollCaptureState.PausedUp,
            ControlledScrollCaptureState.ScrollingUpFirst =>
                ControlledScrollCaptureState.PausedUpFirst,
            ControlledScrollCaptureState.ScrollingDownSecond =>
                ControlledScrollCaptureState.PausedDownSecond,
            _ => throw new InvalidOperationException(
                "当前状态不是活动采集状态。"),
        };
    }

    internal static bool IsControlledSettleState(
        ControlledScrollCaptureState state)
    {
        return state is
            ControlledScrollCaptureState.PreparingPauseDown or
            ControlledScrollCaptureState.PreparingReturnFromDown or
            ControlledScrollCaptureState.AligningUpwardStart or
            ControlledScrollCaptureState.PreparingPauseUp or
            ControlledScrollCaptureState.PreparingPauseUpFirst or
            ControlledScrollCaptureState.PreparingReturnFromUp or
            ControlledScrollCaptureState.AligningDownwardStart or
            ControlledScrollCaptureState.PreparingPauseDownSecond;
    }

    internal static ScrollCaptureDirection GetControlledSettleDirection(
        ControlledScrollCaptureState state)
    {
        return state switch
        {
            ControlledScrollCaptureState.PreparingPauseDown or
            ControlledScrollCaptureState.PreparingReturnFromDown or
            ControlledScrollCaptureState.AligningDownwardStart or
            ControlledScrollCaptureState.PreparingPauseDownSecond =>
                ScrollCaptureDirection.Down,
            ControlledScrollCaptureState.PreparingPauseUp or
            ControlledScrollCaptureState.PreparingPauseUpFirst or
            ControlledScrollCaptureState.PreparingReturnFromUp or
            ControlledScrollCaptureState.AligningUpwardStart =>
                ScrollCaptureDirection.Up,
            _ => throw new InvalidOperationException(
                "当前状态不需要停稳采样。"),
        };
    }

    internal static ControlledScrollCaptureState GetControlledSettledState(
        ControlledScrollCaptureState state)
    {
        return state switch
        {
            ControlledScrollCaptureState.PreparingPauseDown =>
                ControlledScrollCaptureState.PausedDown,
            ControlledScrollCaptureState.PreparingReturnFromDown =>
                ControlledScrollCaptureState.ReturningToStart,
            ControlledScrollCaptureState.AligningUpwardStart =>
                ControlledScrollCaptureState.ScrollingUp,
            ControlledScrollCaptureState.PreparingPauseUp =>
                ControlledScrollCaptureState.PausedUp,
            ControlledScrollCaptureState.PreparingPauseUpFirst =>
                ControlledScrollCaptureState.PausedUpFirst,
            ControlledScrollCaptureState.PreparingReturnFromUp =>
                ControlledScrollCaptureState.ReturningDownToStart,
            ControlledScrollCaptureState.AligningDownwardStart =>
                ControlledScrollCaptureState.ScrollingDownSecond,
            ControlledScrollCaptureState.PreparingPauseDownSecond =>
                ControlledScrollCaptureState.PausedDownSecond,
            _ => throw new InvalidOperationException(
                "当前状态不需要停稳采样。"),
        };
    }

    internal static long GetControlledMinimumReturnMagnitude(
        long outboundInputMagnitude)
    {
        return Math.Max(
            2,
            outboundInputMagnitude - ControlledReturnInputOvershootPixels);
    }

    internal static bool BeginsControlledReturnJourney(
        ControlledScrollCaptureState previousState,
        ControlledScrollCaptureState currentState)
    {
        return currentState is
                ControlledScrollCaptureState.PreparingReturnFromDown or
                ControlledScrollCaptureState.PreparingReturnFromUp ||
            currentState == ControlledScrollCaptureState.ReturningToStart &&
                previousState is
                    ControlledScrollCaptureState.PausedDown or
                    ControlledScrollCaptureState.BottomReached ||
            currentState == ControlledScrollCaptureState.ReturningDownToStart &&
                previousState is
                    ControlledScrollCaptureState.PausedUpFirst or
                    ControlledScrollCaptureState.TopReached;
    }

    internal static bool CanConfirmControlledBoundary(
        bool legHasVisibleMovement,
        bool inputAdvancedSincePreviousSample,
        long inputTravelSinceVisibleMovement)
    {
        return legHasVisibleMovement &&
            inputAdvancedSincePreviousSample &&
            inputTravelSinceVisibleMovement >=
                ControlledBoundaryConfirmationTravelPixels;
    }

    internal static bool ShouldSkipControlledReturn(
        bool outboundHadVisibleMovement,
        bool isInitialViewport)
    {
        return !outboundHadVisibleMovement && isInitialViewport;
    }

    internal static bool IsControlledInitialViewportReached(
        bool isStrictInitialViewport,
        bool isLooseInitialViewport,
        int stationarySamples)
    {
        // A texture-weighted return fingerprint intentionally tolerates live
        // values and hover changes. On sparse pages that tolerance can also
        // make a nearby viewport look like the initial one, especially when
        // there is no unique left-side marker such as a code line number.
        // While the viewport is moving, only the pixel-strict signature may
        // end the return leg. The tolerant signature is a physical-boundary
        // fallback and therefore needs consecutive stationary observations.
        return isStrictInitialViewport ||
            isLooseInitialViewport &&
            stationarySamples >= ControlledInitialBoundaryStationarySamples;
    }

    private static int? GetControlledExpectedRowsForDriver(
        ControlledScrollDriver scrollDriver,
        int frameHeight,
        long locatedInputMagnitude)
    {
        return GetControlledExpectedInputRows(
            frameHeight,
            Math.Max(
                0,
                scrollDriver.TotalInputMagnitude - locatedInputMagnitude));
    }

    internal static int? GetControlledExpectedInputRows(
        int frameHeight,
        long inputTravelUnits)
    {
        if (inputTravelUnits <= 0)
        {
            return null;
        }

        return (int)Math.Clamp(
            inputTravelUnits,
            1,
            Math.Max(
                1,
                frameHeight - ScrollCaptureOptions.Default.MinimumOverlapRows));
    }

    internal static ImageOverlapMatch? FindControlledInitialOverlap(
        Bitmap initialFrame,
        Bitmap returnFrame,
        ScrollCaptureDirection returnDirection,
        int expectedCrossingRows,
        ScrollCaptureOptions options)
    {
        var match = returnDirection == ScrollCaptureDirection.Up
            ? AutomaticImageOverlapMatcher.FindVerticalOverlap(
                returnFrame,
                initialFrame,
                options.MinimumOverlapRows,
                Math.Max(
                    options.MinimumOverlapConfidence,
                    ControlledInitialCrossingMinimumConfidence),
                options.MinimumNewRows,
                expectedCrossingRows)
            : AutomaticImageOverlapMatcher.FindVerticalOverlap(
                initialFrame,
                returnFrame,
                options.MinimumOverlapRows,
                Math.Max(
                    options.MinimumOverlapConfidence,
                    ControlledInitialCrossingMinimumConfidence),
                options.MinimumNewRows,
                expectedCrossingRows);
        if (match is null)
        {
            return null;
        }

        var movementRows = returnFrame.Height - match.OverlapRows;
        var tolerance = Math.Max(48, returnFrame.Height / 3);
        return Math.Abs(movementRows - expectedCrossingRows) <= tolerance
            ? match
            : null;
    }

    internal static long GetControlledExpectedCrossingRows(
        long returnInputMagnitude,
        long outboundInputMagnitude,
        int outboundVisualTravelRows)
    {
        var inputPastInitial = Math.Max(
            0,
            returnInputMagnitude - outboundInputMagnitude);
        if (inputPastInitial == 0 ||
            outboundInputMagnitude <= 0 ||
            outboundVisualTravelRows <= 0)
        {
            return inputPastInitial;
        }

        return (long)Math.Round(
            inputPastInitial *
            (outboundVisualTravelRows / (double)outboundInputMagnitude));
    }

    internal static bool IsControlledInitialCrossingConsistent(
        int previousMovementRows,
        long previousInputMagnitude,
        int currentMovementRows,
        long currentInputMagnitude)
    {
        var movementDelta = currentMovementRows - previousMovementRows;
        var inputDelta = currentInputMagnitude - previousInputMagnitude;
        if (movementDelta <= 0 || inputDelta <= 0)
        {
            return false;
        }

        // After the initial viewport is crossed, both the overlap displacement
        // and return-driver distance must continue in the same direction. The
        // two units are only approximate, so allow a broad local tolerance but
        // require a second independently captured frame before changing legs.
        return Math.Abs((long)movementDelta - inputDelta) <=
            Math.Max(24L, inputDelta);
    }

    internal static bool IsControlledInitialCrossingStable(
        int previousMovementRows,
        long previousInputMagnitude,
        int currentMovementRows,
        long currentInputMagnitude,
        double confidence)
    {
        return confidence >= ControlledInitialCrossingMinimumConfidence &&
            currentInputMagnitude == previousInputMagnitude &&
            Math.Abs(currentMovementRows - previousMovementRows) <= 24;
    }

    /// <summary>
    /// Editors and inertial pages keep drifting past the initial viewport
    /// while the driver is already stopped: consecutive samples both overlap
    /// the initial frame decisively but the crossing grows by far more than
    /// the ±24-row stability window (field: 82→175→220→245 rows at confidence
    /// 1.000 without ever confirming). Two decisive measurements with a
    /// growing crossing are proof of the crossing itself.
    /// </summary>
    internal static bool IsControlledInitialCrossingGlide(
        int previousMovementRows,
        double previousConfidence,
        int currentMovementRows,
        double currentConfidence)
    {
        return previousConfidence >= ControlledInitialCrossingMinimumConfidence &&
            currentConfidence >= ControlledInputReturnMinimumConfidence &&
            currentMovementRows > previousMovementRows;
    }

    internal static int GetControlledResumeMaximumMovementRows(
        int frameHeight,
        int? expectedRows,
        int minimumOverlapRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameHeight, 2);
        var maximumMatchableRows = frameHeight - Math.Clamp(
            minimumOverlapRows,
            1,
            frameHeight - 1);
        var expectedAllowance = Math.Max(0L, expectedRows ?? 0) * 3L;
        return (int)Math.Min(
            maximumMatchableRows,
            Math.Max(frameHeight / 2L, expectedAllowance));
    }

    /// <summary>
    /// Pause/return settle already waits for a stationary fingerprint, so any
    /// remaining inertia strip that still overlaps the previous frame is safe
    /// to commit. Cap at the matcher limit rather than the resume half-viewport
    /// allowance that previously left PreparingPauseDown stuck on
    /// movement-cap-veto.
    /// </summary>
    internal static int GetControlledSettleMaximumMovementRows(
        int frameHeight,
        int minimumOverlapRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameHeight, 2);
        return frameHeight - Math.Clamp(
            minimumOverlapRows,
            1,
            frameHeight - 1);
    }

    internal static int? GetControlledRetryMaximumMovementRows(
        string? rejectReason,
        int frameHeight,
        int? expectedRows,
        int minimumOverlapRows)
    {
        return rejectReason == "movement-cap-veto"
            ? GetControlledResumeMaximumMovementRows(
                frameHeight,
                expectedRows,
                minimumOverlapRows)
            : null;
    }

    internal static int GetControlledInitialAlignmentMaximumMovementRows(
        int frameHeight,
        int? confirmedCrossingRows,
        int minimumOverlapRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameHeight, 2);
        var maximumMatchableRows = frameHeight - Math.Clamp(
            minimumOverlapRows,
            1,
            frameHeight - 1);
        var baseline = Math.Max(64, frameHeight / 3);
        var confirmedAllowance = confirmedCrossingRows is > 0
            ? confirmedCrossingRows.Value + 24
            : 0;
        return Math.Min(
            maximumMatchableRows,
            Math.Max(baseline, confirmedAllowance));
    }

    private static Task<bool> TryAddControlledFrameAsync(
        ControlledScrollCaptureComposer composer,
        Bitmap frame,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        int? expectedRows,
        CancellationToken cancellationToken,
        int? maximumAcceptedNewRows = null,
        bool tolerateQuantizedExpectation = false)
    {
        // A fixed-rate wheel animation can legitimately leave only a few new
        // rows at startup or immediately before a pause. Advancing the viewport
        // anchor without storing those rows creates a tiny loss at every stop,
        // so small controlled expectations are allowed to commit at pixel precision.
        var effectiveOptions = expectedRows is > 0 &&
            expectedRows < options.MinimumNewRows
            ? options with { MinimumNewRows = 1 }
            : options;
        if (maximumAcceptedNewRows is null && expectedRows is > 0)
        {
            maximumAcceptedNewRows = Math.Min(
                frame.Height - effectiveOptions.MinimumOverlapRows,
                Math.Max(96, expectedRows.Value * 3));
        }

        return Task.Run(
            () => direction == ScrollCaptureDirection.Down
                ? composer.TryAddDown(
                    frame,
                    effectiveOptions,
                    expectedRows,
                    maximumAcceptedNewRows,
                    tolerateQuantizedExpectation)
                : composer.TryAddUp(
                    frame,
                    effectiveOptions,
                    expectedRows,
                    maximumAcceptedNewRows,
                    tolerateQuantizedExpectation),
            cancellationToken);
    }

    internal static bool IsControlledFrameLocated(
        bool added,
        int? movementRows,
        string? rejectReason)
    {
        return added ||
            movementRows.HasValue ||
            rejectReason == "below-minimum";
    }

    internal static bool ShouldAdvanceControlledInputAnchor(
        bool frameLocated,
        int? movementRows)
    {
        return frameLocated && movementRows is > 0;
    }

    internal static bool IsControlledBoundarySample(
        bool beganUpwardExtension,
        bool added,
        bool fingerprintStationary,
        int? movementRows,
        string? rejectReason)
    {
        return !beganUpwardExtension &&
            !added &&
            fingerprintStationary &&
            movementRows == 0 &&
            rejectReason is null;
    }

    public static async Task<ScrollCaptureResult> CaptureAsync(
        ScrollCaptureTarget target,
        ScrollCaptureOptions? options = null,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= ScrollCaptureOptions.Default;

        if (target.WindowHandle == IntPtr.Zero || target.CaptureRegion.IsEmpty)
        {
            return ScrollCaptureResult.Failure("无法识别滚动目标窗口。");
        }

        using var composer = new ScrollCaptureComposer();
        var hasOriginalCursorPosition = ForegroundWindowCaptureService.TryGetCursorPosition(
            out var originalCursorPosition);

        try
        {
            using var initialFrame = await CaptureFrameAsync(
                target,
                setProgressVisibilityAsync,
                cancellationToken);
            _ = await Task.Run(
                () => composer.TryAddFrame(initialFrame, options, out _),
                cancellationToken);

            for (var frameIndex = 1; frameIndex < options.MaximumFrames; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var frame = await ScrollAndCaptureFrameAsync(
                    target,
                    options,
                    setProgressVisibilityAsync,
                    cancellationToken);

                if (frame is null)
                {
                    break;
                }

                if (await Task.Run(
                        () => composer.TryAddFrame(frame, options, out _),
                        cancellationToken))
                {
                    continue;
                }

                if (target.SupportsVerticalScroll)
                {
                    break;
                }

                using var fallbackFrame = await ScrollAndCaptureFrameAsync(
                    target,
                    options,
                    setProgressVisibilityAsync,
                    cancellationToken,
                    useWindowMessage: true);

                if (fallbackFrame is null ||
                    !await Task.Run(
                        () => composer.TryAddFrame(fallbackFrame, options, out _),
                        cancellationToken))
                {
                    break;
                }
            }

            // Prefer a usable result over hard failure. A single frame is still
            // the selected region; partial stitches are better than an error tip.
            if (composer.FrameCount < 1)
            {
                return ScrollCaptureResult.Failure("滚动截图失败。");
            }

            return await ComposeResultAsync(composer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ScrollCaptureResult.Failure("长截图已取消。");
        }
        catch (Exception)
        {
            return ScrollCaptureResult.Failure("滚动截图失败。");
        }
        finally
        {
            if (hasOriginalCursorPosition)
            {
                ForegroundWindowCaptureService.RestoreCursorPosition(originalCursorPosition);
            }
        }
    }

    public static Task<ScrollCaptureResult> CaptureAsync(
        IntPtr windowHandle,
        ScrollCaptureOptions? options = null,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (!ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                windowHandle,
                out var target) ||
            target is null)
        {
            return Task.FromResult(ScrollCaptureResult.Failure("无法识别滚动目标窗口。"));
        }

        return CaptureAsync(
            target,
            options,
            setProgressVisibilityAsync,
            cancellationToken);
    }

    public static Task<ScrollCaptureResult> CaptureForegroundWindowAsync(
        ScrollCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return CaptureAsync(
            ForegroundWindowCaptureService.GetForegroundWindowHandle(),
            options,
            setProgressVisibilityAsync: null,
            cancellationToken);
    }

    /// <summary>
    /// Builds the first stitch frame for the sampler region. Prefer a crop of the
    /// pre-scroll snapshot so content before the first wheel event is retained;
    /// fall back to a live capture when the snapshot cannot supply that region.
    /// </summary>
    public static Bitmap CreateInitialFrame(
        Bitmap selectionSnapshot,
        ScreenRegion selectionRegion,
        ScreenRegion captureRegion)
    {
        ArgumentNullException.ThrowIfNull(selectionSnapshot);

        if (captureRegion.IsEmpty)
        {
            throw new ArgumentException("滚动截图区域不能为空。", nameof(captureRegion));
        }

        if (selectionRegion.Width == captureRegion.Width &&
            selectionRegion.Height == captureRegion.Height &&
            selectionRegion.X == captureRegion.X &&
            selectionRegion.Y == captureRegion.Y &&
            selectionSnapshot.Width == captureRegion.Width &&
            selectionSnapshot.Height == captureRegion.Height)
        {
            return (Bitmap)selectionSnapshot.Clone();
        }

        var offsetX = captureRegion.X - selectionRegion.X;
        var offsetY = captureRegion.Y - selectionRegion.Y;
        if (offsetX >= 0 &&
            offsetY >= 0 &&
            offsetX + captureRegion.Width <= selectionSnapshot.Width &&
            offsetY + captureRegion.Height <= selectionSnapshot.Height)
        {
            return selectionSnapshot.Clone(
                new Rectangle(
                    offsetX,
                    offsetY,
                    captureRegion.Width,
                    captureRegion.Height),
                PixelFormat.Format32bppPArgb);
        }

        return ForegroundWindowCaptureService.CaptureRegion(captureRegion);
    }

    private static async Task<System.Drawing.Bitmap> CaptureFrameAsync(
        ScrollCaptureTarget target,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync,
        CancellationToken cancellationToken)
    {
        if (setProgressVisibilityAsync is not null)
        {
            await setProgressVisibilityAsync(false, cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ForegroundWindowCaptureService.CaptureScrollTargetRegion(target);
        }
        finally
        {
            if (setProgressVisibilityAsync is not null)
            {
                await setProgressVisibilityAsync(true, CancellationToken.None);
            }
        }
    }

    private static Task<System.Drawing.Bitmap> CaptureControlledFrameAsync(
        ScrollCaptureTarget target,
        ControlledScrollDriver scrollDriver,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scrollDriver);
        cancellationToken.ThrowIfCancellationRequested();

        return CaptureControlledFrameCoreAsync(
            scrollDriver,
            setProgressVisibilityAsync,
            cancellationToken);
    }

    private static async Task<System.Drawing.Bitmap> CaptureControlledFrameCoreAsync(
        ControlledScrollDriver scrollDriver,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync,
        CancellationToken cancellationToken)
    {
        if (setProgressVisibilityAsync is not null)
        {
            await setProgressVisibilityAsync(false, cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return scrollDriver.CaptureFrame();
        }
        finally
        {
            if (setProgressVisibilityAsync is not null)
            {
                await setProgressVisibilityAsync(true, CancellationToken.None);
            }
        }
    }

    private static async Task TryAddCurrentFrameAsync(
        ScrollCaptureTarget target,
        ScrollCaptureComposer composer,
        ScrollCaptureOptions options,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync,
        Action<int>? acceptedFrameCountChanged,
        CancellationToken cancellationToken)
    {
        using var frame = await CaptureFrameAsync(
            target,
            setProgressVisibilityAsync,
            cancellationToken);

        if (IsLikelyBlankFrame(frame))
        {
            return;
        }

        if (await Task.Run(
                () => composer.TryAddFrame(frame, options, out _),
                cancellationToken))
        {
            acceptedFrameCountChanged?.Invoke(composer.FrameCount);
        }
    }

    // The preview now renders the whole stitched image per update, so pacing
    // it like a video feed would burn memory bandwidth for no visible gain.
    private const int PreviewMinIntervalMilliseconds = 120;

    internal static int GetActiveSampleDelayMilliseconds(
        int configuredDelayMilliseconds,
        TimeSpan elapsedSinceWheel,
        bool directManualWheel = false)
    {
        // Direct manual wheel mode must keep up with the viewport while a
        // high-resolution wheel is moving.  Controlled automatic scrolling
        // and the throttled manual driver retain the established 32 ms floor.
        var cadenceFloor = elapsedSinceWheel < TimeSpan.FromMilliseconds(80)
            ? directManualWheel
                ? SettlingSampleDelayMilliseconds
                : ActiveScrollSampleDelayMilliseconds
            : SettlingSampleDelayMilliseconds;
        return Math.Max(configuredDelayMilliseconds, cadenceFloor);
    }

    internal static int GetBackpressureThreshold(int queueCapacity)
    {
        return Math.Max(4, queueCapacity / 8);
    }

    private static int GetActiveFrameQueueCapacity(ScreenRegion region)
    {
        var frameBytes = Math.Max(
            1L,
            (long)region.Width * region.Height * 4L * ActiveFrameBufferCount);
        return Math.Clamp(
            (int)(ActiveFrameQueueMemoryBudgetBytes / frameBytes),
            2,
            ActiveFrameQueueMaximumCapacity);
    }

    private static async Task CaptureAndQueueFrameAsync(
        ScrollCaptureTarget target,
        ChannelWriter<QueuedScrollFrame> writer,
        ScrollCaptureDirection fallbackDirection,
        ScrollCaptureOptions options,
        Func<bool, CancellationToken, Task>? setPreviewVisibilityAsync,
        ScrollCaptureFrameDump? frameDump,
        ScrollWheelMotionTracker motionTracker,
        ActiveFrameQueueState queueState,
        CapturedFrameGate frameGate,
        StationaryScrollBoundaryDetector boundaryDetector,
        ScrollCaptureDiagnostics diagnostics,
        ManualScrollDriver? throttledScrollDriver,
        bool prepareResult,
        CancellationToken cancellationToken)
    {
        var captureTimestamp = Stopwatch.GetTimestamp();
        var frame = await CaptureFrameAsync(
            target,
            setPreviewVisibilityAsync,
            cancellationToken);

        var injectedWheelDelta =
            throttledScrollDriver?.TakeInjectedWheelDelta() ?? 0;
        if (injectedWheelDelta != 0)
        {
            motionTracker.AddDelta(injectedWheelDelta);
        }

        // Deliberately no blank-frame drop here: chat and web pages routinely
        // scroll through viewports that are almost entirely white, and
        // discarding those samples silently opened unmatchable gaps — the
        // primary reported cause of a stalled stitch. Flat frames now flow to
        // the composer, which bridges them on the wheel estimate.
        if (!frameGate.HasChanged(frame))
        {
            if (!motionTracker.HasPendingInput)
            {
                boundaryDetector.Reset();
                frame.Dispose();
                diagnostics.Record(
                    "frame-skipped-stationary",
                    ("captureMs", Stopwatch.GetElapsedTime(
                        captureTimestamp).TotalMilliseconds));
                return;
            }

            var boundaryDirection = motionTracker.Direction;
            if (!boundaryDetector.ShouldQueueMarker(
                    boundaryDirection,
                    Stopwatch.GetTimestamp(),
                    motionTracker.PendingDelta))
            {
                frame.Dispose();
                diagnostics.Record(
                    "frame-skipped-stationary",
                    ("captureMs", Stopwatch.GetElapsedTime(
                        captureTimestamp).TotalMilliseconds));
                return;
            }

            // Queue the marker behind every frame captured before the physical
            // viewport stopped. This preserves the real boundary position even
            // when the matcher is currently draining a deep fling backlog.
            var pendingMotion = motionTracker.TakePendingMotion(
                frame.Height,
                options,
                boundaryDirection);
            var boundaryFrame = new QueuedScrollFrame(
                frame,
                new ScrollWheelMotionSample(
                    boundaryDirection,
                    ExpectedRows: null,
                    Delta: 0),
                queueState.ReserveSequence(),
                Stopwatch.GetTimestamp(),
                prepareResult: false,
                confirmedBoundary: boundaryDirection);

            if (!writer.TryWrite(boundaryFrame))
            {
                motionTracker.AddDelta(pendingMotion.Delta);
                boundaryFrame.Dispose();
                diagnostics.Record(
                    "boundary-marker-write-rejected",
                    ("direction", boundaryDirection.ToString()));
                return;
            }

            boundaryDetector.MarkQueued();
            queueState.OnEnqueued(boundaryFrame);
            diagnostics.Record(
                "boundary-marker-queued",
                ("direction", boundaryDirection.ToString()),
                ("captureMs", Stopwatch.GetElapsedTime(
                    captureTimestamp).TotalMilliseconds));
            return;
        }

        boundaryDetector.Reset();

        var motion = motionTracker.TakePendingMotion(
            frame.Height,
            options,
            fallbackDirection);
        frameDump?.Save(frame, motion.Direction);
        var queuedFrame = new QueuedScrollFrame(
            frame,
            motion,
            queueState.ReserveSequence(),
            Stopwatch.GetTimestamp(),
            prepareResult);

        // Never block screen sampling behind image matching. Keeping the oldest
        // queued frames preserves overlap continuity; when the fixed queue is
        // full, discard only the newest sample and try again on the next tick.
        if (!writer.TryWrite(queuedFrame))
        {
            motionTracker.AddDelta(motion.Delta);
            queuedFrame.Dispose();
            diagnostics.Record(
                "frame-write-rejected",
                ("sequence", queuedFrame.Sequence));
            return;
        }

        frameGate.AcceptPending();
        queueState.OnEnqueued(queuedFrame);
        diagnostics.Record(
            "frame-captured",
            ("sequence", queuedFrame.Sequence),
            ("direction", motion.Direction.ToString()),
            ("wheelDelta", motion.Delta),
            ("expectedRows", motion.ExpectedRows),
            ("captureMs", Stopwatch.GetElapsedTime(
                captureTimestamp).TotalMilliseconds),
            ("queuedFrames", queueState.QueueCount));
    }

    private static async Task ProcessQueuedFramesAsync(
        ChannelReader<QueuedScrollFrame> reader,
        ScrollCaptureComposer composer,
        ScrollCaptureOptions options,
        Action<ScrollCapturePreviewState>? previewChanged,
        long[] previewTimestampSlot,
        PreparedCaptureCache preparedCache,
        ScrollWheelMotionTracker motionTracker,
        ActiveFrameQueueState queueState,
        ScrollCaptureDiagnostics diagnostics,
        int backlogCapacity,
        SampleCadence sampleCadence,
        ManualScrollDriver? throttledScrollDriver,
        CancellationToken cancellationToken)
    {
        var backlog = new List<QueuedScrollFrame>(backlogCapacity + 1);
        var chainState = new BacklogChainState();
        var drainDeadlineTimestamp = 0L;

        try
        {
            while (true)
            {
                // Top up between every chain step, not once per drained
                // backlog: one match spans several sampling intervals, and a
                // hand-off channel that stays full while a long backlog is
                // processed rejects precisely the fresh samples the chain
                // needs to stay connected to the viewport.
                while (backlog.Count < backlogCapacity &&
                       reader.TryRead(out var queuedFrame))
                {
                    queueState.OnDequeued();
                    AddToBacklog(
                        backlog,
                        queuedFrame,
                        backlogCapacity,
                        chainState,
                        motionTracker,
                        options,
                        queueState);
                }

                if (drainDeadlineTimestamp == 0L && reader.Completion.IsCompleted)
                {
                    // The sampler is done. Everything still pending is real
                    // content the user scrolled past, so stitch it — bounded,
                    // because finishing has to feel immediate.
                    drainDeadlineTimestamp = Stopwatch.GetTimestamp() +
                        (long)(Stopwatch.Frequency *
                            (CompletionDrainBudgetMilliseconds / 1000d));
                    // Keep the same-direction overlap chain intact. Fixed-count
                    // collapsing created gaps larger than one viewport exactly
                    // where a reverse fling crossed the captured boundary.
                    // Sampling backpressure already bounds this backlog.
                    CollapseStaleDirectionRun(
                        backlog,
                        motionTracker,
                        options,
                        queueState);
                }

                if (backlog.Count == 0)
                {
                    if (!await reader.WaitToReadAsync(cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                if (backlog.Count >= SameDirectionBacklogCollapseThreshold &&
                    backlog.All(item =>
                        item.Motion.Direction == backlog[0].Motion.Direction &&
                        item.ConfirmedBoundary is null) &&
                    composer.IsNearCapturedBoundary(
                        backlog[0].Motion.Direction,
                        backlog[0].Frame.Height))
                {
                    // The anchor is at the edge and the queue still contains a
                    // long same-direction tail. Retain two overlap frames to
                    // prove continuity, then carry the remaining displacement
                    // to the freshest frame so the actual boundary crossing is
                    // processed now instead of several seconds later.
                    var pendingBeforeCollapse = backlog.Count;
                    CollapseStaleDirectionRun(
                        backlog,
                        motionTracker,
                        options,
                        queueState,
                        allowSameDirectionCollapse: true);
                    if (backlog.Count < pendingBeforeCollapse)
                    {
                        diagnostics.Record(
                            "boundary-backlog-collapsed",
                            ("direction", backlog[0].Motion.Direction.ToString()),
                            ("dropped", pendingBeforeCollapse - backlog.Count),
                            ("retained", backlog.Count),
                            ("currentTop", composer.CurrentFrameTop),
                            ("capturedTop", composer.CapturedContentTop),
                            ("capturedBottom", composer.CapturedContentBottom));
                    }
                }

                await ProcessNextBacklogFrameAsync(
                    backlog,
                    chainState,
                    composer,
                    options,
                    previewChanged,
                    previewTimestampSlot,
                    preparedCache,
                    motionTracker,
                    queueState,
                    diagnostics,
                    drainDeadlineTimestamp,
                    sampleCadence,
                    throttledScrollDriver,
                    cancellationToken);
            }
        }
        finally
        {
            DisposeBacklog(backlog, backlog.Count, queueState);

            // Cancellation can leave captures in the hand-off channel; release
            // their bitmaps instead of waiting for a finalizer.
            while (reader.TryRead(out var pendingFrame))
            {
                queueState.OnDequeued();
                queueState.OnFrameRetired();
                pendingFrame.Dispose();
            }
        }
    }

    private static void AddToBacklog(
        List<QueuedScrollFrame> backlog,
        QueuedScrollFrame queuedFrame,
        int capacity,
        BacklogChainState chainState,
        ScrollWheelMotionTracker motionTracker,
        ScrollCaptureOptions options,
        ActiveFrameQueueState queueState)
    {
        if (chainState.CarriedMotion is { } carriedMotion)
        {
            // A previous sample was dropped without being located; its wheel
            // motion rides on this one so the accumulated estimate still
            // describes the displacement from the stitched anchor.
            queuedFrame.Motion = motionTracker.MergeMotion(
                carriedMotion,
                queuedFrame.Motion,
                queuedFrame.Frame.Height,
                options);
            chainState.CarriedMotion = null;
        }

        backlog.Add(queuedFrame);

        // Reverse flings often arrive while the matcher is still draining the
        // previous direction. Replaying every stale same-direction sample
        // first leaves the reverse chain several seconds late, and by then
        // capacity decimation has already erased the frames that cross the
        // captured edge. Collapse the obsolete middle of a direction run so
        // reverse samples reach the stitcher while they still overlap.
        CollapseStaleDirectionRun(backlog, motionTracker, options, queueState);

        while (backlog.Count > capacity && backlog.Count >= 3)
        {
            var preferredIndex = ScrollFrameSelection.SelectDecimationIndex(
                backlog.Count);
            var index = Enumerable.Range(1, backlog.Count - 2)
                .Where(candidate =>
                    backlog[candidate].ConfirmedBoundary is null)
                .OrderBy(candidate => Math.Abs(candidate - preferredIndex))
                .FirstOrDefault(-1);
            if (index < 0)
            {
                break;
            }
            var dropped = backlog[index];
            var successor = backlog[index + 1];
            // Fold the dropped sample's wheel motion into its successor so the
            // accumulated estimate still describes the true displacement.
            successor.Motion = motionTracker.MergeMotion(
                dropped.Motion,
                successor.Motion,
                dropped.Frame.Height,
                options);
            backlog.RemoveAt(index);
            queueState.OnDropped(dropped);
            queueState.OnFrameRetired();
            dropped.Dispose();
        }
    }

    private static void CollapseStaleDirectionRun(
        List<QueuedScrollFrame> backlog,
        ScrollWheelMotionTracker motionTracker,
        ScrollCaptureOptions options,
        ActiveFrameQueueState queueState,
        bool allowSameDirectionCollapse = false)
    {
        if (backlog.Count < 4)
        {
            return;
        }

        var headDirection = backlog[0].Motion.Direction;
        var tailDirection = backlog[^1].Motion.Direction;
        if (headDirection == tailDirection)
        {
            // Backpressure bounds a same-direction run. Keep the full short
            // overlap chain here; a boundary-aware collapse is performed by
            // the processor only when the stitched anchor is actually close to
            // that edge.
            if (!allowSameDirectionCollapse ||
                backlog.Count < SameDirectionBacklogCollapseThreshold ||
                backlog.Count <
                    SameDirectionRetainedHeadFrames +
                    SameDirectionRetainedTailFrames ||
                backlog.Any(item =>
                    item.Motion.Direction != headDirection ||
                    item.ConfirmedBoundary is not null))
            {
                return;
            }

            // Drop middle frames aggressively, but never let one surviving
            // frame carry a merged displacement beyond a third of the
            // viewport. Merging the whole run onto the first tail frame
            // created one jump larger than a viewport exactly at the captured
            // edge — unmatchable on the feature-poor bands where page tops
            // live — so content went missing. Keeping every other frame
            // instead preserved the chain but left the backlog hovering at
            // the threshold, and the matcher saturated a core while the
            // preview lagged by seconds. The displacement bound gives both:
            // wholesale-level reduction wherever the estimate says the gap
            // stays bridgeable, and a retained frame exactly where it stops
            // being bridgeable.
            var mergeCapRows = Math.Max(
                96,
                backlog[0].Frame.Height / 3);
            var mergeIndex = SameDirectionRetainedHeadFrames;
            while (mergeIndex <
                   backlog.Count - SameDirectionRetainedTailFrames)
            {
                var current = backlog[mergeIndex];
                var successor = backlog[mergeIndex + 1];
                var merged = motionTracker.MergeMotion(
                    current.Motion,
                    successor.Motion,
                    successor.Frame.Height,
                    options);
                if (merged.ExpectedRows is not { } mergedRows ||
                    mergedRows > mergeCapRows)
                {
                    // Absorbing this frame would make the successor's gap
                    // unbridgeable (or unmeasurable); keep it as a stepping
                    // stone and continue behind it.
                    mergeIndex++;
                    continue;
                }

                successor.Motion = merged;
                backlog.RemoveAt(mergeIndex);
                queueState.OnDropped(current);
                queueState.OnFrameRetired();
                current.Dispose();
            }

            return;
        }

        var firstOpposite = -1;
        for (var index = 1; index < backlog.Count; index++)
        {
            if (backlog[index].Motion.Direction != headDirection ||
                backlog[index].ConfirmedBoundary is not null)
            {
                firstOpposite = index;
                break;
            }
        }

        // Keep only the last same-direction sample before the reverse. The
        // live anchor already provides continuity; replaying the whole stale
        // run first makes the reverse several seconds late and erases the
        // frames that actually cross the captured edge.
        if (firstOpposite < 2)
        {
            return;
        }

        var survivorIndex = firstOpposite - 1;
        var survivor = backlog[survivorIndex];
        var motion = backlog[0].Motion;
        for (var index = 1; index <= survivorIndex; index++)
        {
            motion = motionTracker.MergeMotion(
                motion,
                backlog[index].Motion,
                survivor.Frame.Height,
                options);
        }

        survivor.Motion = motion;

        for (var index = 0; index < survivorIndex; index++)
        {
            var dropped = backlog[0];
            backlog.RemoveAt(0);
            queueState.OnDropped(dropped);
            queueState.OnFrameRetired();
            dropped.Dispose();
        }
    }

    /// <summary>
    /// Stitches the oldest pending viewport. The backlog is processed strictly
    /// in capture order: consecutive samples are only a few frame intervals
    /// apart, so each one still overlaps its predecessor even during the
    /// fastest fling — walking that chain keeps the anchor glued to the
    /// viewport through accelerations, smooth-scroll inertia and direction
    /// reversals alike.
    /// </summary>
    /// <remarks>
    /// Skipping ahead to the newest sample was tried and fails structurally: a
    /// skipped-to sample must overlap the much older stitched content, and the
    /// faster the scroll the smaller that overlap is, so one missed frame
    /// snowballed into a permanently lost anchor. Wheel deltas cannot patch
    /// that up either — smooth scrolling keeps the screen moving long after
    /// the last wheel tick, so pending samples routinely carry no wheel
    /// evidence at all.
    /// </remarks>
    private static async Task ProcessNextBacklogFrameAsync(
        List<QueuedScrollFrame> backlog,
        BacklogChainState chainState,
        ScrollCaptureComposer composer,
        ScrollCaptureOptions options,
        Action<ScrollCapturePreviewState>? previewChanged,
        long[] previewTimestampSlot,
        PreparedCaptureCache preparedCache,
        ScrollWheelMotionTracker motionTracker,
        ActiveFrameQueueState queueState,
        ScrollCaptureDiagnostics diagnostics,
        long drainDeadlineTimestamp,
        SampleCadence sampleCadence,
        ManualScrollDriver? throttledScrollDriver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (composer.FrameCount >= options.MaximumFrames)
        {
            DisposeBacklog(backlog, backlog.Count, queueState);
            return;
        }

        if (drainDeadlineTimestamp != 0L &&
            Stopwatch.GetTimestamp() >= drainDeadlineTimestamp &&
            backlog.Count > 1)
        {
            // Out of budget after Finish/Edit: keep only the freshest
            // viewport (and carry its accumulated wheel motion) so the
            // result still ends where the user stopped without replaying
            // every queued match. Do not collapse just because a few
            // frames are pending — reverse flings need those chain steps.
            CarryBacklogMotion(
                backlog,
                backlog.Count - 1,
                motionTracker,
                options);
            DisposeBacklog(backlog, backlog.Count - 1, queueState);
        }

        // A boundary marker is queued only when the wheel keeps moving while
        // the screen already stands still — the page is parked at a physical
        // edge. Every sample queued before it shows the same parked viewport
        // and none of them matched (that is why they piled up: unmatchable
        // edge frames cost a full search each). Draining them first delayed
        // the marker by several seconds while the preview sat frozen, so let
        // the marker jump the queue and let the edge re-anchor resolve the
        // position in one step.
        var markerIndex = backlog.FindIndex(
            queued => queued.ConfirmedBoundary is not null);
        if (markerIndex > 0)
        {
            CarryBacklogMotion(backlog, markerIndex, motionTracker, options);
            DisposeBacklog(backlog, markerIndex, queueState);
            diagnostics.Record(
                "boundary-marker-promoted",
                ("skippedFrames", markerIndex));
        }

        var queuedFrame = backlog[0];
        var processingMotion = queuedFrame.ConfirmedBoundary is { } boundary
            ? new ScrollWheelMotionSample(
                boundary,
                ExpectedRows: null,
                Delta: 0)
            : queuedFrame.Motion;
        var wasAdded = await ProcessCapturedFrameForDirectionAsync(
            queuedFrame.Frame,
            composer,
            options,
            processingMotion,
            previewChanged,
            forcePreview: false,
            previewTimestampSlot: previewTimestampSlot,
            preparedCache: preparedCache,
            motionTracker: motionTracker,
            prepareResult: queuedFrame.PrepareResult,
            diagnostics: diagnostics,
            queuedSequence: queuedFrame.Sequence,
            capturedTimestamp: queuedFrame.CapturedTimestamp,
            cancellationToken: cancellationToken);

        if (queuedFrame.ConfirmedBoundary is { } confirmedBoundary)
        {
            var wasConfirmed = composer.TryMarkBoundaryReached(
                queuedFrame.Frame,
                confirmedBoundary);
            var wasReanchored = false;
            if (!wasConfirmed)
            {
                // The wheel monitor proved the page stopped at a physical
                // edge, yet the marker frame does not match the stored edge:
                // the anchor lost a fast fling. Re-locate against the stored
                // edge instead of discarding the strongest position evidence
                // this capture will ever get.
                wasReanchored = composer.TryReanchorAtBoundary(
                    queuedFrame.Frame,
                    confirmedBoundary,
                    options);
                if (wasReanchored)
                {
                    composer.MarkBoundaryReached(confirmedBoundary);
                    await ReportPreviewAsync(
                        composer,
                        previewChanged,
                        cancellationToken);
                }
            }

            diagnostics.Record(
                wasConfirmed
                    ? "boundary-confirmed"
                    : wasReanchored
                        ? "boundary-reanchored"
                        : "boundary-marker-rejected",
                ("sequence", queuedFrame.Sequence),
                ("direction", confirmedBoundary.ToString()),
                ("outputHeight", composer.OutputHeight));
        }

        sampleCadence.ObserveProcessedFrame(
            composer.LastFrameMovementRows,
            queuedFrame.CapturedTimestamp);

        // Movement rows are set whenever the viewport was located, even
        // when it only walked through content that is already stitched.
        if (!wasAdded && composer.LastFrameMovementRows is null)
        {
            diagnostics.Record(
                "frame-chain-miss",
                ("sequence", queuedFrame.Sequence),
                ("pending", backlog.Count),
                ("outputHeight", composer.OutputHeight));

            if (backlog.Count > 1)
            {
                // Transient artifact or a decimated-away gap: fold this
                // sample's wheel motion into the next one and let that one
                // try the same anchor.
                CarryBacklogMotion(backlog, 1, motionTracker, options);
            }
            else
            {
                // Retrying the same pixels against the same anchor cannot
                // turn out differently, so only the motion survives until
                // the next capture arrives.
                chainState.CarriedMotion = queuedFrame.Motion;
            }
        }

        DisposeBacklog(backlog, 1, queueState);
        // Manual wheel mode is allowed to advance only after the preceding
        // sampled viewport has actually been matched. Replenishing this budget
        // at capture time lets a slow/repetitive frame search fall seconds
        // behind while the target keeps scrolling, eventually destroying all
        // overlap. Controlled automatic mode uses its own capture handshake.
        if (queueState.PendingStitchCount == 0)
        {
            throttledScrollDriver?.AcknowledgeCapturedFrame();
        }
    }

    private static void CarryBacklogMotion(
        List<QueuedScrollFrame> backlog,
        int count,
        ScrollWheelMotionTracker motionTracker,
        ScrollCaptureOptions options)
    {
        if (count <= 0 || count >= backlog.Count)
        {
            return;
        }

        var survivor = backlog[count];
        var frameHeight = survivor.Frame.Height;
        var motion = backlog[0].Motion;

        for (var index = 1; index <= count; index++)
        {
            motion = motionTracker.MergeMotion(
                motion,
                backlog[index].Motion,
                frameHeight,
                options);
        }

        survivor.Motion = motion;
    }

    private static void DisposeBacklog(
        List<QueuedScrollFrame> backlog,
        int count,
        ActiveFrameQueueState queueState)
    {
        var removed = Math.Clamp(count, 0, backlog.Count);

        for (var index = 0; index < removed; index++)
        {
            backlog[index].Dispose();
            queueState.OnFrameRetired();
        }

        backlog.RemoveRange(0, removed);
    }

    private static async Task<bool> ProcessCapturedFrameForDirectionAsync(
        Bitmap frame,
        ScrollCaptureComposer composer,
        ScrollCaptureOptions options,
        ScrollWheelMotionSample motion,
        Action<ScrollCapturePreviewState>? previewChanged,
        bool forcePreview,
        long[]? previewTimestampSlot,
        PreparedCaptureCache? preparedCache,
        ScrollWheelMotionTracker? motionTracker,
        bool prepareResult,
        ScrollCaptureDiagnostics? diagnostics,
        long queuedSequence,
        long capturedTimestamp,
        CancellationToken cancellationToken)
    {
        var processingTimestamp = Stopwatch.GetTimestamp();
        ImageOverlapMatch? overlapMatch = null;
        var wasAdded = await Task.Run(
            () => composer.TryAddFrame(
                frame,
                motion.Direction,
                options,
                motion.ExpectedRows,
                lockDirection: motion.HasFreshInput,
                out overlapMatch),
            cancellationToken);

        diagnostics?.Record(
            "frame-processed",
            ("sequence", queuedSequence),
            ("direction", motion.Direction.ToString()),
            ("queueLagMs", capturedTimestamp == 0L
                ? 0d
                : Stopwatch.GetElapsedTime(capturedTimestamp).TotalMilliseconds),
            ("durationMs", Stopwatch.GetElapsedTime(
                processingTimestamp).TotalMilliseconds),
            ("added", wasAdded),
            ("overlapRows", overlapMatch?.OverlapRows),
            ("confidence", overlapMatch?.Confidence),
            ("horizontalOffset", overlapMatch?.HorizontalOffset),
            ("movementRows", composer.LastFrameMovementRows),
            ("expectedRows", motion.ExpectedRows),
            ("currentTop", composer.CurrentFrameTop),
            ("capturedTop", composer.CapturedContentTop),
            ("capturedBottom", composer.CapturedContentBottom),
            ("fixedBottomRows", composer.FixedBottomRows),
            ("bridged", composer.LastFrameWasBridged),
            ("reject", composer.LastRejectReason),
            ("boundaryDrift", composer.LastBoundaryDriftRows),
            ("boundaryConfidence", composer.LastBoundaryConfidence),
            ("frameCount", composer.FrameCount),
            ("outputHeight", composer.OutputHeight));

        if (motion.HasFreshInput &&
            !composer.LastFrameWasBridged &&
            composer.LastFrameMovementRows is { } movementRows)
        {
            // A bridged displacement IS the wheel estimate; feeding it back
            // into the calibration would make the estimate self-confirming.
            motionTracker?.ObserveMovement(movementRows, motion.Delta);
        }

        if (previewChanged is not null)
        {
            var now = Environment.TickCount64;
            // Always publish expansion frames. Throttling them made reverse
            // captures report fewer "above" strips than were actually stitched
            // and left the progress UI lagging a full fling behind.
            var shouldRefreshPreview = forcePreview ||
                wasAdded ||
                previewTimestampSlot is not { Length: > 0 } ||
                now - previewTimestampSlot[0] >=
                    PreviewMinIntervalMilliseconds * 2;

            if (shouldRefreshPreview)
            {
                if (previewTimestampSlot is { Length: > 0 })
                {
                    previewTimestampSlot[0] = now;
                }

                var previewTimestamp = Stopwatch.GetTimestamp();
                await ReportPreviewAsync(
                    composer,
                    previewChanged,
                    cancellationToken);
                diagnostics?.Record(
                    "preview-updated",
                    ("durationMs", Stopwatch.GetElapsedTime(
                        previewTimestamp).TotalMilliseconds),
                    ("outputHeight", composer.OutputHeight));
            }
        }

        // Background full-image preparation is useful only when Finish/Edit is
        // imminent. Doing it while still matching competes for the same cores
        // that keep the stitch chain alive.
        if (wasAdded && prepareResult && preparedCache is not null)
        {
            await preparedCache.PrepareIfDueAsync(
                composer,
                force: false,
                cancellationToken);
        }

        return wasAdded;
    }

    private static async Task<bool> TryAddCurrentFrameForDirectionAsync(
        ScrollCaptureTarget target,
        ScrollCaptureComposer composer,
        ScrollCaptureDirection direction,
        ScrollCaptureOptions options,
        Func<bool, CancellationToken, Task>? setPreviewVisibilityAsync,
        Action<ScrollCapturePreviewState>? previewChanged,
        CancellationToken cancellationToken,
        ScrollCaptureFrameDump? frameDump = null,
        bool forcePreview = false,
        long[]? previewTimestampSlot = null,
        PreparedCaptureCache? preparedCache = null,
        ScrollWheelMotionTracker? motionTracker = null,
        bool prepareResult = true)
    {
        using var frame = await CaptureFrameAsync(
            target,
            setPreviewVisibilityAsync,
            cancellationToken);

        var motion = motionTracker?.TakePendingMotion(
            frame.Height,
            options,
            direction) ?? new ScrollWheelMotionSample(
            direction,
            ExpectedRows: null,
            Delta: 0);
        frameDump?.Save(frame, motion.Direction);
        return await ProcessCapturedFrameForDirectionAsync(
            frame,
            composer,
            options,
            motion,
            previewChanged,
            forcePreview,
            previewTimestampSlot,
            preparedCache,
            motionTracker,
            prepareResult,
            diagnostics: null,
            queuedSequence: 0,
            capturedTimestamp: Stopwatch.GetTimestamp(),
            cancellationToken);
    }

    /// <summary>
    /// Chain-stitching state that outlives individual backlog rounds: the
    /// wheel motion of samples that were dropped without being located, which
    /// must ride on the next capture so the accumulated estimate keeps
    /// describing the displacement from the stitched anchor.
    /// </summary>
    private sealed class BacklogChainState
    {
        public ScrollWheelMotionSample? CarriedMotion { get; set; }
    }

    /// <summary>
    /// One captured viewport waiting to be stitched. <see cref="Motion"/> is
    /// mutable because merging two samples into one is how the pipeline keeps
    /// the wheel estimate correct after it skips or decimates a sample.
    /// </summary>
    private sealed class QueuedScrollFrame : IDisposable
    {
        public QueuedScrollFrame(
            Bitmap frame,
            ScrollWheelMotionSample motion,
            long sequence,
            long capturedTimestamp,
            bool prepareResult,
            ScrollCaptureDirection? confirmedBoundary = null)
        {
            Frame = frame;
            Motion = motion;
            Sequence = sequence;
            CapturedTimestamp = capturedTimestamp;
            PrepareResult = prepareResult;
            ConfirmedBoundary = confirmedBoundary;
        }

        public Bitmap Frame { get; }

        public ScrollWheelMotionSample Motion { get; set; }

        public long Sequence { get; }

        public long CapturedTimestamp { get; }

        public bool PrepareResult { get; }

        public ScrollCaptureDirection? ConfirmedBoundary { get; }

        public void Dispose() => Frame.Dispose();
    }

    /// <summary>
    /// Adapts the backpressure sampling window to the observed scroll speed.
    /// The stitcher can fall behind — that is what the backlog is for — but the
    /// travel between two RETAINED samples must never exceed one viewport, or
    /// the chain breaks no matter how deep the buffers are. Written only by
    /// the frame processor; read by the sampling loop.
    /// </summary>
    private sealed class SampleCadence
    {
        private const int BrokenChainSkipWindowMilliseconds = 32;
        private const int MinimumSkipWindowMilliseconds = 24;
        private readonly int _frameHeight;
        private long _previousCapturedTimestamp;
        private double _rowsPerSecond;
        private int _skipWindowMilliseconds = DefaultSampleSkipWindowMilliseconds;

        public SampleCadence(int frameHeight)
        {
            _frameHeight = Math.Max(1, frameHeight);
        }

        public TimeSpan MaximumSkipWindow => TimeSpan.FromMilliseconds(
            Volatile.Read(ref _skipWindowMilliseconds));

        public void ObserveProcessedFrame(
            int? movementRows,
            long capturedTimestamp)
        {
            var previousTimestamp = _previousCapturedTimestamp;
            _previousCapturedTimestamp = capturedTimestamp;

            if (movementRows is null)
            {
                // The chain just missed. Densify sampling immediately so the
                // gap that is still growing stays bridgeable.
                Volatile.Write(
                    ref _skipWindowMilliseconds,
                    BrokenChainSkipWindowMilliseconds);
                return;
            }

            if (previousTimestamp == 0L ||
                capturedTimestamp <= previousTimestamp)
            {
                return;
            }

            var elapsedSeconds = (capturedTimestamp - previousTimestamp) /
                (double)Stopwatch.Frequency;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            var observedRowsPerSecond = movementRows.Value / elapsedSeconds;
            _rowsPerSecond = _rowsPerSecond <= 0
                ? observedRowsPerSecond
                : (_rowsPerSecond * 0.6) + (observedRowsPerSecond * 0.4);

            // Keep the expected travel between forced samples under a quarter
            // viewport at the current speed.
            var window = _rowsPerSecond < 1
                ? DefaultSampleSkipWindowMilliseconds
                : (int)Math.Round(
                    _frameHeight / 4d / _rowsPerSecond * 1000d);
            Volatile.Write(
                ref _skipWindowMilliseconds,
                Math.Clamp(
                    window,
                    MinimumSkipWindowMilliseconds,
                    DefaultSampleSkipWindowMilliseconds));
        }
    }

    private sealed class ActiveFrameQueueState
    {
        private readonly ScrollCaptureDiagnostics _diagnostics;
        private long _latestSequence;
        private int _queueCount;
        private int _pendingStitchCount;

        public ActiveFrameQueueState(ScrollCaptureDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public long LatestSequence => Volatile.Read(ref _latestSequence);

        public int QueueCount => Math.Max(0, Volatile.Read(ref _queueCount));

        /// <summary>
        /// Captured viewports that have not been stitched or retired yet,
        /// across both the hand-off channel and the matcher backlog. This is
        /// the sampler's backpressure signal.
        /// </summary>
        public int PendingStitchCount =>
            Math.Max(0, Volatile.Read(ref _pendingStitchCount));

        public long ReserveSequence() => Interlocked.Increment(
            ref _latestSequence);

        public void OnEnqueued(QueuedScrollFrame frame)
        {
            _ = frame;
            Interlocked.Increment(ref _queueCount);
            Interlocked.Increment(ref _pendingStitchCount);
        }

        public void OnDequeued()
        {
            Interlocked.Decrement(ref _queueCount);
        }

        public void OnFrameRetired()
        {
            Interlocked.Decrement(ref _pendingStitchCount);
        }

        public void OnDropped(QueuedScrollFrame frame)
        {
            _diagnostics.Record(
                "frame-decimated",
                ("sequence", frame.Sequence),
                ("direction", frame.Motion.Direction.ToString()),
                ("queueAgeMs", Stopwatch.GetElapsedTime(
                    frame.CapturedTimestamp).TotalMilliseconds));
        }
    }


    private static bool IsLikelyBlankFrame(Bitmap frame)
    {
        var whiteSamples = 0;
        var samples = 0;
        var stepX = Math.Max(1, frame.Width / 32);
        var stepY = Math.Max(1, frame.Height / 32);
        var rectangle = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = frame.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            frame.PixelFormat);

        try
        {
            var bytesPerPixel = Image.GetPixelFormatSize(frame.PixelFormat) / 8;
            if (bytesPerPixel < 3)
            {
                return false;
            }

            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * frame.Height];
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0,
                buffer,
                0,
                buffer.Length);

            for (var y = 0; y < frame.Height; y += stepY)
            {
                var row = y * stride;
                for (var x = 0; x < frame.Width; x += stepX)
                {
                    var index = row + (x * bytesPerPixel);
                    var b = buffer[index];
                    var g = buffer[index + 1];
                    var r = buffer[index + 2];
                    if (r >= 245 && g >= 245 && b >= 245)
                    {
                        whiteSamples++;
                    }

                    samples++;
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        return samples > 0 && whiteSamples / (double)samples >= 0.92;
    }

    private static async Task<ScrollCaptureResult> ComposeResultAsync(
        ScrollCaptureComposer composer,
        CancellationToken cancellationToken,
        PreparedCaptureCache? preparedCache = null)
    {
        try
        {
            // BitmapSource creation copies every pixel. Keep both composition and
            // conversion off the dispatcher so the progress window remains
            // responsive while the final image is materialized.
            var image = preparedCache is null
                ? await Task.Run(
                () =>
                {
                    var bitmap = composer.Compose();
                    try
                    {
                        var image = new CapturedImage(bitmap);
                        _ = image.WarmPreview();
                        return image;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                },
                cancellationToken)
                : await preparedCache.TakeLatestOrPrepareAsync(
                    composer,
                    cancellationToken);
            return new ScrollCaptureResult(true, image, ErrorMessage: null);
        }
        catch
        {
            throw;
        }
    }

    private static async Task<ScrollCaptureResult> ComposeControlledResultAsync(
        ControlledScrollCaptureComposer composer,
        CancellationToken cancellationToken)
    {
        var image = await Task.Run(
            () =>
            {
                var bitmap = composer.Compose();
                try
                {
                    var capturedImage = new CapturedImage(bitmap);
                    _ = capturedImage.WarmPreview();
                    return capturedImage;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            },
            cancellationToken);
        return new ScrollCaptureResult(true, image, ErrorMessage: null);
    }

    private static ScrollCaptureOptions CreateSettleOptions(
        ScrollCaptureOptions options,
        int frameHeight)
    {
        var minimumOverlapRows = Math.Clamp(
            Math.Max(options.MinimumOverlapRows, frameHeight / 8),
            1,
            Math.Max(1, frameHeight - 1));
        return options with
        {
            MinimumOverlapRows = minimumOverlapRows,
            MinimumOverlapConfidence = Math.Max(
                options.MinimumOverlapConfidence,
                0.96),
            MinimumNewRows = Math.Max(
                options.MinimumNewRows,
                Math.Min(8, Math.Max(1, frameHeight / 4))),
        };
    }

    private static async Task ReportPreviewAsync(
        ScrollCaptureComposer composer,
        Action<ScrollCapturePreviewState>? previewChanged,
        CancellationToken cancellationToken)
    {
        if (previewChanged is null)
        {
            return;
        }

        var previewState = await Task.Run(
            () =>
            {
                using var previewBitmap = composer.ComposeLivePreview(
                    PreviewMaximumWidth,
                    PreviewMaximumHeight);
                return new ScrollCapturePreviewState(
                    CapturedImage.ToBitmapSource(previewBitmap),
                    composer.FrameCount,
                    composer.AddedAboveFrameCount,
                    composer.AddedBelowFrameCount,
                    composer.OutputWidth,
                    composer.OutputHeight);
            },
            cancellationToken);
        previewChanged(previewState);
    }

    private static async Task ReportControlledPreviewAsync(
        ControlledScrollCaptureComposer composer,
        Action<ScrollCapturePreviewState>? previewChanged,
        CancellationToken cancellationToken)
    {
        if (previewChanged is null)
        {
            return;
        }

        var previewState = await Task.Run(
            () =>
            {
                using var previewBitmap = composer.ComposeLivePreview(
                    PreviewMaximumWidth,
                    PreviewMaximumHeight);
                return new ScrollCapturePreviewState(
                    CapturedImage.ToBitmapSource(previewBitmap),
                    composer.FrameCount,
                    composer.AddedAboveFrameCount,
                    composer.AddedBelowFrameCount,
                    composer.OutputWidth,
                    composer.OutputHeight);
            },
            cancellationToken);
        previewChanged(previewState);
    }

    private sealed class PreparedCaptureCache : IDisposable
    {
        private const int PreparationIntervalMilliseconds = 120;
        // A prepared full-size image is an intentional second copy used to make
        // the Edit/Done transition instant. Above this budget that duplicate is
        // more expensive than the latency it saves, so the final image is built
        // from the composer only once.
        private const long MaximumPreparedPixels = 12_000_000;
        private Task<CapturedImage>? _pendingPreparation;
        private CapturedImage? _latestImage;
        private long _lastPreparationTimestamp;
        private bool _disposed;

        public async Task PrepareIfDueAsync(
            ScrollCaptureComposer composer,
            bool force,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            HarvestCompletedPreparation();
            if (_pendingPreparation is not null)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (!force &&
                now - _lastPreparationTimestamp < PreparationIntervalMilliseconds)
            {
                return;
            }

            var outputPixels = (long)composer.OutputWidth * composer.OutputHeight;
            if (outputPixels > MaximumPreparedPixels)
            {
                _latestImage?.Dispose();
                _latestImage = null;
                _lastPreparationTimestamp = now;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _latestImage?.Dispose();
            _latestImage = null;
            var bitmap = await Task.Run(composer.Compose, cancellationToken);
            _lastPreparationTimestamp = Environment.TickCount64;
            _pendingPreparation = Task.Run(() =>
            {
                try
                {
                    var image = new CapturedImage(bitmap);
                    _ = image.WarmPreview();
                    return image;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            });
        }

        public async Task<CapturedImage> TakeLatestOrPrepareAsync(
            ScrollCaptureComposer composer,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingPreparation is not null)
            {
                var pending = _pendingPreparation;
                _pendingPreparation = null;
                try
                {
                    ReplaceLatest(await pending.WaitAsync(cancellationToken));
                }
                catch
                {
                    ObserveAndDispose(pending);
                    throw;
                }
            }

            if (_latestImage is not null &&
                _latestImage.Bitmap.Height == composer.OutputHeight &&
                _latestImage.Bitmap.Width == composer.OutputWidth)
            {
                var image = _latestImage;
                _latestImage = null;
                return image;
            }

            _latestImage?.Dispose();
            _latestImage = null;
            return await Task.Run(
                () =>
                {
                    var bitmap = composer.Compose();
                    try
                    {
                        var image = new CapturedImage(bitmap);
                        _ = image.WarmPreview();
                        return image;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                },
                cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _latestImage?.Dispose();
            _latestImage = null;
            if (_pendingPreparation is not null)
            {
                ObserveAndDispose(_pendingPreparation);
                _pendingPreparation = null;
            }
        }

        private void HarvestCompletedPreparation()
        {
            if (_pendingPreparation is not { IsCompleted: true } completed)
            {
                return;
            }

            _pendingPreparation = null;
            if (completed.IsCompletedSuccessfully)
            {
                ReplaceLatest(completed.Result);
                return;
            }

            _ = completed.Exception;
        }

        private void ReplaceLatest(CapturedImage image)
        {
            _latestImage?.Dispose();
            _latestImage = image;
        }

        private static void ObserveAndDispose(Task<CapturedImage> preparation)
        {
            _ = preparation.ContinueWith(
                static task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        task.Result.Dispose();
                    }
                    else
                    {
                        _ = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task<System.Drawing.Bitmap?> ScrollAndCaptureFrameAsync(
        ScrollCaptureTarget target,
        ScrollCaptureOptions options,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync,
        CancellationToken cancellationToken,
        bool useWindowMessage = false)
    {
        if (setProgressVisibilityAsync is not null)
        {
            await setProgressVisibilityAsync(false, cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var didScroll = useWindowMessage
                ? ForegroundWindowCaptureService.ScrollWithWindowMessage(
                    target,
                    options.ScrollDelta)
                : ForegroundWindowCaptureService.Scroll(target, options.ScrollDelta);

            if (!didScroll)
            {
                return null;
            }

            await Task.Delay(options.FrameDelayMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ForegroundWindowCaptureService.CaptureScrollTargetRegion(target);
        }
        finally
        {
            if (setProgressVisibilityAsync is not null)
            {
                await setProgressVisibilityAsync(true, CancellationToken.None);
            }
        }
    }
}
