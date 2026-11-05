using System.Windows;
using Screenshot.App.Watermarks;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private readonly DateTimeOffset _watermarkTimestamp = DateTimeOffset.Now;
    private CancellationTokenSource? _watermarkCancellation;
    private Task _watermarkTask = Task.CompletedTask;
    private bool _watermarkEnabled;
    private bool _watermarkInitialized;

    private void InitializeWatermark()
    {
        if (_watermarkInitialized || _options?.Watermark is null) return;
        _watermarkInitialized = true;
        if (_options.Watermark.AutoApply) SetWatermarkEnabled(true);
    }

    private async void OnWatermarkClick(object sender, RoutedEventArgs e)
    {
        if (_options?.Watermark is null || _isActionInProgress) return;
        if (!InlineEditorCanvas.HasImage) await EnterInlineEditorAsync();
        if (_isDisposed || _isCompleted || !InlineEditorCanvas.HasImage) return;
        InitializeWatermark();
        SetWatermarkEnabled(!_watermarkEnabled);
    }

    private void SetWatermarkEnabled(bool enabled)
    {
        _watermarkCancellation?.Cancel();
        _watermarkCancellation?.Dispose();
        _watermarkCancellation = null;
        _watermarkEnabled = enabled;
        WatermarkButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            enabled ? "EditorToolbarButtonSelectedBackgroundBrush" : "EditorToolbarButtonBackgroundBrush");
        WatermarkButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
            enabled ? "EditorToolbarButtonSelectedBorderBrush" : "EditorToolbarButtonBorderBrush");
        WatermarkButton.ToolTip = enabled ? "取消水印" : "添加预设水印（在常规设置 → 水印中编辑）";
        if (!enabled)
        {
            InlineEditorCanvas.SetWatermark(null);
            _watermarkTask = Task.CompletedTask;
            return;
        }
        _watermarkCancellation = new CancellationTokenSource();
        _watermarkTask = LoadWatermarkAsync(_watermarkCancellation.Token);
    }

    private async Task LoadWatermarkAsync(CancellationToken cancellationToken)
    {
        var settings = _options!.Watermark!.Normalize();
        try
        {
            // Display text immediately. Export waits for this task, so an image/location
            // still being loaded can never silently disappear from the saved result.
            InlineEditorCanvas.SetWatermark(new WatermarkComposition(settings, _watermarkTimestamp, null, null));
            if (settings.IncludeLocation && settings.UseSystemLocation)
            {
                CaptureStatusMessage = "正在获取水印位置…可继续编辑，完成时使用本次位置。";
                CaptureStatusText.Visibility = Visibility.Visible;
            }
            var result = await new WatermarkLoader().LoadAsync(settings, _watermarkTimestamp, cancellationToken,
                composition => InlineEditorCanvas.SetWatermark(composition));
            if (_isDisposed || !_watermarkEnabled) return;
            InlineEditorCanvas.SetWatermark(result.Composition);
            CaptureStatusMessage = result.Warning ?? (result.Composition.HasContent ? "已添加水印，再次点击“印”可取消。" : "水印内容为空，请在常规设置 → 水印中设置内容。");
            CaptureStatusText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (cancellationToken.IsCancellationRequested || _isDisposed) return;
            InlineEditorCanvas.SetWatermark(null);
            CaptureStatusMessage = "水印无法生成，请检查水印设置。";
            CaptureStatusText.Visibility = Visibility.Visible;
        }
    }

    private void ReleaseWatermark()
    {
        _watermarkCancellation?.Cancel();
        _watermarkCancellation?.Dispose();
        _watermarkCancellation = null;
        _watermarkTask = Task.CompletedTask;
    }
}
