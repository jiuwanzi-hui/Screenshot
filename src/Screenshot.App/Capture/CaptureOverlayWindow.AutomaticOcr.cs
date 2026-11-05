using System.Windows;
using Screenshot.App.Text;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private readonly AutomaticOcrSession _automaticOcr = new();
    private bool? _automaticOcrEnabledOverride;
    private bool _isAutomaticOcrRunning;
    private bool _isAutomaticOcrOverlay;
    internal Task AutomaticOcrTask => _automaticOcr.Completion;
    private bool AutomaticOcrEnabled => _automaticOcrEnabledOverride ?? _options?.AutoOcrAfterCapture == true;

    internal static void ApplyAutomaticOcrSetting(bool enabled)
    {
        if (_activeInteractiveOverlay is not { } overlay || overlay.AutomaticOcrEnabled == enabled) return;
        overlay._automaticOcrEnabledOverride = enabled;
        overlay.CancelAutomaticOcr();
        if (enabled) overlay.ScheduleAutomaticOcr();
        else if (overlay._isAutomaticOcrOverlay)
        {
            overlay.OcrTextOverlay.Children.Clear();
            overlay.OcrTextOverlay.Visibility = Visibility.Collapsed;
            overlay._isAutomaticOcrOverlay = false;
        }
    }

    private bool CanAutomaticallyRecognizeSelection => AutomaticOcrEnabled &&
        !_isDisposed && !_isCompleted && !_isScrollCaptureSelection &&
        !_isSelecting && !_isMovingSelection && !_isResizeDragging && !_isSelectionAdjustmentInProgress &&
        !_isActionInProgress && !_isEditorInitializing && !_isTranslationInitializing &&
        HasValidSelection() && InlineEditorCanvas.HasImage &&
        !InlineEditorCanvas.IsAnnotationCreationEnabled && !InlineEditorCanvas.CanUndo;

    internal void ScheduleAutomaticOcr()
    {
        if (!CanAutomaticallyRecognizeSelection ||
            _options?.RecognizeTextAsync is null && _options?.RecognizeTextWithCancellationAsync is null) return;
        if (_inlineOcrResult is { IsSuccess: true, Regions.Count: > 0 } cached)
        {
            ShowInlineOcrText(cached);
            _isAutomaticOcrOverlay = true;
            return;
        }
        CancelAutomaticOcr();
        _automaticOcr.Schedule(
            () => CanAutomaticallyRecognizeSelection ? _inlineEditorImage?.Clone() : null,
            (image, token) => _options!.RecognizeTextWithCancellationAsync?.Invoke(image, token) ??
                _options.RecognizeTextAsync!(image),
            result =>
            {
                _isAutomaticOcrRunning = false;
                _isOcrInitializing = false;
                UpdateCaptureActivity();
                if (!CanAutomaticallyRecognizeSelection) return;
                _inlineOcrResult = result;
                if (result.IsSuccess && result.Regions.Count > 0)
                {
                    ShowInlineOcrText(result);
                    _isAutomaticOcrOverlay = true;
                }
                else
                {
                    CaptureStatusMessage = result.IsSuccess ? "自动识别完成，未发现可选择的文字。" :
                        result.ErrorMessage ?? "自动识别失败，可点击“文”重试。";
                    CaptureStatusText.Visibility = Visibility.Visible;
                }
            },
            () =>
            {
                _isAutomaticOcrRunning = true;
                _isOcrInitializing = true;
                CaptureStatusMessage = "正在自动识别文字，可继续移动或调整选区…";
                CaptureStatusText.Visibility = Visibility.Visible;
                UpdateCaptureActivity();
            });
    }

    private void CancelAutomaticOcr()
    {
        _automaticOcr.Cancel();
        if (!_isAutomaticOcrRunning) return;
        _isAutomaticOcrRunning = false;
        _isOcrInitializing = false;
        UpdateCaptureActivity();
    }
}
