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
    private const int PreviewMaximumHeight = 520;
    private const int ActiveFrameQueueMaximumCapacity = 8;
    private const long ActiveFrameQueueMemoryBudgetBytes = 64L * 1024 * 1024;
    private const int CompletionTailFrameCount = 3;

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
            activeFrameQueue = Channel.CreateBounded<QueuedScrollFrame>(
                new BoundedChannelOptions(GetActiveFrameQueueCapacity(
                    target.CaptureRegion))
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

                    frameQueueState.BeginCompletion();
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

    private const int PreviewMinIntervalMilliseconds = 16;

    private static int GetActiveFrameQueueCapacity(ScreenRegion region)
    {
        var frameBytes = Math.Max(
            1L,
            (long)region.Width * region.Height * 4L);
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
        CancellationToken cancellationToken)
    {
        await foreach (var queuedFrame in reader.ReadAllAsync(cancellationToken))
        {
            queueState.OnDequeued();
            using (queuedFrame)
            {
                if (queueState.ShouldSkipOnCompletion(
                        queuedFrame.Sequence,
                        CompletionTailFrameCount))
                {
                    continue;
                }

                if (composer.FrameCount >= options.MaximumFrames)
                {
                    continue;
                }

                await ProcessCapturedFrameForDirectionAsync(
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
            }
        }
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

    private sealed record QueuedScrollFrame(
        Bitmap Frame,
        ScrollWheelMotionSample Motion,
        long Sequence,
        long CapturedTimestamp,
        bool PrepareResult) : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }

    private sealed class ActiveFrameQueueState
    {
        private readonly ScrollCaptureDiagnostics _diagnostics;
        private long _latestSequence;
        private int _queueCount;
        private int _isCompleting;

        public ActiveFrameQueueState(ScrollCaptureDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public long LatestSequence => Volatile.Read(ref _latestSequence);

        public int QueueCount => Math.Max(0, Volatile.Read(ref _queueCount));

        public long ReserveSequence() => Interlocked.Increment(
            ref _latestSequence);

        public void BeginCompletion() => Volatile.Write(
            ref _isCompleting,
            1);

        public void OnEnqueued(QueuedScrollFrame frame)
        {
            Interlocked.Increment(ref _queueCount);
        }

        public void OnDequeued()
        {
            Interlocked.Decrement(ref _queueCount);
        }

        public void OnDropped(QueuedScrollFrame frame)
        {
            Interlocked.Decrement(ref _queueCount);
            _diagnostics.Record(
                "frame-dropped",
                ("sequence", frame.Sequence),
                ("direction", frame.Motion.Direction.ToString()),
                ("queueAgeMs", Stopwatch.GetElapsedTime(
                    frame.CapturedTimestamp).TotalMilliseconds));
        }

        public bool ShouldSkipOnCompletion(long sequence, int tailFrameCount)
        {
            if (Volatile.Read(ref _isCompleting) == 0)
            {
                return false;
            }

            var latest = Volatile.Read(ref _latestSequence);
            return sequence < latest - Math.Max(0, tailFrameCount - 1);
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
