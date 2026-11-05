using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Pin;

public partial class PinnedImageWindow
{
    private readonly AutomaticOcrSession _automaticOcr = new();
    private readonly CancellationTokenSource _recognitionLifetime = new();
    private readonly Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>>? _recognizeTextWithCancellationAsync;
    private int _imageRevision;
    private bool _automaticOcrOverlay;
    private bool _automaticOcrEnabled;
    private bool CanRecognizeText => _recognizeTextAsync is not null || _recognizeTextWithCancellationAsync is not null;

    internal void RefreshAutomaticOcrSetting()
    {
        var enabled = _settingsProvider?.Invoke().AutoOcrAfterCapture == true;
        if (_automaticOcrEnabled == enabled) return;
        _automaticOcrEnabled = enabled;
        _automaticOcr.Cancel();
        if (enabled) ScheduleAutomaticOcr();
        else if (_automaticOcrOverlay)
        {
            TextOverlay.Children.Clear();
            TextOverlay.Visibility = Visibility.Collapsed;
            _displayedRegions = [];
            _automaticOcrOverlay = false;
        }
    }

    internal void ScheduleAutomaticOcr()
    {
        if (!_automaticOcrEnabled || !CanRecognizeText || _isClosed || _isEditorMode || _isCropMode || _isMinimized) return;
        if (_recognition is { IsSuccess: true } cached)
        {
            ShowAutomaticOcrResult(cached);
            return;
        }
        _automaticOcr.Schedule(
            () => !_isClosed && !_isEditorMode && !_isCropMode && !_isMinimized ? _capturedImage.Clone() : null,
            (image, token) => _recognizeTextWithCancellationAsync?.Invoke(image, token) ?? _recognizeTextAsync!(image),
            ShowAutomaticOcrResult,
            () => StatusMenuItem.Header = "正在自动识别图片文字…");
        _textRecognitionTask = _automaticOcr.Completion;
    }

    private void ShowAutomaticOcrResult(OcrRecognitionResult result)
    {
        if (_isClosed || !_automaticOcrEnabled) return;
        _recognition = result;
        if (!result.IsSuccess || result.Regions.Count == 0)
        {
            StatusMenuItem.Header = result.ErrorMessage ?? "钉图 · 未识别到文字";
            return;
        }
        _displayedRegions = result.Regions;
        _isShowingTranslation = false;
        _automaticOcrOverlay = true;
        RenderSelectableTextOverlay();
        if (!_isEditorMode && !_isCropMode && !_isMinimized) TextOverlay.Visibility = Visibility.Visible;
        StatusMenuItem.Header = "钉图 · 拖选文字后 Ctrl+C 复制，空白处可拖动";
    }

    private void RenderOriginalTextSelection(Rect imageBounds)
    {
        var words = _recognition is { Words.Count: > 0 } recognition ? recognition.Words :
            _displayedRegions.Select(region => new OcrWordRegion(region.Text, region.X, region.Y, region.Width, region.Height)).ToArray();
        var accent = (TryFindResource("AppAccentBrush") as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Teal;
        var overlay = new SelectableOcrTextOverlay(words,
            imageBounds.Width / _capturedImage.Bitmap.Width, imageBounds.Height / _capturedImage.Bitmap.Height, accent)
        {
            Width = imageBounds.Width, Height = imageBounds.Height,
        };
        overlay.PreviewKeyDown += OnAutomaticTextPreviewKeyDown;
        System.Windows.Controls.Canvas.SetLeft(overlay, imageBounds.X);
        System.Windows.Controls.Canvas.SetTop(overlay, imageBounds.Y);
        TextOverlay.Children.Add(overlay);
    }

    private async void OnAutomaticTextPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            sender is not SelectableOcrTextOverlay { HasSelectedText: true } overlay) return;
        e.Handled = true;
        try
        {
            await ClipboardTextService.SetTextAsync(overlay.SelectedText);
            if (!_isClosed) StatusMenuItem.Header = "钉图 · 所选文字已复制";
        }
        catch (Exception)
        {
            if (!_isClosed) StatusMenuItem.Header = "钉图 · 剪贴板正忙，请重试";
        }
    }
}
