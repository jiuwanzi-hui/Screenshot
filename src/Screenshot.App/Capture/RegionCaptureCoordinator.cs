using System.Net.Http;
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
        Action<VideoRecordingPreferences>? videoRecordingPreferencesChanged = null)
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
    }

    public Task RequestCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: false,
            trackOrdinaryCaptureRegion: true,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null);
    }

    public Task RequestOcrCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: true,
            translateTextAfterSelection: false,
            trackOrdinaryCaptureRegion: false,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null);
    }

    public Task RequestTranslationCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: true,
            trackOrdinaryCaptureRegion: false,
            initialScreenSnapshot,
            pointerContinuation,
            initialSelection: null);
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
        SetCaptureInProgress(true);
        try
        {
            await WaitForCaptureChromeToHideAsync();
            selection = await CaptureOverlayWindow.SelectAsync(
                pointerContinuation);
        }
        catch (Exception exception)
        {
            ReportRecordingStartFailure(exception);
        }
        finally
        {
            SetCaptureInProgress(false);
            Core.MemoryFootprint.TrimAfterHeavyOperation();
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
        ScreenRegion? initialSelection)
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
            var settings = _settingsProvider();
            CaptureOverlayWindow.ShowInteractive(
                new CaptureOverlayOptions
                {
                    SaveDirectory = settings.SaveDirectory,
                    KeepHistory = settings.KeepHistory,
                    HistoryLimit = settings.HistoryLimit,
                    HistoryService = _historyService,
                    PinnedImageManager = _pinnedImageManager,
                    StartOcrAsync = ShowOcrResultAndCompleteCaptureAsync,
                    RecognizeTextAsync = RecognizeTextAsync,
                    TranslateTextAsync = TranslateTextAsync,
                    RecognizeTextAfterSelection = recognizeTextAfterSelection,
                    TranslateTextAfterSelection = translateTextAfterSelection,
                    InitialPointerContinuation = pointerContinuation,
                    StartScrollCaptureAsync = RequestScrollCaptureFromSelectionAsync,
                    StartVideoRecordingAsync = RequestVideoRecordingFromSelectionAsync,
                    InitialSelection = initialSelection,
                    CaptureCompleted = trackOrdinaryCaptureRegion
                        ? region => _lastOrdinaryCaptureRegion = region
                        : null,
                    CaptureClosed = OnInteractiveCaptureClosed,
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
        CapturePointerContinuation? pointerContinuation = null)
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        SetCaptureInProgress(true);

        try
        {
            await WaitForCaptureChromeToHideAsync();
            var selection = await CaptureOverlayWindow.SelectAsync(
                pointerContinuation);

            if (selection is null)
            {
                return;
            }

            // The selection overlay is intentionally a lightweight mode: after
            // the user releases the mouse, capture the selected pixels and hand
            // ownership directly to the pin manager without opening a preview.
            var image = ScreenCaptureService.Capture(selection.Value);
            _pinnedImageManager.Pin(image);
        }
        finally
        {
            SetCaptureInProgress(false);
        }
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

    private Task<OcrRecognitionResult> RecognizeTextAsync(CapturedImage image)
    {
        return OcrService.RecognizeAsync(
            image,
            _settingsProvider().OcrLanguageTag);
    }

    private Task<TranslationSegmentsResult> TranslateTextAsync(
        OcrRecognitionResult recognition)
    {
        var settings = _settingsProvider();
        var provider = TranslationProviderFactory.Create(
            settings,
            _translationCredentialStore,
            _httpClient);
        return provider.TranslateSegmentsAsync(
            recognition.Regions.Select(region => region.Text).ToArray(),
            "auto",
            settings.TranslationTargetLanguage);
    }

    private async Task RequestScrollCaptureAsync(
        ScreenRegion? initialSelection,
        CapturePointerContinuation? pointerContinuation)
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        SetCaptureInProgress(true);

        try
        {
            await WaitForCaptureChromeToHideAsync();
            // Scroll target is resolved under the selection when scrolling starts.
            var scrollSelection = initialSelection is null
                ? await CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    pointerContinuation)
                : await CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    initialSelection.Value);

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
                progressWindow.Show();
                var initialImage = scrollSelection.CaptureSnapshot();
                var initialRegion = scrollSelection.CaptureRegion;
                progressWindow.ConfigureForCaptureRegion(initialRegion);
                progressWindow.TryPositionOutside(initialRegion);
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
                    // Commit the selected region before installing the pointer
                    // hook. Once capture starts, the region becomes a control
                    // surface: click pauses/resumes and double-click reverses.
                    await scrollSelection.LockForScrollingAsync(
                        cancellationSource.Token);
                    using var wheelMonitor = new ScrollCaptureWheelMonitor(
                        initialRegion,
                        wheelDetected: null,
                        (x, y) => progressWindow.ContainsScreenPoint(x, y),
                        CancelFromSelection);
                    var latestSelectionRegion = initialRegion;
                    var selectionPreviewRefreshPending = false;
                    scrollSelection.CaptureRegionChanged += region =>
                    {
                        wheelMonitor.UpdateCaptureRegion(region);
                        latestSelectionRegion = region;

                        // Mouse move events can arrive much faster than WPF can
                        // render. Coalesce the expensive snapshot and layout work
                        // to one update per render pass while keeping the wheel hit
                        // region current synchronously.
                        if (selectionPreviewRefreshPending)
                        {
                            return;
                        }

                        selectionPreviewRefreshPending = true;
                        _ = progressWindow.Dispatcher.BeginInvoke(
                            DispatcherPriority.Render,
                            () =>
                            {
                                selectionPreviewRefreshPending = false;

                                if (!progressWindow.IsVisible ||
                                    cancellationSource.IsCancellationRequested)
                                {
                                    return;
                                }

                                var currentRegion = latestSelectionRegion;
                                progressWindow.ConfigureForCaptureRegion(currentRegion);
                                progressWindow.TryPositionOutside(currentRegion);
                                progressWindow.BringToFront();
                                var previewImage = scrollSelection.CaptureSnapshot();
                                var priorInitialImage = initialImage;
                                initialImage = previewImage;
                                priorInitialImage.Dispose();
                                UpdateProgress(
                                    progressWindow,
                                    new ScrollCapturePreviewState(
                                        previewImage.Preview,
                                        1,
                                        0,
                                        0,
                                        previewImage.Bitmap.Width,
                                        previewImage.Bitmap.Height));
                            });
                    };

                    ScrollCaptureTarget? target = null;
                    progressWindow.Owner = null;
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
                    wheelMonitor.EnableControlledCaptureInput();
                    progressWindow.QueueInteractionState(
                        ControlledScrollCaptureState.WaitingToStart);
                    var result = await ScrollCaptureService.CaptureControlledAsync(
                        target,
                        completionSource.Task,
                        wheelMonitor.PointerActions,
                        stateChanged: progressWindow.QueueInteractionState,
                        previewChanged: previewState =>
                            UpdateProgress(progressWindow, previewState),
                        initialFrame: firstFrame,
                        cancellationToken: cancellationSource.Token);
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
                            var historyCloneTask = settings.KeepHistory
                                ? Task.Run(editorImage.Clone)
                                : null;
                            var editor = new ImageEditorWindow(
                                editorImage,
                                settings.SaveDirectory);
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
                            var historyItem = settings.KeepHistory
                                ? _historyService.Add(
                                    completedImage,
                                    settings.HistoryLimit)
                                : null;
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
                _settingsProvider().SaveDirectory);
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
        var result = await OcrService.RecognizeAsync(
            capturedImage,
            settings.OcrLanguageTag);
        var window = new OcrResultWindow(
            result,
            _settingsProvider,
            _translationCredentialStore,
            _httpClient);
        window.Show();
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
            Core.MemoryFootprint.TrimAfterHeavyOperation();
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
            // Give DWM one composition pass to remove the frozen screenshot
            // before exposing the live desktop and recording controls.
            await WaitForCaptureChromeToHideAsync();
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
                _videoRecordingPreferencesChanged);
            if (result.IsSuccess)
            {
                _statusReporter($"视频已保存：{result.FilePath}");
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
        // Layered popups are composed by DWM after WPF has rendered the hide.
        // Match the overlay's own capture delay so neither the floating button
        // nor its menu is frozen into the desktop snapshot.
        await Task.Delay(80);
    }

    private static void UpdateProgress(
        ScrollCaptureProgressWindow progressWindow,
        ScrollCapturePreviewState previewState)
    {
        progressWindow.QueuePreview(previewState);
    }

}
