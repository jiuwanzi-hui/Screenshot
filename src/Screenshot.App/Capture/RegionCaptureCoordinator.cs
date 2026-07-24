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
    private bool _isCaptureInProgress;

    public RegionCaptureCoordinator(
        Func<AppSettings> settingsProvider,
        CaptureHistoryService historyService,
        PinnedImageManager pinnedImageManager,
        ITranslationCredentialStore translationCredentialStore,
        HttpClient httpClient,
        Action<string> statusReporter)
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
    }

    public Task RequestCaptureAsync()
    {
        return RequestInteractiveCaptureAsync(recognizeTextAfterSelection: false);
    }

    public Task RequestOcrCaptureAsync()
    {
        return RequestInteractiveCaptureAsync(recognizeTextAfterSelection: true);
    }

    private Task RequestInteractiveCaptureAsync(bool recognizeTextAfterSelection)
    {
        if (_isCaptureInProgress)
        {
            return Task.CompletedTask;
        }

        _isCaptureInProgress = true;
        var settings = _settingsProvider();
        try
        {
            CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
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
                StartScrollCaptureAsync = RequestScrollCaptureFromSelectionAsync,
                CaptureClosed = OnInteractiveCaptureClosed,
            });
        }
        catch
        {
            _isCaptureInProgress = false;
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task RequestPinCaptureAsync()
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        _isCaptureInProgress = true;

        try
        {
            var selection = await CaptureOverlayWindow.SelectAsync();

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
            _isCaptureInProgress = false;
        }
    }

    private void OnInteractiveCaptureClosed()
    {
        _isCaptureInProgress = false;
    }

    public Task RequestScrollCaptureAsync()
    {
        return RequestScrollCaptureAsync(initialSelection: null);
    }

    private Task RequestScrollCaptureFromSelectionAsync(ScreenRegion selection)
    {
        return RequestScrollCaptureAsync(selection);
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

    private async Task RequestScrollCaptureAsync(ScreenRegion? initialSelection)
    {
        if (_isCaptureInProgress)
        {
            return;
        }

        _isCaptureInProgress = true;

        try
        {
            // Scroll target is resolved under the selection when scrolling starts.
            var scrollSelection = initialSelection is null
                ? await CaptureOverlayWindow.SelectForScrollCaptureAsync()
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

                void CompleteCapture(object? sender, EventArgs eventArgs)
                {
                    completionSource.TrySetResult();
                }

                void CancelCapture(object? sender, EventArgs eventArgs)
                {
                    cancellationSource.Cancel();
                }

                void CancelFromSelection()
                {
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
                    using var wheelMonitor = new ScrollCaptureWheelMonitor(
                        initialRegion,
                        _ => scrollSelection.LockForScrollingAsync(
                                cancellationSource.Token)
                            .GetAwaiter()
                                .GetResult(),
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

                    var wheelReady = wheelMonitor.WheelEvents
                        .WaitToReadAsync(cancellationSource.Token)
                        .AsTask();
                    var firstAction = await Task.WhenAny(
                        completionSource.Task,
                        wheelReady);
                    CapturedImage? image;

                    if (firstAction == completionSource.Task)
                    {
                        image = scrollSelection.CaptureSnapshot();
                    }
                    else
                    {
                        // First wheel already locked click-through via the monitor.
                        // Re-lock is cheap and ensures the hole is active before we
                        // resolve the window under the selection center.
                        await scrollSelection.LockForScrollingAsync(
                            cancellationSource.Token);
                        wheelMonitor.BlockNonWheelInput();
                        // Let the click-through hole settle so WindowFromPoint /
                        // live pixel reads hit the real content under the selection.
                        await Task.Delay(40, cancellationSource.Token);

                        if (!ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(
                                scrollSelection.CaptureRegion,
                                out var target) ||
                            target is null)
                        {
                            _statusReporter("无法识别选区下的可滚动窗口。");
                            return;
                        }

                        // Activate the window under the selection so wheel input
                        // reaches the right control without a manual pre-focus step.
                        _ = ForegroundWindowCaptureService.TryFocusScrollTarget(target);
                        await Task.Delay(30, cancellationSource.Token);

                        // Capture region equals the user selection. Prefer the
                        // pre-scroll snapshot so content before the first wheel
                        // tick is retained.
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
                        var result = await ScrollCaptureService.CaptureOnWheelAsync(
                            target,
                            completionSource.Task,
                            wheelMonitor.WheelEvents,
                            previewChanged: previewState =>
                                UpdateProgress(progressWindow, previewState),
                            initialFrame: firstFrame,
                            cancellationToken: cancellationSource.Token);
                        image = result.Image;

                        if (!result.IsSuccess || image is null)
                        {
                            _statusReporter(result.ErrorMessage ?? "滚动截图失败。");
                            return;
                        }
                    }

                    CapturedImage? completedImage = image;
                    try
                    {
                        var settings = _settingsProvider();
                        var historyItem = settings.KeepHistory
                            ? _historyService.Add(completedImage, settings.HistoryLimit)
                            : null;
                        if (editRequested)
                        {
                            var editor = new ImageEditorWindow(
                                completedImage,
                                settings.SaveDirectory);
                            editor.Show();
                            completedImage = null;
                        }
                        else
                        {
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
            _isCaptureInProgress = false;
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
            _isCaptureInProgress = false;
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

    private static void UpdateProgress(
        ScrollCaptureProgressWindow progressWindow,
        ScrollCapturePreviewState previewState)
    {
        progressWindow.QueuePreview(previewState);
    }

}
