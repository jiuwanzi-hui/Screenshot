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
    // Manual wheel bursts commonly contain short pauses while inertia is still
    // moving the target. Keep the sampler active long enough to observe the
    // next reversal instead of dropping back to idle between packets.
    private const int MinimumActiveScrollWindowMilliseconds = 2000;
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
    private const int ActiveFrameQueueMaximumCapacity = 128;
    private const long ActiveFrameQueueMemoryBudgetBytes = 384L * 1024 * 1024;
    // The hand-off channel and the matcher backlog can both be full at once.
    private const int ActiveFrameBufferCount = 2;
    // Longest stretch backpressure may go without taking a sample when the
    // viewport is moving slowly. SampleCadence shrinks the window with the
    // observed scroll speed: at fling speed a 120ms hole moves the screen by
    // more than one viewport, which no stitcher can bridge afterwards.
    private const int DefaultSampleSkipWindowMilliseconds = 120;
    // The fixed-rate driver is sampled frequently enough that a slow matcher
    // still receives a small-overlap frame instead of a large accumulated jump.
    private const int ControlledCaptureSampleDelayMilliseconds = 40;
    private const int ControlledSettleSampleDelayMilliseconds = 90;
    // A compositor can return the same bitmap for several samples while a
    // smooth scroll is still in flight. Four samples (160 ms) were enough to
    // classify that transient stall as the end of the document. Require a
    // longer stationary run before declaring a boundary; this keeps automatic
    // capture moving through delayed frames while still finishing promptly at
    // a real edge.
    private const int ControlledScrollBoundarySamples = 6;
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
        Action<string?>? previewStatusChanged = null,
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

        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using var context = ScrollCaptureContext.Create(target);
        await using var previewPublisher = new ScrollCapturePreviewPublisher(
            () => ReportPreviewAsync(composer, previewChanged, cancellationToken),
            PreviewMinIntervalMilliseconds, cancellationToken,
            error => previewStatusChanged?.Invoke(error is null ? null : "预览暂时无法更新，正在重试；已截内容仍然保留。"));
        using var preparedCache = new PreparedCaptureCache();
        using var frameGate = new CapturedFrameGate();
        var frameDump = ScrollCaptureFrameDump.Create();
        var diagnostics = new ScrollCaptureDiagnostics();
        Channel<QueuedScrollFrame>? activeFrameQueue = null;
        Task? activeFrameProcessor = null;
        ManualScrollDriver? throttledScrollDriver = null;
        Bitmap? lastMotionProbeFrame = null;

        try
        {
            if (context is not null && (initialFrame is null ||
                await Task.Run(() => context.Seed(initialFrame, target), cancellationToken)))
                target = target with { Context = context };
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

            await previewPublisher.RefreshAsync();

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
            // Manual-wheel mode must keep observing the real viewport while
            // the matcher drains a burst.  The old quarter-full threshold
            // stopped sampling after only a handful of pending frames; a
            // reverse fling then moved far beyond the last captured viewport
            // and the preview appeared frozen until the queue drained.  Keep
            // the early pacing guard for the throttled/automatic driver, but
            // let direct manual capture use the whole bounded queue.  The
            // channel still provides a hard memory bound and motion deltas are
            // carried forward when a write is rejected.
            var backpressureThreshold = directManualWheel
                ? activeFrameCapacity
                : GetBackpressureThreshold(activeFrameCapacity);
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
                previewPublisher,
                preparedCache,
                motionTracker,
                frameQueueState,
                diagnostics,
                activeFrameCapacity,
                sampleCadence,
                throttledScrollDriver,
                cancellationToken);

            var sampleAfterWheel = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Surface a failed matcher instead of filling its abandoned
                // queue and leaving the capture looking permanently frozen.
                if (activeFrameProcessor.IsCompleted) await activeFrameProcessor;
                var completedTask = await WaitForWheelCaptureEventAsync(
                    completionRequested, nextWheelEvent, nextMotionProbe, nextSample,
                    preferSample: !throttleWheelInput && sampleAfterWheel);
                sampleAfterWheel = completedTask == nextWheelEvent;

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
                    // Finish after consuming all overlap bridges, including
                    // the final expansion queued just before the button click.
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
                    await previewPublisher.RefreshAsync();

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
                                previewPublisher: previewPublisher,
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
                                await previewPublisher.RefreshAsync();
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

                    // In direct manual mode each wheel packet is a viewport
                    // observation boundary. Draining the whole channel here
                    // merged a rapid down/up/down burst into one net motion,
                    // so the sampler never captured the intermediate screen
                    // and the later return leg had a missing bridge. Leave
                    // queued packets for the next loop turn; throttled mode
                    // still coalesces requests because its driver emits the
                    // actual paced input separately.
                    if (throttleWheelInput)
                    {
                        while (wheelEvents.TryRead(out var additionalDelta))
                        {
                            if (additionalDelta != 0)
                            {
                                throttledScrollDriver?.QueueCaptureInput(
                                    additionalDelta);
                                throttledRequestedDirection = additionalDelta > 0
                                    ? ScrollCaptureDirection.Up
                                    : ScrollCaptureDirection.Down;
                                observedWheelDelta = (int)Math.Clamp(
                                    (long)observedWheelDelta + additionalDelta,
                                    int.MinValue,
                                    int.MaxValue);
                            }
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
                    if (!throttleWheelInput && wheelDelta != 0)
                    {
                        // Capture the viewport immediately after this packet;
                        // the normal cadence delay remains in effect after the
                        // forced sample. This keeps reverse-fling previews
                        // live without sacrificing the bounded queue.
                        nextSample = Task.CompletedTask;
                    }
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
                    composer,
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

            await previewPublisher.RefreshAsync();
            var composeTimestamp = Stopwatch.GetTimestamp();
            // The final result must always be composed from the current
            // composer state. A prepared image may have been started during
            // the settle/reverse-scroll tail; its dimensions can remain the
            // same when a leading overlap is trimmed and replaced, so a
            // width/height-only cache check can return a stale image that is
            // missing the newly exposed rows above the original anchor.
            // Correctness is more important than avoiding this one final copy.
            var result = await ComposeResultAsync(
                composer,
                cancellationToken);
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
        Action<string?>? previewStatusChanged = null,
        Action<ScrollCaptureDirection?>? recoveryDirectionChanged = null,
        CancellationToken cancellationToken = default)
    {
        return await CaptureControlledCoreAsync(target, completionRequested, pointerActions, options,
            stateChanged, previewChanged, initialFrame, setProgressVisibilityAsync, cancellationToken: cancellationToken,
            previewStatusChanged: previewStatusChanged, recoveryDirectionChanged: recoveryDirectionChanged);
    }

    internal static async Task<ScrollCaptureResult> CaptureControlledCoreAsync(
        ScrollCaptureTarget target,
        Task completionRequested,
        ChannelReader<ScrollCapturePointerAction> pointerActions,
        ScrollCaptureOptions? options = null,
        Action<ControlledScrollCaptureState>? stateChanged = null,
        Action<ScrollCapturePreviewState>? previewChanged = null,
        Bitmap? initialFrame = null,
        Func<bool, CancellationToken, Task>? setProgressVisibilityAsync = null,
        Func<ScrollCaptureTarget, ControlledScrollDriver>? driverFactory = null,
        Action<string?>? previewStatusChanged = null,
        Action<ScrollCaptureDirection?>? recoveryDirectionChanged = null,
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
        using var context = driverFactory is null ? ScrollCaptureContext.Create(target, preferSelectedContent: true) : null;
        var diagnostics = new ScrollCaptureDiagnostics();
        var hasOriginalCursorPosition = ForegroundWindowCaptureService
            .TryGetCursorPosition(out var originalCursorPosition);

        Exception? previewError = null;
        async Task RefreshControlledPreviewAsync()
        {
            var error = await ScrollCapturePreviewPublisher.TryPublishAsync(
                () => ReportControlledPreviewAsync(composer, previewChanged, cancellationToken), cancellationToken);
            if (error is not null || previewError is not null)
                previewStatusChanged?.Invoke(error is null ? null : "预览暂时无法更新，正在重试；已截内容仍然保留。");
            previewError = error;
        }

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
            if (context is not null && (initialFrame is null ||
                await Task.Run(() => context.Seed(initialFrame, target), cancellationToken)))
                target = target with { Context = context };
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
            var fingerprintFixedRows = 0;
            AutomaticViewportFingerprint CreateControlledFingerprint(Bitmap sample)
            {
                var fixedRows = composer.FixedTopRows + composer.FixedBottomRows;
                if (fixedRows != fingerprintFixedRows)
                {
                    initialFingerprint = AutomaticViewportFingerprint.Create(capturedInitialFrame,
                        composer.FixedTopRows, composer.FixedBottomRows);
                    fingerprintFixedRows = fixedRows;
                }
                return AutomaticViewportFingerprint.Create(sample, composer.FixedTopRows, composer.FixedBottomRows);
            }
            await Task.Run(
                () => composer.Initialize(capturedInitialFrame, options),
                cancellationToken);
            await RefreshControlledPreviewAsync();
            var inputTarget = target with
            {
                ScrollInputRegionProvider = () => new ScreenRegion(target.CaptureRegion.X,
                    target.CaptureRegion.Y + composer.FixedTopRows, target.CaptureRegion.Width,
                    target.CaptureRegion.Height - composer.FixedTopRows - composer.FixedBottomRows),
            };
            await using var scrollDriver = driverFactory?.Invoke(inputTarget) ?? new ControlledScrollDriver(inputTarget);

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
            var recoveryBudget = new AutomaticScrollRecoveryBudget(ControlledAutomaticReanchorRounds);
            recoveryBudget.Reset(composer.OutputHeight);
            var reconnectFailuresLogged = 0;
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
            var lastVisibleMovementTimestamp = Stopwatch.GetTimestamp();
            var stateBeforeInputFailure = ControlledScrollCaptureState.ScrollingDown;

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
                if (previousState == ControlledScrollCaptureState.InputUnavailable)
                {
                    // Preserve the failed leg, clear the transient input error,
                    // and reconnect before resuming. Repeated permission errors
                    // still return to InputUnavailable without losing the image.
                    scrollDriver.RetryInput();
                    previousState = GetControlledPausedState(stateBeforeInputFailure);
                    state = previousState;
                }
                var nextState = ApplyControlledPointerAction(state, action);
                if (nextState == state)
                {
                    return;
                }

                boundaryRecoveryAttempts = 0;
                recoveryBudget.Reset(composer.OutputHeight);
                recoveryDirectionChanged?.Invoke(null);
                previewStatusChanged?.Invoke(null);
                lastVisibleMovementTimestamp = Stopwatch.GetTimestamp();

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

            bool HasControlRequest() => completionRequested.IsCompleted || pointerActions.TryPeek(out _);

            void PauseUnlocated()
            {
                if (reconnectFailuresLogged++ < 8)
                    diagnostics.Record("controlled-reconnect-failed", ("state", state.ToString()),
                        ("reason", composer.LastRejectReason), ("width", target.CaptureRegion.Width),
                        ("height", target.CaptureRegion.Height), ("frames", composer.FrameCount),
                        ("outputHeight", composer.OutputHeight), ("movementRows", composer.LastFrameMovementRows));
                SetState(GetControlledPausedState(state));
                previewStatusChanged?.Invoke("暂时无法接回已截内容，已暂停。单击重试，或编辑、完成当前结果。");
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
                    previewStatusChanged?.Invoke("正在核对拼接位置，单击可暂停。");

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
                        previousFingerprint = CreateControlledFingerprint(
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
                        previewStatusChanged?.Invoke(null);
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
                        if (HasControlRequest()) break;
                        if (attempt > 1)
                        {
                            await Task.Delay(
                                ControlledReanchorDelayMilliseconds,
                                cancellationToken);
                        }

                        if (HasControlRequest()) break;

                        using var resumeFrame = await CaptureControlledFrameAsync(
                            target,
                            scrollDriver,
                            setProgressVisibilityAsync,
                            cancellationToken);
                        var resumeFingerprint = CreateControlledFingerprint(
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
                            await RefreshControlledPreviewAsync();
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

                    if (HasControlRequest()) continue;

                    if (!resumeLocated)
                    {
                        // Repeating the same stationary bitmap cannot recover
                        // an animation jump beyond its overlap. Walk back toward
                        // known content, verifying every sample before resuming.
                        var recoveryWheelBudget = (int)Math.Clamp(
                            Math.Max(0, scrollDriver.TotalInputMagnitude - locatedInputMagnitude) * 2 + 240, 240, 360);
                        recoveryDirectionChanged?.Invoke(resumeDirection == ScrollCaptureDirection.Down
                            ? ScrollCaptureDirection.Up : ScrollCaptureDirection.Down);
                        while (recoveryWheelBudget > 0 && !resumeLocated && !HasControlRequest())
                        {
                            var wheelStep = Math.Min(120, recoveryWheelBudget);
                            if (!scrollDriver.StepBackForRecovery(resumeDirection, wheelStep)) break;
                            recoveryWheelBudget -= wheelStep;
                            await Task.Delay(ControlledReanchorDelayMilliseconds * 2, cancellationToken);
                            if (HasControlRequest()) break;
                            using var recoveryFrame = await CaptureControlledFrameAsync(target, scrollDriver,
                                setProgressVisibilityAsync, cancellationToken);
                            previousFingerprint = CreateControlledFingerprint(recoveryFrame);
                            var recoveryAdded = await TryAddControlledFrameAsync(composer, recoveryFrame,
                                resumeDirection, options with { MinimumOverlapConfidence = Math.Max(0.985, options.MinimumOverlapConfidence) },
                                null, cancellationToken,
                                GetControlledSettleMaximumMovementRows(recoveryFrame.Height, options.MinimumOverlapRows),
                                tolerateQuantizedExpectation: true);
                            resumeLocated = IsControlledFrameLocated(recoveryAdded,
                                composer.LastFrameMovementRows, composer.LastRejectReason);
                            if (recoveryAdded) await RefreshControlledPreviewAsync();
                        }
                        recoveryDirectionChanged?.Invoke(null);
                        if (HasControlRequest()) continue;
                        if (resumeLocated)
                        {
                            locatedInputMagnitude = scrollDriver.TotalInputMagnitude;
                            lastSampledInputMagnitude = locatedInputMagnitude;
                            lastVisibleMovementInputMagnitude = locatedInputMagnitude;
                            lastVisibleMovementTimestamp = Stopwatch.GetTimestamp();
                            notchJumpTarget = true;
                        }
                    }
                    if (HasControlRequest()) continue;
                    if (!resumeLocated)
                    {
                        PauseUnlocated();
                    }
                    else
                    {
                        unlocatedProgramSteps = 0;
                        previewStatusChanged?.Invoke(null);
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
                    var settleFingerprint = CreateControlledFingerprint(
                        settleFrame);
                    var settleStationary = previousFingerprint
                        .IsStationaryComparedTo(settleFingerprint);
                    previousFingerprint = settleFingerprint;
                    // Pause/return settle waits for stillness. Aligning* must
                    // not: chat/AI pages keep the fingerprint jittering, and
                    // the confirming return frame was already thrown away once
                    // — gating here left "正在对齐初始位置" up forever while the
                    // viewport had already scrolled past the start.
                    var hasVerifiedContent = composer.FixedTopRows + composer.FixedBottomRows > 0;
                    if (!settleStationary && !isInitialAlignment && !hasVerifiedContent)
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
                    if (hasVerifiedContent)
                        settleStationary = settleLocated && composer.LastFrameMovementRows == 0;
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
                        // A GIF can keep changing while its document position
                        // is stationary. Use registered body motion once bars
                        // are known; waiting for identical frames never ends.
                        settleStationarySamples = settleStationary
                            ? settleStationarySamples + 1 : 0;
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
                        await RefreshControlledPreviewAsync();
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
                    ScrollCapturePosition.Read(capturedInitialFrame) is null &&
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
                    stateBeforeInputFailure = state;
                    SetState(ControlledScrollCaptureState.InputUnavailable);
                    continue;
                }

                using var frame = await CaptureControlledFrameAsync(
                    target,
                    scrollDriver,
                    setProgressVisibilityAsync,
                    cancellationToken);
                var sampledInputMagnitude = scrollDriver.LastCapturedInputMagnitude;
                if (isReturning)
                {
                    returnInputMagnitude = sampledInputMagnitude;
                }
                var inputAdvancedSincePreviousSample =
                    sampledInputMagnitude > lastSampledInputMagnitude;
                lastSampledInputMagnitude = sampledInputMagnitude;
                var fingerprint = CreateControlledFingerprint(frame);
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
                    if (ScrollCapturePosition.Read(capturedInitialFrame) is not null)
                    {
                        // A repeated avatar or blank selection must not stop
                        // the return driver before the measured start position.
                        var relativePosition = ScrollCapturePosition.Difference(capturedInitialFrame, frame);
                        initialReached = relativePosition == 0;
                        crossedInitial = relativePosition is { } measured &&
                            (direction == ScrollCaptureDirection.Up ? measured <= 0 : measured >= 0);
                        initialCrossingConfirmed = crossedInitial;
                        pendingInitialCrossingRows = crossedInitial ? Math.Abs(relativePosition!.Value) : null;
                        scrollDriver.SetDirection(relativePosition is null || crossedInitial ? null : direction,
                            fastReturn: true);
                        if (relativePosition is null)
                        {
                            if (++unlocatedProgramSteps > ControlledReanchorAttempts)
                                SetState(GetControlledPausedState(state));
                        }
                        else unlocatedProgramSteps = 0;
                    }
                    else if (initialReached)
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
                                options,
                                composer.FixedTopRows,
                                composer.FixedBottomRows),
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
                                         initialOverlap.Confidence,
                                         verifiedContent: composer.FixedTopRows + composer.FixedBottomRows > 0) ||
                                     IsControlledInitialCrossingGlide(
                                         previousRows,
                                         pendingInitialCrossingConfidence,
                                         crossingRows,
                                         initialOverlap.Confidence,
                                         verifiedContent: composer.FixedTopRows + composer.FixedBottomRows > 0));
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
                            await RefreshControlledPreviewAsync();
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
                if (!frameLocated && composer.LastRejectReason == "movement-cap-veto")
                {
                    // A quantized wheel step is already in this bitmap. Verify
                    // the larger overlap immediately before pausing input; the
                    // old stop/sleep/recapture cycle made each notch stutter.
                    added = await TryAddControlledFrameAsync(composer, frame, direction,
                        options, expectedRows, cancellationToken,
                        maximumAcceptedNewRows: GetControlledSettleMaximumMovementRows(
                            frame.Height, options.MinimumOverlapRows),
                        tolerateQuantizedExpectation: true);
                    frameLocated = IsControlledFrameLocated(added,
                        composer.LastFrameMovementRows, composer.LastRejectReason);
                    if (frameLocated) notchJumpTarget = true;
                }
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
                        if (HasControlRequest()) break;
                        using var retryFrame = await CaptureControlledFrameAsync(
                            target,
                            scrollDriver,
                            setProgressVisibilityAsync,
                            cancellationToken);
                        sampledInputMagnitude = scrollDriver.LastCapturedInputMagnitude;
                        var retryFingerprint = CreateControlledFingerprint(
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
                            if (HasControlRequest()) break;
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

                if (HasControlRequest()) continue;

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
                    lastVisibleMovementTimestamp = Stopwatch.GetTimestamp();
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

                if ((composer.FixedTopRows + composer.FixedBottomRows > 0 ||
                     ScrollCapturePosition.Read(frame) is not null) && frameLocated)
                    isStationary = composer.LastFrameMovementRows == 0;
                var canConfirmBoundary = CanConfirmControlledBoundary(
                    legHasVisibleMovement,
                    inputAdvancedSincePreviousSample,
                    inputTravelSinceVisibleMovement) &&
                    Stopwatch.GetElapsedTime(lastVisibleMovementTimestamp).TotalMilliseconds >= 1200;
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
                    await RefreshControlledPreviewAsync();
                }

                if (!frameLocated)
                {
                    unlocatedProgramSteps++;
                    if (recoveryBudget.TryRecover(composer.OutputHeight))
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
                    PauseUnlocated();
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
                    lastVisibleMovementTimestamp = Stopwatch.GetTimestamp();
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
                    var completionFingerprint = CreateControlledFingerprint(
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
                    if (composer.FixedTopRows + composer.FixedBottomRows > 0 &&
                        composer.LastFrameMovementRows is { } completionMovement)
                        completionStationary = completionMovement == 0;
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
            if (hasOriginalCursorPosition && driverFactory is null)
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
        if (action == ScrollCapturePointerAction.Pause)
        {
            if (ShouldDeferControlledPointerClicks(state) || IsControlledSettleState(state)) return state;
            action = ScrollCapturePointerAction.Click;
        }
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
            ControlledScrollCaptureState.BottomReached =>
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
            ControlledScrollCaptureState.FinalTopReached =>
                ControlledScrollCaptureState.ScrollingUp,
            ControlledScrollCaptureState.ScrollingUpFirst =>
                ControlledScrollCaptureState.PreparingPauseUpFirst,
            ControlledScrollCaptureState.PreparingPauseUpFirst =>
                ControlledScrollCaptureState.ScrollingUpFirst,
            ControlledScrollCaptureState.PausedUpFirst =>
                ControlledScrollCaptureState.ScrollingUpFirst,
            ControlledScrollCaptureState.TopReached =>
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
            ControlledScrollCaptureState.FinalBottomReached =>
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
            ControlledScrollCaptureState.PausedDownSecond or
            ControlledScrollCaptureState.BottomReached or
            ControlledScrollCaptureState.TopReached or
            ControlledScrollCaptureState.FinalTopReached or
            ControlledScrollCaptureState.FinalBottomReached;
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
        ScrollCaptureOptions options,
        int fixedTopRows = 0,
        int fixedBottomRows = 0)
    {
        var fixedRows = fixedTopRows + fixedBottomRows;
        if (fixedRows > 0)
        {
            using var initialContent = new ManualScrollFrame(initialFrame);
            using var returnContent = new ManualScrollFrame(returnFrame);
            var contentMatch = initialContent.Locate(returnContent, options.MinimumOverlapRows,
                fixedTopRows, fixedBottomRows);
            if (contentMatch is null) return null;
            var shift = contentMatch.Value.Shift;
            var movedRows = Math.Abs(shift);
            if (movedRows < options.MinimumNewRows ||
                (shift < 0) != (returnDirection == ScrollCaptureDirection.Up) ||
                Math.Abs(movedRows - expectedCrossingRows) > Math.Max(48, (returnFrame.Height - fixedRows) / 3))
                return null;
            // Keep the service's displacement calculation in raw viewport
            // units: raw height - reported overlap still equals body movement.
            return new ImageOverlapMatch(returnFrame.Height - movedRows, contentMatch.Value.Confidence);
        }
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
        double confidence,
        bool verifiedContent = false)
    {
        // Spatial verification intentionally tolerates animated tiles. Require
        // two registrations at the same pixel position for its stable case.
        return confidence >= (verifiedContent ? 0.94 : ControlledInitialCrossingMinimumConfidence) &&
            currentInputMagnitude == previousInputMagnitude &&
            Math.Abs(currentMovementRows - previousMovementRows) <= (verifiedContent ? 0 : 24);
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
        double currentConfidence,
        bool verifiedContent = false)
    {
        return previousConfidence >= (verifiedContent ? 0.94 : ControlledInitialCrossingMinimumConfidence) &&
            currentConfidence >= (verifiedContent ? 0.94 : ControlledInputReturnMinimumConfidence) &&
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
            return await Task.Run(() => ForegroundWindowCaptureService.CaptureScrollTargetRegion(target),
                cancellationToken);
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
            return await Task.Run(scrollDriver.CaptureFrame, cancellationToken);
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
    // Keep the live map responsive during a reverse fling. The preview cache is
    // incremental, so 80 ms is frequent enough to look continuous without
    // rebuilding the full-resolution result for every captured frame.
    private const int PreviewMinIntervalMilliseconds = 80;

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

    internal static Task<Task> WaitForWheelCaptureEventAsync(Task completion, Task wheel,
        Task motionProbe, Task sample, bool preferSample)
    {
        // A slow capture can overrun every sample timer. Always observe Finish,
        // and alternate a ready wheel packet with its forced sample instead of
        // starving wheel input for seconds (or starving Finish indefinitely).
        if (completion.IsCompleted) return Task.FromResult(completion);
        if (preferSample && sample.IsCompleted) return Task.FromResult(sample);
        return Task.WhenAny(completion, wheel, motionProbe, sample);
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
        ScrollCaptureComposer composer,
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
        // primary reported cause of a stalled stitch. The composer decides
        // whether the pixels contain enough evidence for a continuous seam.
        if (!frameGate.HasChanged(frame) &&
            !frameGate.ShouldRetainBlankMotion(motionTracker.HasPendingInput,
                composer.FixedTopRows, composer.FixedBottomRows))
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
        ScrollCapturePreviewPublisher previewPublisher,
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
                        chainState,
                        motionTracker,
                        options);
                }

                if (backlog.Count == 0)
                {
                    if (!await reader.WaitToReadAsync(cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                await ProcessNextBacklogFrameAsync(
                    backlog,
                    chainState,
                    composer,
                    options,
                    previewChanged,
                    previewPublisher,
                    preparedCache,
                    motionTracker,
                    queueState,
                    diagnostics,
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
        BacklogChainState chainState,
        ScrollWheelMotionTracker motionTracker,
        ScrollCaptureOptions options)
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

        // The reader only tops up to capacity. No input-based decimation is
        // allowed: wheel estimates cannot prove that discarded pixels overlap.
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
        ScrollCapturePreviewPublisher previewPublisher,
        PreparedCaptureCache preparedCache,
        ScrollWheelMotionTracker motionTracker,
        ActiveFrameQueueState queueState,
        ScrollCaptureDiagnostics diagnostics,
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

        // Consume every queued viewport in capture order, including before a
        // boundary marker and after Finish. These are the only overlap bridges.
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
            previewPublisher: previewPublisher,
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
                    await previewPublisher.RefreshAsync();
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
        ScrollCapturePreviewPublisher? previewPublisher,
        PreparedCaptureCache? preparedCache,
        ScrollWheelMotionTracker? motionTracker,
        bool prepareResult,
        ScrollCaptureDiagnostics? diagnostics,
        long queuedSequence,
        long capturedTimestamp,
        CancellationToken cancellationToken)
    {
        var processingTimestamp = Stopwatch.GetTimestamp();
        var wasTrackingLost = composer.LastRejectReason is not null && composer.LastFrameMovementRows is null;
        var wasCrossingBlank = composer.IsCrossingManualBlank;
        ImageOverlapMatch? overlapMatch = null;
        // Manual wheels and precision touchpads can report sub-notch motion
        // (often 1–7 rows).  The normal minimum-new-rows guard is tuned for
        // automatic captures and used to drop those samples, leaving gaps
        // when the user scrolled slowly or changed direction frequently.  A
        // measured wheel displacement is independent evidence, so allow a
        // one-row commit for these samples and let the overlap matcher keep
        // the chain pixel-accurate.  Controlled scrolling already applies the
        // same rule in TryAddControlledFrameAsync.
        var effectiveOptions = motion.ExpectedRows is > 0 &&
            motion.ExpectedRows < options.MinimumNewRows
            ? options with { MinimumNewRows = 1 }
            : options;
        var wasAdded = await Task.Run(
            () => composer.TryAddFrame(
                frame,
                motion.Direction,
                effectiveOptions,
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

        var isTrackingLost = composer.LastRejectReason is not null && composer.LastFrameMovementRows is null;
        if (previewChanged is not null && (wasAdded || forcePreview || wasTrackingLost != isTrackingLost ||
                wasCrossingBlank != composer.IsCrossingManualBlank))
        {
            if (previewPublisher is not null)
            {
                // Throttle rendering, never discard an expansion request.
                // The last request is rendered even if no further frame arrives.
                previewPublisher.Request();
            }
            else
            {
                await ReportPreviewAsync(composer, previewChanged, cancellationToken);
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
        ScrollCapturePreviewPublisher? previewPublisher = null,
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
            previewPublisher,
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
                return composer.CreateLivePreviewState(PreviewMaximumWidth, PreviewMaximumHeight);
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
