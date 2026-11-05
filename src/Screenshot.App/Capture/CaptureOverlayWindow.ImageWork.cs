using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Text;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private CancellationTokenSource? _imageWorkCancellation;
    private volatile bool _isCaptureBackgrounded;
    private int _imageWorkEditVersion;

    private CancellationTokenSource BeginImageWork()
    {
        CancelAutomaticOcr();
        _imageWorkCancellation?.Cancel();
        return _imageWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellationSource.Token);
    }
    private bool IsImageWorkCurrent(CancellationTokenSource work, int generation) =>
        !_isCompleted && !_isDisposed && !work.IsCancellationRequested &&
        ReferenceEquals(_imageWorkCancellation, work) && generation == _automaticRecognitionGeneration;
    private void EndImageWork(CancellationTokenSource work)
    { if (ReferenceEquals(_imageWorkCancellation, work)) _imageWorkCancellation = null; }

    private void OnBackgroundCaptureClick(object sender, RoutedEventArgs e) => BackgroundCapture();
    private void BackgroundCapture()
    {
        if (_isCompleted || _isDisposed) return;
        ReleaseOverlayMouseCapture();
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }
    private void ReturnToCapture()
    {
        if (_isCompleted || _isDisposed) return;
        WindowState = WindowState.Normal;
        Activate();
    }
    private void OnBackgroundStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _isCaptureBackgrounded = true;
            _nativeSelectionFrame.Hide(); _nativeSelectionMask.HideMask(); _nativeSelectionSize.HideSize();
        }
        else if (_isCaptureBackgrounded && !_isCompleted && !_isDisposed)
        {
            _isCaptureBackgrounded = false;
            if (HasValidSelection()) { UpdateSelectionControlPositions(GetSelectionBounds()); UpdateNativeSelectionVisuals(GetSelectionBounds()); }
        }
    }

    private void PositionImageQuestionButton(Rect bounds)
    {
        if (ImageQuestionButton is null) return;
        ImageQuestionButton.Visibility = (_options?.SettingsProvider?.Invoke().EnableImageQuestion ?? true) && !_isScrollCaptureSelection && CaptureToolbar.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        var width = Math.Max(40, CaptureSurface.ActualWidth);
        var height = Math.Max(104, CaptureSurface.ActualHeight);
        Canvas.SetLeft(ImageQuestionButton, Math.Clamp(bounds.Right + 6, 0, width - 38));
        Canvas.SetTop(ImageQuestionButton, Math.Clamp(bounds.Top + (bounds.Height - 100) / 2, 0, height - 100));
    }

    private async void OnImageQuestionClick(object sender, RoutedEventArgs e)
    {
        if (!HasValidSelection() || _isActionInProgress || _isEditorInitializing || !ImageQuestionButton.IsEnabled) return;
        ImageQuestionButton.IsEnabled = false;
        try
        {
            var profiles = await ImageQuestionService.GetReadyProfilesAsync(_options?.SettingsProvider?.Invoke() ?? new AppSettings());
            if (_isCompleted || _isDisposed) return;
            if (profiles.Length == 0)
            { CaptureStatusMessage = "没有配置在线 AI 模型，请先在设置 → 翻译中配置并启用支持图片的模型。"; CaptureStatusText.Visibility = Visibility.Visible; return; }
            if (!BeginAction()) return;
            System.Windows.Media.Imaging.BitmapSource? snapshot;
            try { snapshot = await CompleteCaptureAsync(showSuccessNotice: false); }
            catch { ShowCaptureCompletionFailure(); return; }
            if (snapshot is null) return;
            var chat = new ImageQuestionWindow(snapshot, profiles);
            chat.ShowForUserRequest();
        }
        catch { if (!_isCompleted) { CaptureStatusMessage = "无法打开问图，请重试。"; CaptureStatusText.Visibility = Visibility.Visible; }
            else _ = StartupFeedbackWindow.ShowAsync("截图已完成，但无法打开问图，请从快捷键重试。"); }
        finally { if (!_isDisposed) ImageQuestionButton.IsEnabled = true; }
    }
}
