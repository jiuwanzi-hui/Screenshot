using System.Net.Http;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;
using Screenshot.App.Watermarks;

namespace Screenshot.App.Capture;

public sealed partial class RegionCaptureCoordinator : IDisposable
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly CaptureHistoryService _historyService;
    private readonly PinnedImageManager _pinnedImageManager;
    private readonly ITranslationCredentialStore _translationCredentialStore;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _statusReporter;
    private readonly Action<TranslationProviderKind, bool, string>? _translationAvailabilityReporter;
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
    private readonly Action<CaptureAspectRatio>? _captureAspectRatioChanged;
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
        Action? recordingAlreadyInProgressFeedback = null,
        Action<TranslationProviderKind, bool, string>? translationAvailabilityReporter = null,
        Action<CaptureAspectRatio>? captureAspectRatioChanged = null)
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
        _translationAvailabilityReporter = translationAvailabilityReporter;
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
        _captureAspectRatioChanged = captureAspectRatioChanged;
        _showVideoHistory = showVideoHistory;
        _recordingAlreadyInProgressFeedback = recordingAlreadyInProgressFeedback;
    }

    public Task RequestCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false,
        ScreenRegion? initialSelection = null,
        Action? capturePresented = null,
        IntPtr residentSelectionWindow = default)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: false,
            trackOrdinaryCaptureRegion: true,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection,
            deferInitialColorPickerActivation,
            capturePresented,
            residentSelectionWindow);
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
    internal ScreenRegion? LastOrdinaryCaptureRegion
    {
        get => _lastOrdinaryCaptureRegion;
        set => _lastOrdinaryCaptureRegion = value;
    }

    public async Task RequestVideoRecordingAsync(
        CapturePointerContinuation? pointerContinuation = null,
        ScreenRegion? initialSelection = null,
        Func<Task>? selectionPresented = null)
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

        ScreenRegion? selection = initialSelection;
        CapturedImage? overlaySnapshot = null;
        SetCaptureInProgress(true);
        try
        {
            if (selection is null)
            {
                await WaitForCaptureChromeToHideAsync();
                selection = await CaptureOverlayWindow.SelectAsync(
                    pointerContinuation,
                    overlaySnapshot,
                    deferInitialColorPickerActivation: false,
                    deferInitialScreenCapture: pointerContinuation is null);
                overlaySnapshot = null;
            }
        }
        catch (Exception exception)
        {
            await ReportRecordingStartFailureAsync(exception);
        }
        finally
        {
            overlaySnapshot?.Dispose();
            if (initialSelection is null || selection is null) SetCaptureInProgress(false);
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation, CancellationToken.None);
        }

        if (selection is null)
        {
            return;
        }

        try
        {
            await RunVideoRecordingSessionAsync(selection.Value, selectionPresented);
        }
        catch (OperationCanceledException) { SetCaptureInProgress(false); }
        catch (Exception exception)
        {
            await ReportRecordingStartFailureAsync(exception);
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
            using var watermarked = settings.Watermark.AutoApply
                ? await ApplyAutomaticWatermarkAsync(image, settings.Watermark) : null;
            var output = watermarked ?? image;
            await ClipboardImageService.SetImageAsync(output.Preview);
            _ = _historyService.Add(
                output,
                Math.Max(1, settings.HistoryLimit));
            if (trackOrdinaryCaptureRegion)
            {
                _lastOrdinaryCaptureRegion = region;
            }

            if (showFeedbackOnEveryScreen)
            {
                await Task.WhenAll(System.Windows.Forms.Screen.AllScreens.Select(
                    screen => CaptureSuccessWindow.ShowAsync(new ScreenRegion(
                        screen.Bounds.X,
                        screen.Bounds.Y,
                        screen.Bounds.Width,
                        screen.Bounds.Height))));
            }
            else
            {
                await CaptureSuccessWindow.ShowAsync(region);
            }
        }
        finally
        {
            SetCaptureInProgress(false);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
        }
    }

    private async Task<CapturedImage> ApplyAutomaticWatermarkAsync(CapturedImage image, WatermarkSettings settings)
    {
        var result = await new WatermarkLoader().LoadAsync(settings, DateTimeOffset.Now, CancellationToken.None);
        if (!string.IsNullOrEmpty(result.Warning)) _statusReporter(result.Warning);
        return CapturedImage.FromBitmapSource(result.Composition.RenderOn(image.Preview), image.SourceRegion);
    }

    private async Task RequestInteractiveCaptureAsync(
        bool recognizeTextAfterSelection,
        bool translateTextAfterSelection,
        bool trackOrdinaryCaptureRegion,
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation,
        ScreenRegion? initialSelection,
        bool deferInitialColorPickerActivation = false,
        Action? capturePresented = null,
        IntPtr residentSelectionWindow = default)
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
            // Keyboard sessions acquire their first frame on the overlay's
            // background worker before Show, preserving browser hover cards.
            // The input dispatcher remains free and Escape can cancel while
            // the hidden session waits. Mouse continuations keep their handoff.
            var deferInitialScreenCapture =
                pointerContinuation is null &&
                initialSelection is null;
            if (initialScreenSnapshot is null &&
                !deferInitialScreenCapture)
            {
                CaptureTimingDiagnostics.Mark("before-capture-chrome-yield");
                await WaitForCaptureChromeToHideAsync();
                CaptureTimingDiagnostics.Mark("after-capture-chrome-yield");
            }
            var settings = _settingsProvider();
            if ((recognizeTextAfterSelection || translateTextAfterSelection) &&
                settings.OcrEngine == OcrEngineMode.PaddleOcrV6)
            {
                // Start model loading while the user is still selecting the
                // region. The native work is serialized with recognition and
                // runs on a background thread, so the first result does not
                // pay the model-init cost after the selection is complete.
                _ocrSelectionPreparation?.Cancel();
                _ocrSelectionPreparation?.Dispose();
                _ocrSelectionPreparation = new();
                _ocrPreparationUsed = false;
                _ = HighQualityOcrService.PrewarmAsync(_ocrSelectionPreparation.Token);
            }
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
            var options = CreateInteractiveOptions(settings, recognizeTextAfterSelection, translateTextAfterSelection,
                trackOrdinaryCaptureRegion, pointerContinuation, initialSelection,
                deferInitialColorPickerActivation, deferInitialScreenCapture, capturePresented, residentSelectionWindow);
            var prepared = TakePreparedCaptureToolbar(settings, trackOrdinaryCaptureRegion &&
                initialScreenSnapshot is not null && initialSelection is not null && residentSelectionWindow != IntPtr.Zero);
            CaptureTimingDiagnostics.Mark("toolbar-preparation-result", $"reused={prepared is not null}");
            await CaptureOverlayWindow.BeginInteractive(options, initialScreenSnapshot, prepared);
        }
        catch
        {
            initialScreenSnapshot?.Dispose();
            _ocrSelectionPreparation?.Cancel();
            _ocrSelectionPreparation?.Dispose();
            _ocrSelectionPreparation = null;
            SetCaptureInProgress(false);
            throw;
        }

    }

    public async Task RequestPinCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null,
        bool deferInitialColorPickerActivation = false,
        ScreenRegion? initialSelection = null,
        Func<Task>? selectionPresented = null)
    {
        if (_isCaptureInProgress)
        {
            initialScreenSnapshot?.Dispose();
            return;
        }

        SetCaptureInProgress(true);

        try
        {
            ScreenRegion? selection = initialSelection;
            if (selection is null)
            {
                await WaitForCaptureChromeToHideAsync();
                CapturedImage? overlaySnapshot = initialScreenSnapshot?.Clone();
                try
                {
                    selection = await CaptureOverlayWindow.SelectAsync(
                        pointerContinuation,
                        overlaySnapshot,
                        deferInitialColorPickerActivation,
                        deferInitialScreenCapture: pointerContinuation is null);
                    overlaySnapshot = null;
                }
                finally { overlaySnapshot?.Dispose(); }
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
            var pin = _pinnedImageManager.PinAndGetWindow(image);
            if (selectionPresented is not null)
            {
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    await selectionPresented();
                }
                catch
                {
                    pin.Close(); // Cancelled/stale handoffs never leave an unwanted pin.
                    throw;
                }
            }
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

    private CancellationTokenSource? _ocrSelectionPreparation;
    private bool _ocrPreparationUsed;

    public void Dispose()
    {
        DiscardPreparedCaptureToolbar();
        DiscardPreparedRecordingToolbar();
        _ocrSelectionPreparation?.Cancel();
        _ocrSelectionPreparation?.Dispose();
        _ocrSelectionPreparation = null;
        OcrWorkerProcess.CancelUnusedPreparation();
    }

    private void OnInteractiveCaptureClosed()
    {
        if (!_ocrPreparationUsed)
        {
            _ocrSelectionPreparation?.Cancel();
            OcrWorkerProcess.CancelUnusedPreparation();
        }
        _ocrSelectionPreparation?.Dispose();
        _ocrSelectionPreparation = null;
        SetCaptureInProgress(false);
        // The overlay held a frozen full-desktop snapshot plus the capture
        // bitmaps; return that memory after the close input burst so the
        // cursor is not held up by synchronous working-set/GC trimming.
        _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation, CancellationToken.None);
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

    private Task<OcrRecognitionResult> RecognizeTextAsync(CapturedImage image) => RecognizeTextAsync(image, CancellationToken.None);

    private Task<OcrRecognitionResult> RecognizeTextAsync(CapturedImage image, CancellationToken cancellationToken)
    {
        _ocrPreparationUsed = true;
        return OcrProviderFactory.RecognizeAsync(image, _settingsProvider(), cancellationToken);
    }

    private Task<TranslationSegmentsResult> TranslateTextAsync(OcrRecognitionResult recognition) =>
        TranslateTextAsync(recognition, CancellationToken.None);

    private async Task<TranslationSegmentsResult> TranslateTextAsync(
        OcrRecognitionResult recognition, CancellationToken cancellationToken)
    {
        var settings = _settingsProvider();
        var provider = TranslationProviderFactory.Create(
            settings,
            _translationCredentialStore,
            _httpClient);
        if (provider is OrderedTranslationProvider ordered)
        {
            ordered.AvailabilityChanged += OnTranslationAvailabilityChanged;
        }
        try
        {
            return await provider.TranslateSegmentsAsync(
                recognition.Regions.Select(region => region.Text).ToArray(),
                "auto",
                settings.TranslationTargetLanguage,
                cancellationToken);
        }
        finally
        {
            if (provider is OrderedTranslationProvider orderedProvider)
            {
                orderedProvider.AvailabilityChanged -= OnTranslationAvailabilityChanged;
            }
            // Translation models and OCR buffers are native/heavy allocations;
            // return their retired pages without blocking the overlay thread.
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation, CancellationToken.None);
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
        // A completed selection from the interactive overlay transitions
        // directly into scrolling before its close callback clears the
        // shared flag. Keep the guard for fresh shortcut requests while
        // allowing that explicit selection hand-off.
        if (_isCaptureInProgress && initialSelection is null)
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
                    if (Volatile.Read(ref cancellationRequested) != 0 || progressWindow.Dispatcher.HasShutdownStarted) return;
                    if (progressWindow.Dispatcher.CheckAccess()) CancelSession();
                    else _ = progressWindow.Dispatcher.BeginInvoke(DispatcherPriority.Send, CancelSession);
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
                // The coordinator owns both lifetimes. An independent preview
                // must not inherit hide/show transitions from the selection.
                CapturedImage? initialImage = null;
                Action<ScreenRegion>? regionChanged = null;
                try
                {
                    initialImage = scrollSelection.CaptureSnapshot();
                    var initialRegion = scrollSelection.CaptureRegion;
                    progressWindow.ConfigureForCaptureRegion(initialRegion);
                    new System.Windows.Interop.WindowInteropHelper(progressWindow)
                        .EnsureHandle();
                    progressWindow.TryPositionOutside(initialRegion);
                    // Position and size the preview before showing it. Showing the
                    // window first lets WPF place it at the default origin for one
                    // compositor frame, which appears as a flash on the left side.
                    progressWindow.UpdatePreview(
                        new ScrollCapturePreviewState(
                            initialImage.Preview,
                            1,
                            0,
                            0,
                            initialImage.Bitmap.Width,
                            initialImage.Bitmap.Height));
                    progressWindow.TryPositionOutside(initialRegion);

                    var captureMode = _settingsProvider().ScrollCaptureMode;
                    if (captureMode == ScrollCaptureMode.ManualWheel)
                        progressWindow.ConfigureManualWheelMode();
                    using var wheelMonitor = new ScrollCaptureWheelMonitor(
                        initialRegion,
                        wheelDetected: null,
                        (x, y) => progressWindow.ContainsScreenPoint(x, y),
                        CancelFromSelection);
                    if (captureMode == ScrollCaptureMode.ManualWheel)
                        wheelMonitor.BlockNonWheelInput();
                    else
                    {
                        // Own the physical wheel as soon as the hook exists.
                        // Otherwise a wheel gesture during target discovery or
                        // first-frame preparation can leak into the page before
                        // automatic mode enters its state machine.
                        wheelMonitor.EnableControlledCaptureInput();
                    }
                    // Install input protection before opening the live scroll
                    // hole, so clicks cannot leak during the hand-off.
                    await scrollSelection.LockForScrollingAsync(
                        cancellationSource.Token);
                    // Locking the selection refreshes the native full-screen
                    // mask and can move that HWND above the WPF preview. Put
                    // the preview back on top after the final mask update.
                    progressWindow.Show();
                    progressWindow.BringToFront();
                    regionChanged = region =>
                    {
                        wheelMonitor.UpdateCaptureRegion(region);
                    };
                    scrollSelection.CaptureRegionChanged += regionChanged;

                    // Resolve behind our own visible windows. Keep the same
                    // owner and z-order for the entire preview lifetime.
                    _ = ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(
                        scrollSelection.CaptureRegion, out var target, ignoreCaptureProcess: true);
                    if (target is null)
                    {
                        _statusReporter("无法识别选区下的可滚动窗口。");
                        return;
                    }

                    await using var scrollFrameSource = new ScrollCaptureFrameSource(target.WindowHandle, cancellationSource.Token);
                    target = target with
                    {
                        FrameSource = scrollFrameSource,
                        PreviewRegionProvider = progressWindow.GetScreenRegion,
                        AllowContextCapture = scrollSelection.SupportsContextCapture,
                        BorderRegions = scrollSelection.BorderRegions,
                        BorderBackdrop = (initialImage.Bitmap, initialRegion),
                    };

                    // Activate the window under the selection so controlled
                    // wheel input reaches the correct scroll viewer.
                    _ = ForegroundWindowCaptureService.TryFocusScrollTarget(target);
                    await Task.Delay(30, cancellationSource.Token);

                    // Capture region equals the user selection. Prefer the
                    // pre-scroll snapshot so content before the first controlled
                    // step is retained.
                    using var firstFrame = ScrollCaptureService.CreateInitialFrame(
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
                        progressWindow.ConfigureManualWheelMode();
                        // Surface readback and frame checks must stay off the UI thread.
                        // Keep its entire async chain off the dispatcher that also
                        // services WH_MOUSE_LL; ConfigureAwait at only the matcher
                        // does not prevent the next capture from blocking input.
                        result = await Task.Run(() => ScrollCaptureService.CaptureOnWheelAsync(
                            target,
                            completionSource.Task,
                            wheelMonitor.WheelEvents,
                            previewChanged: previewState =>
                                UpdateProgress(progressWindow, previewState),
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
                            cancellationToken: cancellationSource.Token,
                            previewStatusChanged: progressWindow.QueuePreviewStatus), cancellationSource.Token);
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
                        result = await Task.Run(() => ScrollCaptureService.CaptureControlledAsync(
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
                            setProgressVisibilityAsync: null,
                            initialFrame: firstFrame,
                            cancellationToken: cancellationSource.Token,
                            previewStatusChanged: progressWindow.QueuePreviewStatus,
                            recoveryDirectionChanged: progressWindow.QueueRecoveryDirection), cancellationSource.Token);
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
                            // The immutable WPF surface can be shared with the
                            // asynchronous history writer. A separate GDI clone
                            // and WPF conversion duplicated the entire tall image
                            // and could outlive a failed editor hand-off.
                            var editorImage = completedImage;
                            var historyPreview = await Task.Run(
                                editorImage.WarmPreview, cancellationSource.Token);
                            cancellationSource.Token.ThrowIfCancellationRequested();
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
                                pngSaveLocationMode: settings.PngSaveLocationMode,
                                recognizeTextWithCancellationAsync: RecognizeTextAsync,
                                translateTextAsync: TranslateTextAsync,
                                settingsProvider: _settingsProvider);
                            try { editor.Show(); }
                            catch { editor.Close(); throw; }
                            completedImage = null;
                            if (settings.HistoryLimit > 0)
                                _ = _historyService.Add(historyPreview, settings.HistoryLimit);
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
                    // Ignore a hook-thread cancellation queued just before the
                    // session completed; its token source is about to retire.
                    Interlocked.Exchange(ref cancellationRequested, 1);
                    initialImage?.Dispose();
                    if (regionChanged is not null)
                        scrollSelection.CaptureRegionChanged -= regionChanged;
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
                pngSaveLocationMode: _settingsProvider().PngSaveLocationMode,
                recognizeTextWithCancellationAsync: RecognizeTextAsync,
                translateTextAsync: TranslateTextAsync,
                settingsProvider: _settingsProvider);
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
        _ocrPreparationUsed = true;
        using var popup = RecognitionPopupSession.Begin(
            settings.RecognitionResultPresentation == RecognitionResultPresentationMode.Popup,
            "正在识别文字…");
        var result = await OcrProviderFactory.RecognizeAsync(capturedImage, settings,
            new Progress<string>(message => popup?.Update("正在识别文字…", message)),
            popup?.CancellationToken ?? CancellationToken.None);
        if (settings.RecognitionResultPresentation ==
            RecognitionResultPresentationMode.Popup)
        {
            popup?.Complete("文字识别", result.Text, notice: result.IsSuccess ? result.ResultNotice : result.ErrorMessage);
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
        string? translatedText,
        string? notice = null)
    {
        var window = new RecognitionResultPopupWindow(
            title,
            sourceText,
            translatedText,
            closeAfterCopy: true,
            notice: notice);
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    private async Task RequestVideoRecordingFromSelectionAsync(
        ScreenRegion selection)
    {
        try
        {
            await RunVideoRecordingSessionAsync(selection);
        }
        catch (Exception exception)
        {
            await ReportRecordingStartFailureAsync(exception);
        }
        finally
        {
            _ = Task.Run(Core.MemoryFootprint.TrimAfterHeavyOperation, CancellationToken.None);
        }
    }

    private async Task RunVideoRecordingSessionAsync(ScreenRegion selection, Func<Task>? selectionPresented = null)
    {
        if (_isRecordingInProgress)
        {
            SetCaptureInProgress(false);
            ReportRecordingAlreadyInProgress();
            return;
        }

        _isRecordingInProgress = true;
        using var startupInteraction = new CaptureInteractionScope(SetCaptureInProgress);
        try
        {
            // Keep the existing selection and capture state until the new
            // frame and toolbar are ready. ShowSessionAsync owns that hand-off.
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
                toolbarScalePercent: settings.ToolbarScalePercent,
                toolbarFeatureShortcuts: settings.CaptureToolbarFeatureShortcuts,
                controlsReady: startupInteraction.Dispose,
                selectionPresented: selectionPresented,
                preparedToolbar: TakePreparedRecordingToolbar());
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

    private async Task ReportRecordingStartFailureAsync(Exception exception)
    {
        if (exception.GetBaseException() is OperationCanceledException) return;
        using var activity = Infrastructure.FeatureActivity.Begin();
        var rootException = exception.GetBaseException();
        var detail = string.IsNullOrWhiteSpace(rootException.Message)
            ? rootException.GetType().Name
            : rootException.Message.Trim();
        var settings = _settingsProvider();
        var log = await new RecordingErrorLog().WriteAsync("open-recording",
            $"Requested: {settings.VideoRecordingCodec}, {settings.VideoRecordingFrameRate} fps\n" +
            $"Audio: system={settings.RecordSystemAudio}, microphone={settings.RecordMicrophone}", exception.ToString());
        _statusReporter($"无法开始区域录制：{detail}\n{log.Description}");
    }

    private void SetCaptureInProgress(bool isInProgress)
    {
        if (_isCaptureInProgress == isInProgress)
        {
            return;
        }

        _isCaptureInProgress = isInProgress;
        CaptureTimingDiagnostics.Mark(
            "coordinator-capture-state",
            $"inProgress={isInProgress}");
        // Teardown must not be aborted by an optional subscriber whose native
        // state has already been disposed. Notify each listener independently
        // so the floating button and hotkey manager both recover.
        try
        {
            _mouseShortcutSuppressionChanged?.Invoke(isInProgress);
        }
        catch (Exception)
        {
            // Optional input suppression must never prevent the capture state
            // notification that restores the tray/floating controls.
        }

        try
        {
            CaptureStateChanged?.Invoke(isInProgress);
        }
        catch (Exception)
        {
            // A closing window may already have released its native handle;
            // the coordinator still has to finish its state transition.
        }
    }

    private void OnTranslationAvailabilityChanged(
        string providerId,
        bool isAvailable,
        string reason)
    {
        var kind = string.Equals(
            providerId,
            TranslationProviderFactory.OfflineProviderId,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, TranslationProviderFactory.LocalLargeModelProviderId, StringComparison.OrdinalIgnoreCase)
            ? TranslationProviderKind.Offline
            : TranslationProviderFactory.IsPublicProvider(providerId) ? TranslationProviderKind.Public : TranslationProviderKind.Online;
        _translationAvailabilityReporter?.Invoke(kind, isAvailable, reason);
    }

    private static async Task WaitForCaptureChromeToHideAsync()
    {
        if (System.Windows.Application.Current is App application)
            await application.WaitForResidentCaptureChromeAsync();
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static void UpdateProgress(
        ScrollCaptureProgressWindow progressWindow,
        ScrollCapturePreviewState previewState)
    {
        progressWindow.QueuePreview(previewState);
    }

}
