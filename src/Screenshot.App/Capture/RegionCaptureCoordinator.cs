using System.Net.Http;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Capture;

public sealed class RegionCaptureCoordinator
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly CaptureHistoryService _historyService;
    private readonly PinnedImageManager _pinnedImageManager;
    private readonly ITranslationCredentialStore _translationCredentialStore;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _statusReporter;
    private readonly Action<bool>? _mouseShortcutSuppressionChanged;
    private readonly Action<VideoRecordingPreferences>?
        _videoRecordingPreferencesChanged;
    private readonly Action<VideoRecordingAnnotationPreferences>?
        _videoRecordingAnnotationPreferencesChanged;
    private readonly Action<ArrowStyle>? _arrowStyleChanged;
    private readonly Action<ArrowToolMode>? _arrowToolModeChanged;
    private readonly Action<ShapeToolMode>? _shapeToolModeChanged;
    private readonly Action<AnnotationToolMode>? _lastAnnotationToolChanged;
    private readonly Action<string>? _customStrokeColorChanged;
    private readonly Action<int[]>? _customColorPaletteChanged;
    private readonly Action<double, double>? _captureToolbarPositionChanged;
    private readonly Action? _showVideoHistory;
    private readonly Action? _recordingAlreadyInProgressFeedback;
    private bool _isCaptureInProgress;
    private bool _isRecordingInProgress;
    private ScreenRegion? _lastOrdinaryCaptureRegion;

    public RegionCaptureCoordinator(
        Func<AppSettings> settingsProvider,
        CaptureHistoryService historyService,
        PinnedImageManager pinnedImageManager,
        ITranslationCredentialStore translationCredentialStore,
        HttpClient httpClient,
        Action<string> statusReporter,
        Action<bool>? mouseShortcutSuppressionChanged = null,
        Action<VideoRecordingPreferences>? videoRecordingPreferencesChanged = null,
        Action<VideoRecordingAnnotationPreferences>?
            videoRecordingAnnotationPreferencesChanged = null,
        Action<ArrowStyle>? arrowStyleChanged = null,
        Action<ArrowToolMode>? arrowToolModeChanged = null,
        Action<ShapeToolMode>? shapeToolModeChanged = null,
        Action<AnnotationToolMode>? lastAnnotationToolChanged = null,
        Action<string>? customStrokeColorChanged = null,
        Action<int[]>? customColorPaletteChanged = null,
        Action<double, double>? captureToolbarPositionChanged = null,
        Action? showVideoHistory = null,
        Action? recordingAlreadyInProgressFeedback = null)
    {
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(pinnedImageManager);
        ArgumentNullException.ThrowIfNull(translationCredentialStore);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(statusReporter);

        _settingsProvider = settingsProvider;
        _historyService = historyService;
        _pinnedImageManager = pinnedImageManager;
        _translationCredentialStore = translationCredentialStore;
        _httpClient = httpClient;
        _statusReporter = statusReporter;
        _mouseShortcutSuppressionChanged = mouseShortcutSuppressionChanged;
        _videoRecordingPreferencesChanged = videoRecordingPreferencesChanged;
        _videoRecordingAnnotationPreferencesChanged =
            videoRecordingAnnotationPreferencesChanged;
        _arrowStyleChanged = arrowStyleChanged;
        _arrowToolModeChanged = arrowToolModeChanged;
        _shapeToolModeChanged = shapeToolModeChanged;
        _lastAnnotationToolChanged = lastAnnotationToolChanged;
        _customStrokeColorChanged = customStrokeColorChanged;
        _customColorPaletteChanged = customColorPaletteChanged;
        _captureToolbarPositionChanged = captureToolbarPositionChanged;
        _showVideoHistory = showVideoHistory;
        _recordingAlreadyInProgressFeedback = recordingAlreadyInProgressFeedback;
    }

    public Task RequestCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: false,
            trackOrdinaryCaptureRegion: true,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null,
            deferInitialColorPickerActivation);
    }

    public Task RequestOcrCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: true,
            translateTextAfterSelection: false,
            trackOrdinaryCaptureRegion: false,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null,
            deferInitialColorPickerActivation);
    }

    public Task RequestTranslationCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: true,
            trackOrdinaryCaptureRegion: false,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null,
            deferInitialColorPickerActivation);
    }

    public event Action<bool>? CaptureStateChanged;

    internal bool IsRecordingInProgress => _isRecordingInProgress;

    public async Task RequestVideoRecordingAsync(
        CapturePointerContinuation? pointerContinuation = null)
    {
        if (_isRecordingInProgress)
        {
            ReportRecordingAlreadyInProgress();
            return;
        }

        if (_isCaptureInProgress)
        {
            return;
        }

        ScreenRegion? selection = null;
        CapturedImage? overlaySnapshot = null;
        SetCaptureInProgress(true);
        try
        {
            await WaitForCaptureChromeToHideAsync();
            // Opening the region picker used to let its constructor take the
            // first full-screen snapshot on the dispatcher. That is most
            // noticeable on the first video hotkey after boot, when GDI/DWM
            // has not been touched yet. Keep the picker responsive by using
            // the same background snapshot handoff as ordinary screenshots.
            try
            {
                overlaySnapshot = await Task.Run(() =>
                {
                    var snapshot = ScreenCaptureService.Capture(
                        VirtualScreen.GetBounds());
                    _ = snapshot.WarmPreview();
                    return snapshot;
                });
            }
            catch
            {
                // The overlay retains its own synchronous fallback for the
                // rare case where a desktop snapshot is temporarily unavailable.
            }

            selection = await CaptureOverlayWindow.SelectAsync(
                pointerContinuation,
                overlaySnapshot);
            overlaySnapshot = null;
        }
        catch (Exception exception)
        {
            ReportRecordingStartFailure(exception);
        }
        finally
        {
            overlaySnapshot?.Dispose();
            SetCaptureInProgress(false);
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation);
        }

        if (selection is null)
        {
            return;
        }

        try
        {
            await RunVideoRecordingSessionAsync(selection.Value);
        }
        catch (Exception exception)
        {
            ReportRecordingStartFailure(exception);
        }
    }

    public async Task RequestFloatingCaptureAsync(
        FloatingCaptureClickBehavior behavior)
    {
        switch (behavior)
        {
            case FloatingCaptureClickBehavior.RegionCapture:
                await RequestCaptureAsync();
                return;
            case FloatingCaptureClickBehavior.VideoRecording:
                await RequestVideoRecordingAsync();
                return;
            case FloatingCaptureClickBehavior.ScrollCapture:
                await RequestScrollCaptureAsync();
                return;
            case FloatingCaptureClickBehavior.PinCapture:
                await RequestPinCaptureAsync();
                return;
            case FloatingCaptureClickBehavior.CaptureAllScreens:
                await RequestAllScreensCaptureAsync();
                return;
        }

        var region = TryGetReusableRegion(
            _lastOrdinaryCaptureRegion,
            VirtualScreen.GetBounds());
        if (region is null)
        {
            await RequestCaptureAsync();
            return;
        }

        if (behavior == FloatingCaptureClickBehavior.ShowSelection)
        {
            await RequestInteractiveCaptureAsync(
                recognizeTextAfterSelection: false,
                translateTextAfterSelection: false,
                trackOrdinaryCaptureRegion: true,
                initialScreenSnapshot: null,
                pointerContinuation: null,
                initialSelection: region.Value);
            return;
        }

        await CaptureImmediatelyAsync(
            region.Value,
            trackOrdinaryCaptureRegion: true,
            showFeedbackOnEveryScreen: false);
    }

    public Task RequestAllScreensCaptureAsync()
    {
        return CaptureImmediatelyAsync(
            VirtualScreen.GetBounds(),
            trackOrdinaryCaptureRegion: false,
            showFeedbackOnEveryScreen: true,
            captureFactory: ScreenCaptureService.CaptureAllScreens);
    }

    internal static ScreenRegion? TryGetReusableRegion(
        ScreenRegion? candidate,
        ScreenRegion virtualScreen)
    {
        if (candidate is not ScreenRegion region ||
            region.IsEmpty ||
            virtualScreen.IsEmpty)
        {
            return null;
        }

        return ScreenRegion.Intersect(region, virtualScreen) == region
            ? region
            : null;
    }

    private async Task CaptureImmediatelyAsync(
        ScreenRegion region,
        bool trackOrdinaryCaptureRegion,
        bool showFeedbackOnEveryScreen,
        Func<CapturedImage>? captureFactory = null)
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        SetCaptureInProgress(true);
        try
        {
            await WaitForCaptureChromeToHideAsync();
            var settings = _settingsProvider();
            using var image = captureFactory?.Invoke() ??
                ScreenCaptureService.Capture(region);
            await ClipboardImageService.SetImageAsync(image.Preview);
            _ = _historyService.Add(
                image,
                Math.Max(1, settings.HistoryLimit));
            if (trackOrdinaryCaptureRegion)
            {
                _lastOrdinaryCaptureRegion = region;
            }

            if (showFeedbackOnEveryScreen)
            {
                await Task.WhenAll(System.Windows.Forms.Screen.AllScreens.Select(
                    screen => CaptureFeedbackWindow.ShowAsync(new ScreenRegion(
                        screen.Bounds.X,
                        screen.Bounds.Y,
                        screen.Bounds.Width,
                        screen.Bounds.Height))));
            }
            else
            {
                await CaptureFeedbackWindow.ShowAsync(region);
            }
        }
        finally
        {
            SetCaptureInProgress(false);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private async Task RequestInteractiveCaptureAsync(
        bool recognizeTextAfterSelection,
        bool translateTextAfterSelection,
        bool trackOrdinaryCaptureRegion,
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation,
        ScreenRegion? initialSelection,
        bool deferInitialColorPickerActivation = false)
    {
        using var timing = CaptureTimingDiagnostics.Begin(
            "coordinator-interactive",
            $"snapshot={(initialScreenSnapshot is not null)}");
        if (_isCaptureInProgress)
        {
            initialScreenSnapshot?.Dispose();
            return;
        }

        using (CaptureTimingDiagnostics.Begin("set-capture-in-progress"))
        {
            SetCaptureInProgress(true);
        }
        try
        {
            var deferInitialScreenCapture =
                deferInitialColorPickerActivation && initialScreenSnapshot is null;
            if (initialScreenSnapshot is null &&
                !deferInitialColorPickerActivation)
            {
                CaptureTimingDiagnostics.Mark("before-capture-chrome-yield");
                await WaitForCaptureChromeToHideAsync();
                CaptureTimingDiagnostics.Mark("after-capture-chrome-yield");
            }
            var settings = _settingsProvider();
            // A hotkey can arrive before the low-level pre-capture worker has
            // finished. Capture the fallback snapshot off the dispatcher so a
            // cold/idle first wake-up never blocks the UI thread in GDI.
            if (initialScreenSnapshot is null && !deferInitialScreenCapture)
            {
                try
                {
                    CaptureTimingDiagnostics.Mark("before-gdi-capture");
                    initialScreenSnapshot = await Task.Factory.StartNew(() =>
                    {
                        var previousPriority = Thread.CurrentThread.Priority;
                        try
                        {
                            Thread.CurrentThread.Priority =
                                ThreadPriority.BelowNormal;
                        }
                        catch
                        {
                        }

                        try
                        {
                            using var captureTiming =
                                CaptureTimingDiagnostics.Begin("gdi-capture-worker");
                            var snapshot = ScreenCaptureService.Capture(
                                VirtualScreen.GetBounds());
                            _ = snapshot.WarmPreview();
                            return snapshot;
                        }
                        finally
                        {
                            try
                            {
                                Thread.CurrentThread.Priority = previousPriority;
                            }
                            catch
                            {
                            }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    CaptureTimingDiagnostics.Mark("after-gdi-capture");
                }
                catch
                {
                    // CaptureOverlayWindow keeps its own fallback path when
                    // the best-effort snapshot cannot be obtained.
                }
            }
            CaptureTimingDiagnostics.Mark("before-show-interactive");
            CaptureOverlayWindow.ShowInteractive(
                new CaptureOverlayOptions
                {
                    SaveDirectory = settings.SaveDirectory,
                    PngSaveLocationMode = settings.PngSaveLocationMode,
                    ScreenshotScalePercent = settings.ScreenshotScalePercent,
                    CompletionHotKey = settings.CompleteCaptureHotKey,
                    KeepHistory = true,
                    HistoryLimit = settings.HistoryLimit,
                    HistoryService = _historyService,
                    PinnedImageManager = _pinnedImageManager,
                    TranslationTargetLanguage = settings.TranslationTargetLanguage,
                    RecognitionResultPresentation = settings.RecognitionResultPresentation,
                    StartOcrAsync = ShowOcrResultAndCompleteCaptureAsync,
                    RecognizeTextAsync = RecognizeTextAsync,
                    TranslateTextAsync = TranslateTextAsync,
                    ShowRecognitionPopupAsync = ShowRecognitionPopupAsync,
                    RecognizeFormulaAsync = RecognizeFormulaAsync,
                    RecognizeTextAfterSelection = recognizeTextAfterSelection,
                    TranslateTextAfterSelection = translateTextAfterSelection,
                    InitialPointerContinuation = pointerContinuation,
                    StartScrollCaptureAsync = RequestScrollCaptureFromSelectionAsync,
                    StartScrollCaptureWithSnapshotAsync =
                        RequestScrollCaptureFromSelectionWithSnapshotAsync,
                    StartVideoRecordingAsync = RequestVideoRecordingFromSelectionAsync,
                    InitialSelection = initialSelection,
                    CaptureCompleted = trackOrdinaryCaptureRegion
                        ? region => _lastOrdinaryCaptureRegion = region
                        : null,
                    CaptureClosed = OnInteractiveCaptureClosed,
                    ArrowStyle = settings.ArrowStyle,
                    ArrowToolMode = settings.ArrowToolMode,
                    ShapeToolMode = settings.ShapeToolMode,
                    LastAnnotationTool = settings.LastAnnotationTool,
                    VisibleToolbarFeatures =
                        settings.VisibleCaptureToolbarFeatures.ToArray(),
                    ToolbarFeatureOrder =
                        settings.CaptureToolbarFeatureOrder.ToArray(),
                    ToolbarRows = settings.CaptureToolbarRows,
                    ToolbarScalePercent = settings.ToolbarScalePercent,
                    DeferInitialColorPickerActivation =
                        deferInitialColorPickerActivation,
                    DeferInitialScreenCapture = deferInitialScreenCapture,
                    ArrowStyleChanged = _arrowStyleChanged,
                    ArrowToolModeChanged = _arrowToolModeChanged,
                    ShapeToolModeChanged = _shapeToolModeChanged,
                    LastAnnotationToolChanged = _lastAnnotationToolChanged,
                    CustomStrokeColor = settings.CustomStrokeColor,
                    CustomStrokeColorChanged = _customStrokeColorChanged,
                    CustomColorPalette = settings.CustomColorPalette,
                    CustomColorPaletteChanged = _customColorPaletteChanged,
                    ToolbarPositionXRatio =
                        settings.CaptureToolbarPositionXRatio,
                    ToolbarPositionYRatio =
                        settings.CaptureToolbarPositionYRatio,
                    ToolbarPositionChanged = _captureToolbarPositionChanged,
                },
                initialScreenSnapshot);
        }
        catch
        {
            initialScreenSnapshot?.Dispose();
            SetCaptureInProgress(false);
            throw;
        }

    }

    public async Task RequestPinCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false)
    {
        if (_isCaptureInProgress)
        {
            initialScreenSnapshot?.Dispose();
            return;
        }

        SetCaptureInProgress(true);

        try
        {
            await WaitForCaptureChromeToHideAsync();
            CapturedImage? overlaySnapshot = initialScreenSnapshot?.Clone();
            ScreenRegion? selection;
            try
            {
                selection = await CaptureOverlayWindow.SelectAsync(
                    pointerContinuation,
                    overlaySnapshot,
                    deferInitialColorPickerActivation);
                overlaySnapshot = null;
            }
            finally
            {
                overlaySnapshot?.Dispose();
            }

            if (selection is null)
            {
                return;
            }

            // The selection overlay is intentionally a lightweight mode: after
            // the user releases the mouse, capture the selected pixels and hand
            // ownership directly to the pin manager without opening a preview.
            var image = initialScreenSnapshot is null
                ? ScreenCaptureService.Capture(selection.Value)
                : CropCapturedImage(initialScreenSnapshot, selection.Value);
            _pinnedImageManager.Pin(image);
        }
        finally
        {
            initialScreenSnapshot?.Dispose();
            SetCaptureInProgress(false);
        }
    }

    private static CapturedImage CropCapturedImage(
        CapturedImage source,
        ScreenRegion selection)
    {
        var sourceRegion = source.SourceRegion ?? VirtualScreen.GetBounds();
        var crop = new Rectangle(
            selection.X - sourceRegion.X,
            selection.Y - sourceRegion.Y,
            selection.Width,
            selection.Height);
        crop.Intersect(new Rectangle(0, 0, source.Bitmap.Width, source.Bitmap.Height));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("截图选区超出屏幕范围。");
        }

        return new CapturedImage(
            source.Bitmap.Clone(crop, source.Bitmap.PixelFormat),
            ScreenRegion.FromCorners(
                selection.X,
                selection.Y,
                selection.X + crop.Width,
                selection.Y + crop.Height));
    }

    private void OnInteractiveCaptureClosed()
    {
        SetCaptureInProgress(false);
        // The overlay held a frozen full-desktop snapshot plus the capture
        // bitmaps; return that memory now so the tray idle stays small.
        Core.MemoryFootprint.TrimAfterHeavyOperation();
    }

    public Task RequestScrollCaptureAsync(
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestScrollCaptureAsync(
            initialSelection: null,
            pointerContinuation);
    }

    private Task RequestScrollCaptureFromSelectionAsync(ScreenRegion selection)
    {
        return RequestScrollCaptureAsync(
            selection,
            pointerContinuation: null);
    }

    private Task RequestScrollCaptureFromSelectionWithSnapshotAsync(
        ScreenRegion selection,
        CapturedImage? initialScreenSnapshot)
    {
        return RequestScrollCaptureAsync(
            selection,
            pointerContinuation: null,
            initialScreenSnapshot);
    }

    private Task<OcrRecognitionResult> RecognizeTextAsync(CapturedImage image)
    {
        return OcrProviderFactory.RecognizeAsync(
            image,
            _settingsProvider());
    }

    private async Task<TranslationSegmentsResult> TranslateTextAsync(
        OcrRecognitionResult recognition)
    {
        var settings = _settingsProvider();
        var provider = TranslationProviderFactory.Create(
            settings,
            _translationCredentialStore,
            _httpClient,
            // Screenshot translation is a user-visible blocking operation;
            // use the fast offline beam for this workflow.
            preferFastOffline: true);
        try
        {
            return await provider.TranslateSegmentsAsync(
                recognition.Regions.Select(region => region.Text).ToArray(),
                "auto",
                settings.TranslationTargetLanguage);
        }
        finally
        {
            // Translation models and OCR buffers are native/heavy allocations;
            // return their retired pages without blocking the overlay thread.
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation);
        }
    }

    private Task<ContentRecognitionResult> RecognizeFormulaAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        return FormulaRecognitionService.RecognizeAsync(
            image,
            _settingsProvider(),
            _translationCredentialStore,
            _httpClient,
            cancellationToken);
    }

    private async Task RequestScrollCaptureAsync(
        ScreenRegion? initialSelection,
        CapturePointerContinuation? pointerContinuation,
        CapturedImage? initialScreenSnapshot = null)
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        SetCaptureInProgress(true);

        try
        {
            // When long screenshot is launched from an existing capture
            // selection, the toolbar/overlay has just been closed by the
            // caller. Waiting the full chrome-settle delay here exposes the
            // desktop for one visible compositor interval and produces a
            // flash. A fresh shortcut still needs the delay so its floating
            // chrome cannot be included in the initial frame.
            if (initialSelection is null)
            {
                await WaitForCaptureChromeToHideAsync();
            }
            // Scroll target is resolved under the selection when scrolling starts.
            var scrollSelection = initialSelection is null
                ? await CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    pointerContinuation)
                : await CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    initialSelection.Value,
                    initialScreenSnapshot);
            initialScreenSnapshot = null;

            if (scrollSelection is null)
            {
                return;
            }

            try
            {
                using var cancellationSource = new CancellationTokenSource();
                var completionSource = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var progressWindow = new ScrollCaptureProgressWindow();
                var editRequested = false;
                var cancellationRequested = 0;

                void CompleteCapture(object? sender, EventArgs eventArgs)
                {
                    completionSource.TrySetResult();
                }

                void CancelCapture(object? sender, EventArgs eventArgs)
                {
                    CancelSession();
                }

                void CancelFromSelection()
                {
                    CancelSession();
                }

                void CancelSession()
                {
                    if (Interlocked.Exchange(ref cancellationRequested, 1) != 0)
                    {
                        return;
                    }

                    // Remove both visual surfaces immediately. Background frame
                    // processing observes the token and unwinds independently.
                    progressWindow.CloseFromCoordinator();
                    scrollSelection.Dispose();
                    cancellationSource.Cancel();
                }

                void EditCapture(object? sender, EventArgs eventArgs)
                {
                    editRequested = true;
                    completionSource.TrySetResult();
                }

                progressWindow.CompleteRequested += CompleteCapture;
                progressWindow.EditRequested += EditCapture;
                progressWindow.CancelRequested += CancelCapture;
                scrollSelection.CancelRequested += CancelFromSelection;
                progressWindow.Owner = scrollSelection.OverlayWindow;
                var initialImage = scrollSelection.CaptureSnapshot();
                var initialRegion = scrollSelection.CaptureRegion;
                progressWindow.ConfigureForCaptureRegion(initialRegion);
                new System.Windows.Interop.WindowInteropHelper(progressWindow)
                    .EnsureHandle();
                progressWindow.ExcludeFromScreenCapture();
                progressWindow.TryPositionOutside(initialRegion);
                // Position and size the preview before showing it. Showing the
                // window first lets WPF place it at the default origin for one
                // compositor frame, which appears as a flash on the left side.
                progressWindow.Show();
                progressWindow.BringToFront();
                UpdateProgress(
                    progressWindow,
                    new ScrollCapturePreviewState(
                        initialImage.Preview,
                        1,
                        0,
                        0,
                        initialImage.Bitmap.Width,
                        initialImage.Bitmap.Height));

                try
                {
                    var captureMode = _settingsProvider().ScrollCaptureMode;
                    // Commit the selected region before installing the pointer
                    // hook. Once capture starts, the region becomes a control
                    // surface: click pauses/resumes and double-click reverses.
                    await scrollSelection.LockForScrollingAsync(
                        cancellationSource.Token);
                    progressWindow.ConfigureForCaptureRegion(
                        scrollSelection.CaptureRegion);
                    progressWindow.TryPositionOutside(
                        scrollSelection.CaptureRegion);
                    // Capture the settled selection once after the drag has
                    // completed. A full-screen GDI snapshot for every pointer
                    // move made manual mode compete with high-report-rate
                    // cursors and was unrelated to stitching.
                    var settledSelectionImage = scrollSelection.CaptureSnapshot();
                    var previousInitialImage = initialImage;
                    initialImage = settledSelectionImage;
                    previousInitialImage.Dispose();
                    using var wheelMonitor = new ScrollCaptureWheelMonitor(
                        initialRegion,
                        wheelDetected: null,
                        (x, y) => progressWindow.ContainsScreenPoint(x, y),
                        CancelFromSelection);
                    if (captureMode != ScrollCaptureMode.ManualWheel)
                    {
                        // Own the physical wheel as soon as the hook exists.
                        // Otherwise a wheel gesture during target discovery or
                        // first-frame preparation can leak into the page before
                        // automatic mode enters its state machine.
                        wheelMonitor.EnableControlledCaptureInput();
                    }
                    scrollSelection.CaptureRegionChanged += region =>
                    {
                        wheelMonitor.UpdateCaptureRegion(region);
                    };

                    ScrollCaptureTarget? target = null;
                    progressWindow.Owner = null;
                    async Task SetProgressVisibilityAsync(
                        bool isVisible,
                        CancellationToken token)
                    {
                        if (cancellationSource.IsCancellationRequested)
                        {
                            return;
                        }

                        // The preview is marked WDA_EXCLUDEFROMCAPTURE, so it can
                        // remain visible over wide selections without introducing
                        // a hide/show flash or contaminating the captured image.
                        await progressWindow.Dispatcher.InvokeAsync(
                            progressWindow.BringToFront,
                            DispatcherPriority.Send,
                            token);
                    }
                    await scrollSelection.SetVisibleAsync(
                        isVisible: false,
                        cancellationSource.Token);
                    try
                    {
                        // WindowFromPoint cannot see through a layered WPF
                        // window owned by another process. Hide the overlay for
                        // one render turn while resolving the real target.
                        await Task.Delay(30, cancellationSource.Token);
                        _ = ForegroundWindowCaptureService
                            .TryCreateScrollCaptureTargetFromSelection(
                                scrollSelection.CaptureRegion,
                                out target);
                    }
                    finally
                    {
                        await scrollSelection.SetVisibleAsync(
                            isVisible: true,
                            CancellationToken.None);
                        if (!cancellationSource.IsCancellationRequested)
                        {
                            progressWindow.Owner = scrollSelection.OverlayWindow;
                            progressWindow.BringToFront();
                        }
                    }

                    if (target is null)
                    {
                        _statusReporter("无法识别选区下的可滚动窗口。");
                        return;
                    }

                    // Activate the window under the selection so controlled
                    // wheel input reaches the correct scroll viewer.
                    _ = ForegroundWindowCaptureService.TryFocusScrollTarget(target);
                    await Task.Delay(30, cancellationSource.Token);

                    // Capture region equals the user selection. Prefer the
                    // pre-scroll snapshot so content before the first controlled
                    // step is retained.
                    var firstFrame = ScrollCaptureService.CreateInitialFrame(
                        initialImage.Bitmap,
                        scrollSelection.CaptureRegion,
                        target.CaptureRegion);
                    UpdateProgress(
                        progressWindow,
                        new ScrollCapturePreviewState(
                            CapturedImage.ToBitmapSource(firstFrame),
                            1,
                            0,
                            0,
                            firstFrame.Width,
                            firstFrame.Height));
                    ScrollCaptureResult result;
                    if (captureMode == ScrollCaptureMode.ManualWheel)
                    {
                        // Manual mode is deliberately unthrottled: the user's
                        // physical wheel drives the target directly at any
                        // speed and in either direction, while the hook only
                        // observes deltas to pace sampling and stitching.
                        // Right-click cancel still needs the pointer block.
                        wheelMonitor.BlockNonWheelInput();
                        progressWindow.ConfigureManualWheelMode();
                        result = await ScrollCaptureService.CaptureOnWheelAsync(
                            target,
                            completionSource.Task,
                            wheelMonitor.WheelEvents,
                            previewChanged: previewState =>
                                UpdateProgress(progressWindow, previewState),
                            // The progress window is excluded from screen
                            // capture. Passing a visibility callback here
                            // would marshal every sampled frame through the
                            // WPF dispatcher just to bring an already
                            // visible window to the front. Manual mode
                            // must leave the target's input/UI thread
                            // untouched between wheel events.
                            setPreviewVisibilityAsync: null,
                            throttleWheelInput: false,
                            // Manual mode is driven exclusively by the
                            // low-level wheel channel. Do not probe the
                            // viewport while the pointer is idle: each
                            // probe captures and clones the whole region,
                            // which makes high-report-rate mouse movement
                            // contend with GDI/bitmap memory bandwidth.
                            enableViewportMotionFallback: false,
                            initialFrame: firstFrame,
                            cancellationToken: cancellationSource.Token);
                    }
                    else
                    {
                        // Clicks in motion states mean "pause" and must feel
                        // immediate; only idle states still need the
                        // double-click disambiguation delay.
                        var latestControlledState =
                            (int)ControlledScrollCaptureState.WaitingToStart;
                        wheelMonitor.ConfigureClickDeferral(
                            () => ScrollCaptureService
                                .ShouldDeferControlledPointerClicks(
                                    (ControlledScrollCaptureState)Volatile.Read(
                                        ref latestControlledState)));
                        progressWindow.QueueInteractionState(
                            ControlledScrollCaptureState.WaitingToStart);
                        result = await ScrollCaptureService.CaptureControlledAsync(
                            target,
                            completionSource.Task,
                            wheelMonitor.PointerActions,
                            stateChanged: interactionState =>
                            {
                                Volatile.Write(
                                    ref latestControlledState,
                                    (int)interactionState);
                                progressWindow.QueueInteractionState(
                                    interactionState);
                            },
                            previewChanged: previewState =>
                                UpdateProgress(progressWindow, previewState),
                            setProgressVisibilityAsync: SetProgressVisibilityAsync,
                            initialFrame: firstFrame,
                            cancellationToken: cancellationSource.Token);
                    }
                    var image = result.Image;

                    if (!result.IsSuccess || image is null)
                    {
                        _statusReporter(result.ErrorMessage ?? "滚动截图失败。");
                        return;
                    }

                    CapturedImage? completedImage = image;
                    try
                    {
                        var settings = _settingsProvider();
                        if (editRequested)
                        {
                            // Start the history copy on a worker before handing
                            // ownership to the editor. Cloning a tall bitmap on
                            // the dispatcher blocked the first editor paint for
                            // seconds even though history encoding itself was
                            // already asynchronous.
                            var editorImage = completedImage;
                            var historyCloneTask = Task.Run(editorImage.Clone);
                            var editor = new ImageEditorWindow(
                                editorImage,
                                settings.SaveDirectory,
                                settings.ArrowStyle,
                                _arrowStyleChanged,
                                settings.CustomStrokeColor,
                                _customStrokeColorChanged,
                                settings.CustomColorPalette,
                                _customColorPaletteChanged,
                                arrowToolMode: settings.ArrowToolMode,
                                arrowToolModeChanged: _arrowToolModeChanged,
                                shapeToolMode: settings.ShapeToolMode,
                                shapeToolModeChanged: _shapeToolModeChanged,
                                lastAnnotationTool: settings.LastAnnotationTool,
                                lastAnnotationToolChanged: _lastAnnotationToolChanged,
                                recognizeTextAsync: RecognizeTextAsync,
                                pngSaveLocationMode: settings.PngSaveLocationMode);
                            editor.Show();
                            completedImage = null;

                            if (historyCloneTask is not null)
                            {
                                var historyLimit = settings.HistoryLimit;
                                var historyService = _historyService;
                                // Build the WPF surface off the UI thread, then
                                // insert the history entry on the dispatcher so
                                // the bound collection stays thread-safe without
                                // delaying the editor open.
                                _ = Task.Run(() =>
                                {
                                    CapturedImage? historyImage = null;
                                    try
                                    {
                                        historyImage = historyCloneTask
                                            .GetAwaiter()
                                            .GetResult();
                                        _ = historyImage.WarmPreview();
                                        var preparedHistoryImage = historyImage;
                                        historyImage = null;
                                        _ = System.Windows.Application.Current
                                            .Dispatcher
                                            .BeginInvoke(
                                                DispatcherPriority.Background,
                                                () =>
                                                {
                                                    try
                                                    {
                                                        _ = historyService.Add(
                                                            preparedHistoryImage,
                                                            historyLimit);
                                                    }
                                                    finally
                                                    {
                                                        preparedHistoryImage.Dispose();
                                                    }
                                                });
                                    }
                                    catch
                                    {
                                        historyImage?.Dispose();
                                    }
                                });
                            }
                        }
                        else
                        {
                            var historyItem = _historyService.Add(
                                completedImage,
                                settings.HistoryLimit);
                            try
                            {
                                await ClipboardImageService.SetImageAsync(
                                    completedImage.Preview,
                                    cancellationSource.Token);
                                historyItem?.MarkCopied();
                            }
                            catch (ExternalException)
                            {
                                _statusReporter("剪贴板正被其他程序使用，请重试。");
                            }
                        }
                    }
                    finally
                    {
                        completedImage?.Dispose();
                    }
                }
                finally
                {
                    initialImage.Dispose();
                    progressWindow.CompleteRequested -= CompleteCapture;
                    progressWindow.EditRequested -= EditCapture;
                    progressWindow.CancelRequested -= CancelCapture;
                    scrollSelection.CancelRequested -= CancelFromSelection;
                    progressWindow.CloseFromCoordinator();
                }

            }
            finally
            {
                scrollSelection.Dispose();
            }
        }
        finally
        {
            initialScreenSnapshot?.Dispose();
            SetCaptureInProgress(false);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private void WirePreviewActions(CapturePreviewWindow preview)
    {
        preview.EditRequested += (_, _) =>
        {
            var editor = new ImageEditorWindow(
                preview.CloneImage(),
                _settingsProvider().SaveDirectory,
                _settingsProvider().ArrowStyle,
                _arrowStyleChanged,
                _settingsProvider().CustomStrokeColor,
                _customStrokeColorChanged,
                _settingsProvider().CustomColorPalette,
                _customColorPaletteChanged,
                arrowToolMode: _settingsProvider().ArrowToolMode,
                arrowToolModeChanged: _arrowToolModeChanged,
                shapeToolMode: _settingsProvider().ShapeToolMode,
                shapeToolModeChanged: _shapeToolModeChanged,
                lastAnnotationTool: _settingsProvider().LastAnnotationTool,
                lastAnnotationToolChanged: _lastAnnotationToolChanged,
                recognizeTextAsync: RecognizeTextAsync,
                pngSaveLocationMode: _settingsProvider().PngSaveLocationMode);
            editor.Show();
            preview.Close();
        };
        preview.PinRequested += (_, _) =>
        {
            _pinnedImageManager.Pin(preview.CloneImage());
        };
        preview.OcrRequested += async (_, _) =>
        {
            var image = preview.CloneImage();
            preview.Close();
            await ShowOcrResultAndCompleteCaptureAsync(image);
        };
    }

    private async Task ShowOcrResultAndCompleteCaptureAsync(CapturedImage image)
    {
        try
        {
            using (image)
            {
                await ShowOcrResultAsync(image);
            }
        }
        catch (Exception)
        {
            _statusReporter("文字识别失败，请检查语言设置。");
        }
        finally
        {
            SetCaptureInProgress(false);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private async Task ShowOcrResultAsync(CapturedImage capturedImage)
    {
        var settings = _settingsProvider();
        var result = await OcrProviderFactory.RecognizeAsync(
            capturedImage,
            settings);
        if (settings.RecognitionResultPresentation ==
            RecognitionResultPresentationMode.Popup)
        {
            await ShowRecognitionPopupAsync(
                "已识别",
                result.Text,
                null);
            return;
        }

        var window = new OcrResultWindow(
            result,
            _settingsProvider,
            _translationCredentialStore,
            _httpClient);
        window.Show();
    }

    private Task ShowRecognitionPopupAsync(
        string title,
        string sourceText,
        string? translatedText)
    {
        var window = new RecognitionResultPopupWindow(
            title,
            sourceText,
            translatedText,
            closeAfterCopy: true);
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    private async Task RequestVideoRecordingFromSelectionAsync(
        ScreenRegion selection)
    {
        SetCaptureInProgress(false);
        try
        {
            await RunVideoRecordingSessionAsync(selection);
        }
        catch (Exception exception)
        {
            ReportRecordingStartFailure(exception);
        }
        finally
        {
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation);
        }
    }

    private async Task RunVideoRecordingSessionAsync(ScreenRegion selection)
    {
        if (_isRecordingInProgress)
        {
            ReportRecordingAlreadyInProgress();
            return;
        }

        _isRecordingInProgress = true;
        try
        {
            // The selection overlay has already been closed by the caller.
            // Only yield one render turn here; the full chrome-settle delay
            // would expose the desktop for 80 ms before the recording window
            // appears, which is visible as a flash.
            await Dispatcher.Yield(DispatcherPriority.Render);
            var settings = _settingsProvider();
            var result = await VideoRecordingControlWindow.ShowSessionAsync(
                selection,
                settings.VideoSaveDirectory,
                settings.RecordSystemAudio,
                settings.RecordMicrophone,
                settings.VideoRecordingCodec,
                settings.VideoRecordingFrameRate,
                settings.ShowKeyboardInputInRecording,
                settings.ShowMouseInputInRecording,
                settings.ShowMouseTrailInRecording,
                settings.ShowCameraInRecording,
                settings.RecordingOutputFormat,
                microphoneDeviceId: settings.MicrophoneDeviceId,
                cameraDeviceId: settings.CameraDeviceId,
                recordingPreferencesChanged: _videoRecordingPreferencesChanged,
                annotationPreferences: new VideoRecordingAnnotationPreferences(
                    settings.ShapeToolMode,
                    settings.ArrowToolMode,
                    settings.ArrowStyle,
                    string.IsNullOrWhiteSpace(settings.CustomStrokeColor)
                        ? settings.DefaultStrokeColor
                        : settings.CustomStrokeColor,
                    settings.DefaultStrokeWidth),
                recordingAnnotationPreferencesChanged:
                    _videoRecordingAnnotationPreferencesChanged,
                customColorPalette: settings.CustomColorPalette,
                customColorPaletteChanged: _customColorPaletteChanged,
                endRecordingHotKey: settings.EndVideoRecordingHotKey,
                toolbarScalePercent: settings.ToolbarScalePercent);
            if (result.IsSuccess)
            {
                var completedSettings = _settingsProvider();
                var savedVideoPath = result.FilePath;
                if (result.OpenEditor)
                {
                    _statusReporter($"视频已保存：{result.FilePath}");
                    var editor = new VideoPostProcessWindow(result.FilePath!);
                    editor.Closed += (_, _) => _showVideoHistory?.Invoke();
                    editor.Show();
                }
                else if (completedSettings.RecordingOutputFormat ==
                    VideoRecordingOutputFormat.Gif)
                {
                    _statusReporter("正在生成 GIF 动图...");
                    try
                    {
                        var duration = await VideoPostProcessingService
                            .GetDurationAsync(result.FilePath!);
                        var gifPath = await VideoPostProcessingService
                            .ExportAnimatedImageAsync(
                                result.FilePath!,
                                TimeSpan.Zero,
                                duration,
                                AnimatedImageFormat.Gif);
                        File.Delete(result.FilePath!);
                        savedVideoPath = gifPath;
                        _statusReporter($"GIF 已保存：{gifPath}");
                    }
                    catch (Exception exception)
                    {
                        _statusReporter(
                            $"GIF 生成失败，已保留 MP4：{exception.Message}");
                    }
                }
                else
                {
                    _statusReporter($"视频已保存：{result.FilePath}");
                }

                if (!string.IsNullOrWhiteSpace(savedVideoPath) &&
                    File.Exists(savedVideoPath))
                {
                    try
                    {
                        await ClipboardFileService.SetFileAsync(savedVideoPath);
                    }
                    catch
                    {
                        // Saving the recording must succeed even if another
                        // application temporarily owns the clipboard.
                    }
                }

                VideoHistoryService.ApplyRetentionPolicy(
                    completedSettings.VideoSaveDirectory,
                    completedSettings.VideoHistoryRetentionDays,
                    completedSettings.VideoHistoryLimit);
                if (!result.OpenEditor)
                {
                    _showVideoHistory?.Invoke();
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                _statusReporter($"视频录制失败：{result.ErrorMessage}");
            }
        }
        finally
        {
            _isRecordingInProgress = false;
        }
    }

    private void ReportRecordingAlreadyInProgress()
    {
        _statusReporter("正在录屏，不能同时开始另一个录屏任务。");
        _ = VideoRecordingControlWindow.TryShowAlreadyRecordingFeedback();
        _recordingAlreadyInProgressFeedback?.Invoke();
    }

    private void ReportRecordingStartFailure(Exception exception)
    {
        var rootException = exception.GetBaseException();
        var detail = string.IsNullOrWhiteSpace(rootException.Message)
            ? rootException.GetType().Name
            : rootException.Message.Trim();
        _statusReporter($"无法开始区域录制：{detail}");
    }

    private void SetCaptureInProgress(bool isInProgress)
    {
        if (_isCaptureInProgress == isInProgress)
        {
            return;
        }

        _isCaptureInProgress = isInProgress;
        _mouseShortcutSuppressionChanged?.Invoke(isInProgress);
        CaptureStateChanged?.Invoke(isInProgress);
    }

    private static async Task WaitForCaptureChromeToHideAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static void UpdateProgress(
        ScrollCaptureProgressWindow progressWindow,
        ScrollCapturePreviewState previewState)
    {
        progressWindow.QueuePreview(previewState);
    }

}
