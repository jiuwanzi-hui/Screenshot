using Avalonia.Threading;
using SnapCut.Core;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Editor;
using SnapCut.Mac.Native;
using SnapCut.Mac.Presentation;
using SnapCut.Mac.Text;
using SnapCut.Mac.Pin;
using SnapCut.Mac.Recording;

namespace SnapCut.Mac.App;

internal sealed class CaptureCoordinator
{
    private readonly CaptureHistoryStore _history;
    private readonly Func<MacSettings> _settings;
    private readonly Action<MacSettings> _saveSettings;
    private readonly MacOcrService _ocr;
    private readonly MacTranslationService _translation;
    private readonly MacPinnedImageManager _pins;
    private int _busy;

    public CaptureCoordinator(
        CaptureHistoryStore history,
        Func<MacSettings> settings,
        Action<MacSettings> saveSettings,
        MacOcrService ocr,
        MacTranslationService translation,
        MacPinnedImageManager pins)
    {
        _history = history;
        _settings = settings;
        _saveSettings = saveSettings;
        _ocr = ocr;
        _translation = translation;
        _pins = pins;
    }

    public event Action<string>? CaptureCompleted;

    public async Task CaptureAllDisplaysAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            if (!EnsureScreenCaptureAccess())
            {
                ShowNotice("需要屏幕录制权限", "请先在系统设置中允许 SnapCut。");
                return;
            }

            CompleteCapture(
                MacScreenCaptureService.CaptureAllDisplays(),
                isScrollCapture: false);
        }
        catch (Exception exception)
        {
            ShowNotice("全屏截图失败", exception.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
        await Task.CompletedTask;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public async Task StartAsync(
        bool scrollCapture,
        MacCaptureAction defaultAction = MacCaptureAction.Complete)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            return;
        }

        try
        {
            if (!EnsureScreenCaptureAccess())
            {
                ShowNotice(
                    "需要屏幕录制权限",
                    "请在 系统设置 → 隐私与安全性 → 屏幕录制 中允许 SnapCut，然后重新启动应用。");
                return;
            }

            var cursor = MacCursorService.GetGlobalPosition();
            var display = MacDisplayService.SelectDisplayFor(
                new CGRect(cursor.X, cursor.Y, 1, 1));
            // Let the previous overlay leave the WindowServer compositor before
            // freezing the next desktop frame; otherwise menu bars and Dock can
            // appear twice in the captured background.
            await Task.Delay(500);
            var desktop = MacScreenCaptureService.CaptureRegion(display.Bounds);
            var currentSettings = _settings();
            var selectionWindow = new RegionSelectionWindow(
                display,
                desktop,
                scrollCapture,
                currentSettings,
                defaultAction);
            var selection = await selectionWindow.SelectAsync();
            _saveSettings(currentSettings);
            if (selection is null)
            {
                return;
            }

            var region = SelectionGeometry.ToGlobalRect(
                selection.Bounds,
                selectionWindow.SelectionSurfaceSize,
                display.Bounds);
            await Task.Delay(90);
            if (selection.Action == MacCaptureAction.VideoRecording)
            {
                await RunVideoRecordingAsync(region, currentSettings);
                return;
            }

            if (scrollCapture || selection.Action == MacCaptureAction.ScrollCapture)
            {
                await RunScrollCaptureAsync(region, currentSettings);
            }
            else
            {
                var image = MacScreenCaptureService.CaptureRegion(region);
                image = MacAnnotationRenderer.Apply(
                    image,
                    selection.Bounds,
                    selection.Annotations);
                await HandleCapturedImageAsync(image, selection.Action);
            }
        }
        catch (Exception exception)
        {
            ShowNotice("截图失败", exception.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task RunScrollCaptureAsync(CGRect region, MacSettings settings)
    {
        using var cancellation = new CancellationTokenSource();
        var discard = false;
        var progressWindow = new LongCaptureProgressWindow();
        progressWindow.StopRequested += cancellation.Cancel;
        progressWindow.CancelRequested += () =>
        {
            discard = true;
            cancellation.Cancel();
        };
        progressWindow.Show();
        using var automaticDriver = settings.ScrollCaptureMode == "Automatic"
            ? new MacAutomaticScrollDriver()
            : null;
        automaticDriver?.Start(region);
        PixelImage? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                var engine = new ScrollCaptureEngine(ScrollCaptureOptions.Default);
                return engine.Run(
                    region,
                    cancellation.Token,
                    progress => Dispatcher.UIThread.Post(
                        () => progressWindow.UpdateProgress(progress)));
            });
        }
        finally
        {
            progressWindow.Finish();
        }

        if (!discard && result is not null)
        {
            CompleteCapture(result, isScrollCapture: true);
        }
    }

    private void CompleteCapture(PixelImage image, bool isScrollCapture)
    {
        var settings = _settings();
        var path = SaveCapture(image, isScrollCapture);
        if (settings.ShowPreviewAfterCapture)
        {
            new CapturePreviewWindow(
                image,
                path,
                isScrollCapture,
                () => _pins.Pin(image, path)).Show();
        }
        else
        {
            MacNativeUi.CopyPngFile(path);
        }
    }

    private async Task HandleCapturedImageAsync(
        PixelImage image,
        MacCaptureAction action)
    {
        switch (action)
        {
            case MacCaptureAction.Save:
            {
                var path = SaveCapture(image, isScrollCapture: false);
                _ = MacNativeUi.CopyPngFile(path);
                return;
            }
            case MacCaptureAction.RecognizeText:
            {
                var recognition = await _ocr.RecognizeAsync(image);
                new OcrResultWindow(recognition).Show();
                return;
            }
            case MacCaptureAction.CopyRecognizedText:
            {
                var recognition = await _ocr.RecognizeAsync(image);
                if (!recognition.IsSuccess)
                {
                    new OcrResultWindow(recognition).Show();
                    return;
                }

                var copied = MacNativeUi.CopyText(recognition.Text);
                ShowNotice(
                    copied ? "文字已复制" : "复制失败",
                    copied
                        ? $"已复制 {recognition.Text.Length} 个字符。"
                        : "无法写入系统剪贴板。");
                return;
            }
            case MacCaptureAction.PrivacyRedaction:
            {
                var recognition = await _ocr.RecognizeAsync(image);
                if (!recognition.IsSuccess)
                {
                    new OcrResultWindow(recognition).Show();
                    return;
                }

                var candidates = MacPrivacyDetectionService.Detect(recognition);
                if (candidates.Count == 0)
                {
                    ShowNotice("未发现隐私信息", "没有检测到手机号、邮箱、身份证号、API Key 或 IP 地址。");
                    return;
                }

                var confirmed = await new PrivacyConfirmationWindow(candidates).ShowAsync();
                if (confirmed is null)
                {
                    return;
                }

                CompleteCapture(
                    MacPrivacyRedactionRenderer.Apply(image, confirmed),
                    isScrollCapture: false);
                return;
            }
            case MacCaptureAction.PinImage:
            {
                var path = SaveCapture(image, isScrollCapture: false);
                _pins.Pin(image, path);
                return;
            }
            case MacCaptureAction.Translation:
            {
                var recognition = await _ocr.RecognizeAsync(image);
                if (!recognition.IsSuccess)
                {
                    new OcrResultWindow(recognition).Show();
                    return;
                }

                var translation = await _translation.TranslateAsync(recognition.Text);
                new TranslationResultWindow(recognition.Text, translation).Show();
                return;
            }
            case MacCaptureAction.QrRecognition:
            {
                var value = MacQrCodeRecognitionService.Recognize(image);
                if (string.IsNullOrWhiteSpace(value))
                {
                    ShowNotice("未识别到二维码", "选区中没有可识别的二维码或条码。");
                    return;
                }

                var copied = MacNativeUi.CopyText(value);
                ShowNotice(
                    copied ? "二维码内容已复制" : "二维码识别完成",
                    value);
                return;
            }
            default:
                CompleteCapture(image, isScrollCapture: false);
                return;
        }
    }

    private string SaveCapture(PixelImage image, bool isScrollCapture)
    {
        var settings = _settings();
        var path = _history.Save(
            image,
            isScrollCapture,
            settings.HistoryLimit);
        CaptureCompleted?.Invoke(path);
        return path;
    }

    private static async Task RunVideoRecordingAsync(
        CGRect region,
        MacSettings settings)
    {
        var result = await MacVideoRecordingService.RecordAsync(region, settings);
        if (!result.IsSuccess)
        {
            ShowNotice("录屏未完成", result.ErrorMessage ?? "录屏失败。");
            return;
        }

        if (result.OutputPath is null)
        {
            return;
        }

        if (result.OpenEditor)
        {
            new VideoPostProcessWindow(result.OutputPath, settings).Show();
        }
        else
        {
            ShowNotice("录屏完成", result.OutputPath);
        }
    }

    private static bool EnsureScreenCaptureAccess()
    {
        return MacScreenCaptureService.HasScreenCaptureAccess() ||
               MacScreenCaptureService.RequestScreenCaptureAccess();
    }

    private static void ShowNotice(string title, string message)
    {
        new NoticeWindow(title, message).Show();
    }
}
