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
    private bool _isCaptureInProgress;

    public RegionCaptureCoordinator(
        Func<AppSettings> settingsProvider,
        CaptureHistoryService historyService,
        PinnedImageManager pinnedImageManager,
        ITranslationCredentialStore translationCredentialStore,
        HttpClient httpClient,
        Action<string> statusReporter,
        Action<bool>? mouseShortcutSuppressionChanged = null)
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
    }

    public Task RequestCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: false,
            initialScreenSnapshot,
            pointerContinuation);
    }

    public Task RequestOcrCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: true,
            translateTextAfterSelection: false,
            initialScreenSnapshot,
            pointerContinuation);
    }

    public Task RequestTranslationCaptureAsync(
        CapturedImage? initialScreenSnapshot = null,
        CapturePointerContinuation? pointerContinuation = null)
    {
        return RequestInteractiveCaptureAsync(
            recognizeTextAfterSelection: false,
            translateTextAfterSelection: true,
            initialScreenSnapshot,
            pointerContinuation);
    }

    private Task RequestInteractiveCaptureAsync(
        bool recognizeTextAfterSelection,
        bool translateTextAfterSelection,
        CapturedImage? initialScreenSnapshot,
        CapturePointerContinuation? pointerContinuation)
    {
        if (_isCaptureInProgress)
        {
            initialScreenSnapshot?.Dispose();
            return Task.CompletedTask;
        }

        SetCaptureInProgress(true);
        var settings = _settingsProvider();
        try
        {
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

        return Task.CompletedTask;
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

    private void SetCaptureInProgress(bool isInProgress)
    {
        if (_isCaptureInProgress == isInProgress)
        {
            return;
        }

        _isCaptureInProgress = isInProgress;
        _mouseShortcutSuppressionChanged?.Invoke(isInProgress);
    }

    private static void UpdateProgress(
        ScrollCaptureProgressWindow progressWindow,
        ScrollCapturePreviewState previewState)
    {
        progressWindow.QueuePreview(previewState);
    }

}
