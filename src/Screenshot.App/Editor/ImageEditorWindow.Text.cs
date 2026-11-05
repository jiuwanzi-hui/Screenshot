using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Editor;

public partial class ImageEditorWindow
{
    private readonly Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>>? _recognizeWithCancellation;
    private readonly Func<OcrRecognitionResult, CancellationToken, Task<TranslationSegmentsResult>>? _translateTextAsync;
    private readonly Func<AppSettings>? _settingsProvider;
    private CancellationTokenSource? _activeTextOperation;
    private OcrRecognitionResult? _textRecognition;
    private OcrRecognitionResult? _translationInput;
    private TranslationSegmentsResult? _textTranslation;
    private bool _applyingTranslation;
    private int _textRevision;

    private void InitializeTextActions()
    {
        RecognizeTextButton.Visibility = CopyTextButton.Visibility = CopyTableButton.Visibility =
            _recognizeWithCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        TranslateTextButton.Visibility = _recognizeWithCancellation is null || _translateTextAsync is null
            ? Visibility.Collapsed : Visibility.Visible;
        TranslationTargetPreferences.Changed += OnTextTargetChanged;
    }

    private void DisposeTextActions()
    {
        var operation = Interlocked.Exchange(ref _activeTextOperation, null);
        operation?.Cancel();
        TranslationTargetPreferences.Changed -= OnTextTargetChanged;
        _textRecognition = null; _textTranslation = null; _translationInput = null;
        RecognitionOverlay.Children.Clear();
        RecognitionOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnTextDocumentChanged()
    {
        if (_applyingTranslation) return;
        _textRevision++;
        _activeTextOperation?.Cancel();
        _textRecognition = null; _textTranslation = null; _translationInput = null;
        HideSelectableRecognition();
        TranslateTextLabel.Text = "译";
    }

    private void OnTextTargetChanged(object? sender, EventArgs e)
    {
        _activeTextOperation?.Cancel();
        _textTranslation = null; _translationInput = null;
        _applyingTranslation = true;
        try { EditorCanvas.ClearTranslationOverlay(); }
        finally { _applyingTranslation = false; }
        HideSelectableRecognition();
        TranslateTextLabel.Text = "译";
        StatusText.Text = "目标语言已同步，请重新翻译。";
    }

    private async void OnRecognizeTextClick(object sender, RoutedEventArgs e) => await RunTextActionAsync(ImageTextAction.Recognize);
    private async void OnCopyTextClick(object sender, RoutedEventArgs e) => await RunTextActionAsync(ImageTextAction.CopyText);
    private async void OnTranslateTextClick(object sender, RoutedEventArgs e) => await RunTextActionAsync(ImageTextAction.Translate);

    internal async Task RunTextActionAsync(ImageTextAction action)
    {
        if (!_isInitialized || _isClosed || _recognizeWithCancellation is null || _activeTextOperation is not null) return;
        if (action == ImageTextAction.Translate && _translateTextAsync is null) return;
        if (action == ImageTextAction.Recognize && RecognitionOverlay.Visibility == Visibility.Visible)
        { HideSelectableRecognition(); StatusText.Text = "已退出文字选择，可继续标注。"; return; }
        if (action == ImageTextAction.Translate && _textTranslation is { FailedSegmentIndices.Count: 0 })
        {
            if (!EditorCanvas.HasTranslationOverlay) { StatusText.Text = "文字已经是目标语言，未生成覆盖层。"; return; }
            EditorCanvas.SetTranslationOverlayVisible(!EditorCanvas.IsTranslationOverlayVisible);
            TranslateTextLabel.Text = EditorCanvas.IsTranslationOverlayVisible ? "原" : "译";
            HideSelectableRecognition();
            StatusText.Text = EditorCanvas.IsTranslationOverlayVisible ? "已显示缓存译文。" : "已显示原图。";
            return;
        }

        // Snapshot before registering cancellation: committing a pending text
        // annotation can itself raise HistoryChanged.
        var wasTranslated = EditorCanvas.IsTranslationOverlayVisible;
        EditorCanvas.SetTranslationOverlayVisible(false);
        BitmapSource snapshot;
        try { snapshot = EditorCanvas.HasAnnotations || EditorCanvas.IsTextInputActive
                ? EditorCanvas.RenderEditedImage() : _capturedImage.Preview; }
        finally { EditorCanvas.SetTranslationOverlayVisible(wasTranslated); }
        using var cancellation = new CancellationTokenSource();
        _activeTextOperation = cancellation;
        var token = cancellation.Token;
        var revision = _textRevision;
        var targetVersion = TranslationTargetPreferences.Version;
        var stopwatch = Stopwatch.StartNew();
        SetTextActionBusy(true);
        StatusText.ToolTip = null;
        StatusText.Text = action == ImageTextAction.Translate ? "正在识别并翻译图片…" : "正在识别图片文字…";
        try
        {
            using var image = _textRecognition is null || action == ImageTextAction.CopyTable
                ? await Task.Run(() => CaptureTextSnapshot(snapshot), token) : null;
            var recognition = _textRecognition ?? await TallImageOcrService.RecognizeAsync(image!, _recognizeWithCancellation, token);
            token.ThrowIfCancellationRequested();
            if (_isClosed || revision != _textRevision) return;
            if (!recognition.IsSuccess || string.IsNullOrWhiteSpace(recognition.Text))
            { StatusText.Text = recognition.ErrorMessage ?? "未识别到文字。"; return; }
            _textRecognition = recognition;

            if (action == ImageTextAction.CopyText)
            { await ClipboardTextService.SetTextAsync(recognition.Text); StatusText.Text = "文字已识别并复制。"; }
            else if (action == ImageTextAction.CopyTable)
            {
                var supplementary = await TableSupplementaryOcrService.RecognizeAsync(image!, recognition.Words, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                var table = await Task.Run(() => TableRecognitionService.BuildTsv(recognition, image!.Bitmap, supplementary, token), token);
                token.ThrowIfCancellationRequested();
                if (!table.IsSuccess) { StatusText.Text = table.ErrorMessage ?? "未识别到表格。"; return; }
                if (!string.IsNullOrWhiteSpace(table.ClipboardHtml)) await ClipboardTextService.SetHtmlAsync(table.ClipboardHtml, table.Content);
                else await ClipboardTextService.SetTextAsync(table.Content);
                StatusText.Text = "表格已识别并复制。";
            }
            else if (action == ImageTextAction.Recognize)
            {
                EditorCanvas.SetTranslationOverlayVisible(false);
                TranslateTextLabel.Text = "译";
                if (_settingsProvider?.Invoke().RecognitionResultPresentation == RecognitionResultPresentationMode.Popup)
                    ShowTextResult(recognition.Text, null, null);
                else ShowSelectableRecognition(recognition);
                StatusText.Text = $"识别完成 · {stopwatch.Elapsed.TotalSeconds:F1} 秒 · 可选择文字复制";
            }
            else if (action == ImageTextAction.Translate)
            {
                _translationInput ??= CreateTranslationInput(recognition);
                var input = _translationInput;
                if (input.Regions.Count == 0) { StatusText.Text = "识别引擎未返回文字位置，无法覆盖翻译。"; return; }
                var indices = _textTranslation?.FailedSegmentIndices?.ToArray() ?? Enumerable.Range(0, input.Regions.Count).ToArray();
                var retryInput = input with { Regions = indices.Select(index => input.Regions[index]).ToArray(),
                    Text = string.Join("\n", indices.Select(index => input.Regions[index].Text)) };
                using var progress = TranslationProgress.Begin(message =>
                {
                    if (_isClosed || token.IsCancellationRequested || targetVersion != TranslationTargetPreferences.Version) return;
                    StatusText.Text = message;
                    StatusText.ToolTip = message;
                });
                var translated = await _translateTextAsync!(retryInput, token);
                token.ThrowIfCancellationRequested();
                if (_isClosed || targetVersion != TranslationTargetPreferences.Version || revision != _textRevision) return;
                if (!translated.IsSuccess || translated.Segments.Count != indices.Length)
                { StatusText.Text = translated.ErrorMessage ?? "翻译返回的段落不完整。"; StatusText.ToolTip = StatusText.Text; return; }
                var output = _textTranslation?.Segments.ToArray() ?? input.Regions.Select(region => region.Text).ToArray();
                var failed = (translated.FailedSegmentIndices ?? []).ToHashSet();
                for (var i = 0; i < indices.Length; i++) if (!failed.Contains(i)) output[indices[i]] = translated.Segments[i];
                _textTranslation = translated with { Segments = output, FailedSegmentIndices = failed.Select(i => indices[i]).ToArray() };
                var failedOriginal = _textTranslation.FailedSegmentIndices.ToHashSet();
                var annotations = input.Regions.Select((region, index) => (region, index))
                    .Where(item => !failedOriginal.Contains(item.index) && TranslationPresentationLayout.HasMeaningfulTranslation(item.region.Text, output[item.index]))
                    .Select(item => new TranslatedTextAnnotationRegion(new Rect(Math.Max(0, item.region.X - 3), Math.Max(0, item.region.Y - 2),
                        Math.Max(20, item.region.Width + 6), Math.Max(12, item.region.Height + 4)), output[item.index],
                        Math.Clamp(item.region.EstimatedFontSize > 0 ? item.region.EstimatedFontSize : item.region.Height / 1.12, TranslationTextLayout.MinimumFontSize, 32))).ToArray();
                var details = string.Join("\n", new[] { translated.Notice, translated.ErrorMessage }.Where(value => !string.IsNullOrWhiteSpace(value)));
                _applyingTranslation = true;
                try { EditorCanvas.AddTranslationOverlay(annotations, replaceExisting: true); }
                finally { _applyingTranslation = false; }
                HideSelectableRecognition();
                TranslateTextLabel.Text = failed.Count > 0 ? "译" : "原";
                StatusText.Text = failed.Count > 0 ? $"部分段落未完成，已保留原文 · 再次翻译只重试未完成段落 · {stopwatch.Elapsed.TotalSeconds:F1} 秒"
                    : $"翻译完成 · {stopwatch.Elapsed.TotalSeconds:F1} 秒 · 点击“原”切换原图";
                StatusText.ToolTip = details;
                if (_settingsProvider?.Invoke().RecognitionResultPresentation == RecognitionResultPresentationMode.Popup)
                    ShowTextResult(input.Text, string.Join("\n", output), details);
            }
        }
        catch (OperationCanceledException) { if (!_isClosed) StatusText.Text = "本次处理已取消，请按当前图片和语言重新处理。"; }
        catch (Exception exception) { if (!_isClosed) StatusText.Text = $"处理失败：{exception.Message}"; }
        finally
        {
            Interlocked.CompareExchange(ref _activeTextOperation, null, cancellation);
            if (!_isClosed) SetTextActionBusy(false);
        }
    }

    private void SetTextActionBusy(bool busy)
    {
        RecognitionActivity.IsActive = busy;
        CancelTextOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecognizeTextButton.IsEnabled = CopyTextButton.IsEnabled = CopyTableButton.IsEnabled = !busy;
        TranslateTextButton.IsEnabled = !busy;
    }

    private static CapturedImage CaptureTextSnapshot(BitmapSource snapshot)
    {
        // Preserve full-size pixels without compressing and decoding a tall PNG.
        if (snapshot.Format != PixelFormats.Bgra32)
            snapshot = new FormatConvertedBitmap(snapshot, PixelFormats.Bgra32, null, 0);
        var bitmap = new System.Drawing.Bitmap(snapshot.PixelWidth, snapshot.PixelHeight,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try { snapshot.CopyPixels(Int32Rect.Empty, data.Scan0, checked(data.Stride * data.Height), data.Stride); }
            finally { bitmap.UnlockBits(data); }
            return new CapturedImage(bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void OnCancelTextOperationClick(object sender, RoutedEventArgs e) => _activeTextOperation?.Cancel();

    private static OcrRecognitionResult CreateTranslationInput(OcrRecognitionResult recognition)
    {
        var regions = TranslationPresentationLayout.GroupParagraphs(
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words));
        return recognition with { Regions = regions, Text = string.Join("\n", regions.Select(region => region.Text)) };
    }

    private void ShowTextResult(string source, string? translated, string? notice)
    {
        var popup = new RecognitionResultPopupWindow(translated is null ? "图片文字识别" : "图片翻译", source, translated, notice: notice) { Owner = this };
        popup.Show();
    }

    private void HideSelectableRecognition() { RecognitionOverlay.Children.Clear(); RecognitionOverlay.Visibility = Visibility.Collapsed; }

    private void ShowSelectableRecognition(OcrRecognitionResult recognition)
    {
        HideSelectableRecognition();
        var words = recognition.Words.Count > 0 ? recognition.Words : recognition.Regions
            .Select(region => new OcrWordRegion(region.Text, region.X, region.Y, region.Width, region.Height)).ToArray();
        var color = (TryFindResource("SelectionBorderBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var overlay = new SelectableOcrTextOverlay(words, 1, 1, color) { Width = _capturedImage.Bitmap.Width, Height = _capturedImage.Bitmap.Height };
        overlay.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.C || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !overlay.HasSelectedText) return;
            e.Handled = true;
            try { await ClipboardTextService.SetTextAsync(overlay.SelectedText); }
            catch { StatusText.Text = "复制失败，请重试。"; }
        };
        RecognitionOverlay.Children.Add(overlay);
        RecognitionOverlay.Visibility = Visibility.Visible;
        UpdateRecognitionOverlaySize();
        overlay.Focus();
    }

    private void UpdateRecognitionOverlaySize()
    {
        RecognitionOverlay.Width = EditorCanvas.DisplayWidth;
        RecognitionOverlay.Height = EditorCanvas.DisplayHeight;
        if (RecognitionOverlay.Children.Count > 0)
            ((FrameworkElement)RecognitionOverlay.Children[0]).RenderTransform = new ScaleTransform(
                EditorCanvas.DisplayWidth / _capturedImage.Bitmap.Width, EditorCanvas.DisplayHeight / _capturedImage.Bitmap.Height);
    }
}
