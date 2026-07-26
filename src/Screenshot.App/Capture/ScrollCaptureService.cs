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
    // Completion should feel immediate. A bounded tail still captures common
    // smooth-scroll inertia, while the idle break below avoids doing every
    // possible sample when the viewport has already stopped.
    private const int CompletionSettleMilliseconds = 120;
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
    private const int ActiveFrameQueueMaximumCapacity = 48;
    private const long ActiveFrameQueueMemoryBudgetBytes = 96L * 1024 * 1024;
    // The hand-off channel and the matcher backlog can both be full at once.
    private const int ActiveFrameBufferCount = 2;
    // Finishing has to feel quick, but a deep backlog after a final fling is
    // real content the user scrolled past: at a few tens of milliseconds per
    // chain step, this budget drains a full backlog rather than discarding it.
    private const int CompletionDrainBudgetMilliseconds = 1200;
    // Longest stretch backpressure may go without taking a sample. Bounds the
    // scroll distance between retained samples when smooth-scroll inertia
    // moves the screen without wheel input the travel estimate could see.
    private static readonly TimeSpan MaximumSampleSkipWindow =
        TimeSpan.FromMilliseconds(120);

    public static async Task<ScrollCaptureResult> CaptureOnWheelAsync(
        ScrollCaptureTarget target,
        Task completionRequested,
        ChannelReader<int> wheelEvents,
        ScrollCaptureOptions? options = null,
        Func<bool, CancellationToken, Task>? setPreviewVisibilityAsync = null,
        Action<ScrollCapturePreviewState>? previewChanged = null,
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
            var lastWheelEventTimestamp = 0L;
            var lastDiagnosticWheelTimestamp = 0L;
            var completionRequestedTimestamp = 0L;
            var previewTimestampSlot = new long[1];
            var activeScrollWindow = TimeSpan.FromMilliseconds(Math.Max(
                MinimumActiveScrollWindowMilliseconds,
                options.FrameDelayMilliseconds * 4));
            var nextWheelEvent = wheelEvents.ReadAsync(
                cancellationToken).AsTask();
            var nextSample = Task.Delay(
                options.FrameDelayMilliseconds,
                cancellationToken);
            var activeFrameCapacity = GetActiveFrameQueueCapacity(
                target.CaptureRegion);
            var backpressureThreshold = Math.Max(4, activeFrameCapacity / 4);
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
                cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completedTask = await Task.WhenAny(
                    completionRequested,
                    nextWheelEvent,
                    nextSample);

                if (completedTask == completionRequested)
                {
                    await completionRequested;
                    var completionTimestamp = Stopwatch.GetTimestamp();
                    completionRequestedTimestamp = completionTimestamp;
                    diagnostics.Record(
                        "completion-requested",
                        ("queuedFrames", frameQueueState.QueueCount),
                        ("latestSequence", frameQueueState.LatestSequence),
                        ("outputHeight", composer.OutputHeight));

                    // Drain late wheel events so the settle pass uses the
                    // freshest scroll direction (including reverse).
                    while (wheelEvents.TryRead(out var lateDelta))
                    {
                        if (lateDelta != 0)
                        {
                            motionTracker.AddDelta(lateDelta);
                            activeDirection = motionTracker.Direction;
                            lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                        }
                    }

                    activeFrameQueue.Writer.TryComplete();
                    await activeFrameProcessor;
                    activeFrameProcessor = null;
                    diagnostics.Record(
                        "queue-drained",
                        ("durationMs", Stopwatch.GetElapsedTime(
                            completionTimestamp).TotalMilliseconds),
                        ("frameCount", composer.FrameCount),
                        ("outputHeight", composer.OutputHeight));

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
                    }

                    break;
                }

                if (completedTask == nextWheelEvent)
                {
                    var wheelDelta = await nextWheelEvent;

                    if (wheelDelta != 0)
                    {
                        motionTracker.AddDelta(wheelDelta);
                    }

                    while (wheelEvents.TryRead(out var additionalDelta))
                    {
                        if (additionalDelta != 0)
                        {
                            motionTracker.AddDelta(additionalDelta);
                        }
                    }

                    if (motionTracker.HasPendingInput)
                    {
                        activeDirection = motionTracker.Direction;
                        lastWheelEventTimestamp = Stopwatch.GetTimestamp();
                        var wheelInterval = lastDiagnosticWheelTimestamp == 0L
                            ? 0d
                            : Stopwatch.GetElapsedTime(
                                lastDiagnosticWheelTimestamp).TotalMilliseconds;
                        lastDiagnosticWheelTimestamp = lastWheelEventTimestamp;
                        diagnostics.Record(
                            "wheel",
                            ("direction", activeDirection.Value.ToString()),
                            ("pendingDelta", motionTracker.PendingDelta),
                            ("intervalMs", wheelInterval));
                    }

                    nextWheelEvent = wheelEvents.ReadAsync(
                        cancellationToken).AsTask();
                    continue;
                }

                var elapsedSinceWheel = lastWheelEventTimestamp == 0L
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(lastWheelEventTimestamp);
                var sampleDelayMilliseconds = elapsedSinceWheel <
                    TimeSpan.FromMilliseconds(80)
                    ? options.FrameDelayMilliseconds
                    : Math.Max(
                        options.FrameDelayMilliseconds,
                        SettlingSampleDelayMilliseconds);
                nextSample = Task.Delay(
                    sampleDelayMilliseconds,
                    cancellationToken);

                if (activeDirection is null ||
                    elapsedSinceWheel > activeScrollWindow)
                {
                    continue;
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
                var mustSample =
                    estimatedPendingRows >= target.CaptureRegion.Height / 4 ||
                    lastSampleTimestamp == 0L ||
                    Stopwatch.GetElapsedTime(lastSampleTimestamp) >=
                        MaximumSampleSkipWindow;
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
                    diagnostics,
                    prepareResult: elapsedSinceWheel >=
                        TimeSpan.FromMilliseconds(80),
                    cancellationToken: cancellationToken);
            }

            // Prefer a usable result over hard failure. A single frame is still
            // the selected region; partial stitches are better than an error tip.
            if (composer.FrameCount < 1)
            {
                return ScrollCaptureResult.Failure("滚动截图失败。");
            }

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
        catch (Exception)
        {
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
            return ForegroundWindowCaptureService.CaptureRegion(target.CaptureRegion);
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
        ScrollCaptureDiagnostics diagnostics,
        bool prepareResult,
        CancellationToken cancellationToken)
    {
        var captureTimestamp = Stopwatch.GetTimestamp();
        var frame = await CaptureFrameAsync(
            target,
            setPreviewVisibilityAsync,
            cancellationToken);
        if (IsLikelyBlankFrame(frame))
        {
            frame.Dispose();
            return;
        }

        if (!frameGate.HasChanged(frame))
        {
            frame.Dispose();
            diagnostics.Record(
                "frame-skipped-stationary",
                ("captureMs", Stopwatch.GetElapsedTime(
                    captureTimestamp).TotalMilliseconds));
            return;
        }

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
                while (reader.TryRead(out var queuedFrame))
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
                    previewTimestampSlot,
                    preparedCache,
                    motionTracker,
                    queueState,
                    diagnostics,
                    drainDeadlineTimestamp,
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

        while (backlog.Count > capacity && backlog.Count >= 3)
        {
            var index = ScrollFrameSelection.SelectDecimationIndex(backlog.Count);
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
            // Out of budget: keep only the freshest viewport so the final
            // image still ends where the user stopped scrolling.
            CarryBacklogMotion(
                backlog,
                backlog.Count - 1,
                motionTracker,
                options);
            DisposeBacklog(backlog, backlog.Count - 1, queueState);
        }

        var queuedFrame = backlog[0];
        var wasAdded = await ProcessCapturedFrameForDirectionAsync(
            queuedFrame.Frame,
            composer,
            options,
            queuedFrame.Motion,
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
            ("reject", composer.LastRejectReason),
            ("boundaryDrift", composer.LastBoundaryDriftRows),
            ("boundaryConfidence", composer.LastBoundaryConfidence),
            ("frameCount", composer.FrameCount),
            ("outputHeight", composer.OutputHeight));

        if (motion.HasFreshInput &&
            composer.LastFrameMovementRows is { } movementRows)
        {
            motionTracker?.ObserveMovement(movementRows, motion.Delta);
        }

        if (previewChanged is not null)
        {
            var now = Environment.TickCount64;
            var shouldRefreshPreview = forcePreview ||
                previewTimestampSlot is not { Length: > 0 } ||
                now - previewTimestampSlot[0] >=
                    (wasAdded
                        ? PreviewMinIntervalMilliseconds
                        : PreviewMinIntervalMilliseconds * 2);

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
        if (IsLikelyBlankFrame(frame))
        {
            return false;
        }

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
            bool prepareResult)
        {
            Frame = frame;
            Motion = motion;
            Sequence = sequence;
            CapturedTimestamp = capturedTimestamp;
            PrepareResult = prepareResult;
        }

        public Bitmap Frame { get; }

        public ScrollWheelMotionSample Motion { get; set; }

        public long Sequence { get; }

        public long CapturedTimestamp { get; }

        public bool PrepareResult { get; }

        public void Dispose() => Frame.Dispose();
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
                        return new CapturedImage(bitmap);
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
                    return new CapturedImage(bitmap);
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
                        return new CapturedImage(bitmap);
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
            return ForegroundWindowCaptureService.CaptureRegion(target.CaptureRegion);
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
