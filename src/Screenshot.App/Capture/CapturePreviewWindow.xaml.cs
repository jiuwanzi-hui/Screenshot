using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Capture;

public partial class CapturePreviewWindow : Window
{
    private const double MinimumZoom = 0.02;
    private const double MaximumZoom = 8;
    private const double ZoomStep = 1.12;

    private readonly CapturedImage _capturedImage;
    private readonly string _saveDirectory;
    private readonly CaptureHistoryItem? _historyItem;
    private readonly bool _hasSavedPlacement;
    private bool _fitToWidth = true;
    private bool _resizeWindowToImage;
    private bool _fitUpdatePending;
    private bool _isApplyingFit;
    private bool _isPanning;
    private ScreenRegion? _pendingPositionRegion;
    private System.Windows.Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private double _zoom = 1;

    public CapturePreviewWindow(
        CapturedImage capturedImage,
        string saveDirectory,
        CaptureHistoryItem? historyItem)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        _capturedImage = capturedImage;
        _saveDirectory = saveDirectory;
        _historyItem = historyItem;

        InitializeComponent();
        _hasSavedPlacement = WindowPlacementService.Track(
            this,
            WindowPlacementKeys.CapturePreview);
        DataContext = _capturedImage;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        PreviewScrollViewer.SizeChanged += OnPreviewViewportSizeChanged;
    }

    public event EventHandler? ReselectRequested;

    public event EventHandler? EditRequested;

    public event EventHandler? PinRequested;

    public event EventHandler? OcrRequested;

    public CapturedImage CloneImage()
    {
        return _capturedImage.Clone();
    }

    public void ConfigureForHistoryView()
    {
        Title = "截图历史查看";
        _resizeWindowToImage = !_hasSavedPlacement;
        ConfirmButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        ReselectButton.Visibility = Visibility.Collapsed;
        EditButton.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        OcrButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "可复制或保存这张完整截图。";
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    public void PositionToRightOf(ScreenRegion captureRegion)
    {
        _pendingPositionRegion = captureRegion;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyPendingPosition();
    }

    protected override void OnClosed(EventArgs e)
    {
        EndPanning();
        SourceInitialized -= OnSourceInitialized;
        _capturedImage.Dispose();
        base.OnClosed(e);
        Core.MemoryFootprint.TrimAfterHeavyOperation();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPendingPosition();
        QueueFitToWidth();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyPendingPosition();
    }

    private void ApplyPendingPosition()
    {
        if (_pendingPositionRegion is not { } captureRegion ||
            !MonitorGeometryService.TryGetWindowBounds(this, out var windowBounds))
        {
            return;
        }

        var captureBounds = new DrawingRectangle(
            captureRegion.X,
            captureRegion.Y,
            captureRegion.Width,
            captureRegion.Height);
        var workArea = MonitorGeometryService.GetWorkArea(captureBounds);
        var dpi = MonitorGeometryService.GetDpiScale(captureBounds);
        var gap = Math.Max(1, (int)Math.Round(12 * dpi.X));
        var targetBounds = CalculateAdjacentBounds(
            captureBounds,
            windowBounds.Size,
            workArea,
            gap);
        if (MonitorGeometryService.TryMoveWindow(
                this,
                targetBounds.X,
                targetBounds.Y))
        {
            _pendingPositionRegion = null;
        }
    }

    internal static DrawingRectangle CalculateAdjacentBounds(
        DrawingRectangle captureBounds,
        System.Drawing.Size windowSize,
        DrawingRectangle workArea,
        int gap)
    {
        var maximumX = Math.Max(
            workArea.Left,
            workArea.Right - windowSize.Width);
        var maximumY = Math.Max(
            workArea.Top,
            workArea.Bottom - windowSize.Height);
        var centeredY = Math.Clamp(
            captureBounds.Top + ((captureBounds.Height - windowSize.Height) / 2),
            workArea.Top,
            maximumY);
        var rightX = captureBounds.Right + gap;
        if (rightX + windowSize.Width <= workArea.Right)
        {
            return new DrawingRectangle(
                rightX,
                centeredY,
                windowSize.Width,
                windowSize.Height);
        }

        var leftX = captureBounds.Left - gap - windowSize.Width;
        if (leftX >= workArea.Left)
        {
            return new DrawingRectangle(
                leftX,
                centeredY,
                windowSize.Width,
                windowSize.Height);
        }

        return new DrawingRectangle(
            Math.Clamp(rightX, workArea.Left, maximumX),
            centeredY,
            windowSize.Width,
            windowSize.Height);
    }

    private void OnPreviewViewportSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (_fitToWidth &&
            !_isApplyingFit &&
            Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.1)
        {
            QueueFitToWidth();
        }
    }

    private void OnPreviewScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (_fitToWidth &&
            !_isApplyingFit &&
            Math.Abs(e.ViewportWidthChange) > 0.1)
        {
            QueueFitToWidth();
        }
    }

    private void QueueFitToWidth()
    {
        if (_fitUpdatePending || !IsLoaded)
        {
            return;
        }

        _fitUpdatePending = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _fitUpdatePending = false;
                if (!_fitToWidth || !IsVisible)
                {
                    return;
                }

                var viewportWidth = PreviewScrollViewer.ViewportWidth;
                if (viewportWidth <= 0)
                {
                    viewportWidth = PreviewScrollViewer.ActualWidth;
                }

                _isApplyingFit = true;
                try
                {
                    SetZoom(CalculateFitWidthZoom(
                        viewportWidth,
                        _capturedImage.Preview.PixelWidth));
                    PreviewScrollViewer.ScrollToHorizontalOffset(0);
                    if (_resizeWindowToImage)
                    {
                        PreviewScrollViewer.UpdateLayout();
                        ResizeWindowToImageHeight();
                        _resizeWindowToImage = false;
                    }
                }
                finally
                {
                    _isApplyingFit = false;
                }
            });
    }

    private void ResizeWindowToImageHeight()
    {
        var imageHeight = _capturedImage.Preview.PixelHeight * _zoom;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var workArea = MonitorGeometryService.GetWorkArea(this);
        var maximumHeight = Math.Max(
            MinHeight,
            (workArea.Height / dpi.DpiScaleY) - 24);
        var nextHeight = CalculateAdaptiveWindowHeight(
            ActualHeight,
            PreviewScrollViewer.ActualHeight,
            imageHeight,
            MinHeight,
            maximumHeight);
        if (Math.Abs(nextHeight - Height) >= 1)
        {
            Height = nextHeight;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        _fitToWidth = false;
        _resizeWindowToImage = false;
        var pointer = e.GetPosition(PreviewScrollViewer);
        var contentX =
            (PreviewScrollViewer.HorizontalOffset + pointer.X) / _zoom;
        var contentY =
            (PreviewScrollViewer.VerticalOffset + pointer.Y) / _zoom;
        var nextZoom = CalculateWheelZoom(_zoom, e.Delta);
        SetZoom(nextZoom);
        PreviewScrollViewer.UpdateLayout();
        PreviewScrollViewer.ScrollToHorizontalOffset(
            (contentX * nextZoom) - pointer.X);
        PreviewScrollViewer.ScrollToVerticalOffset(
            (contentY * nextZoom) - pointer.Y);
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStartPoint = e.GetPosition(PreviewScrollViewer);
        _panStartHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = PreviewScrollViewer.VerticalOffset;
        PreviewScrollViewer.Cursor = System.Windows.Input.Cursors.SizeAll;
        _ = PreviewScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPanning();
            return;
        }

        var currentPoint = e.GetPosition(PreviewScrollViewer);
        PreviewScrollViewer.ScrollToHorizontalOffset(
            _panStartHorizontalOffset + _panStartPoint.X - currentPoint.X);
        PreviewScrollViewer.ScrollToVerticalOffset(
            _panStartVerticalOffset + _panStartPoint.Y - currentPoint.Y);
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        EndPanning();
        e.Handled = true;
    }

    private void OnPreviewLostMouseCapture(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        _isPanning = false;
        PreviewScrollViewer.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void EndPanning()
    {
        _isPanning = false;
        if (PreviewScrollViewer.IsMouseCaptured)
        {
            PreviewScrollViewer.ReleaseMouseCapture();
        }

        PreviewScrollViewer.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        PreviewScaleTransform.ScaleX = _zoom;
        PreviewScaleTransform.ScaleY = _zoom;
    }

    internal static double CalculateFitWidthZoom(
        double viewportWidth,
        int imagePixelWidth)
    {
        if (!double.IsFinite(viewportWidth) ||
            viewportWidth <= 0 ||
            imagePixelWidth <= 0)
        {
            return 1;
        }

        return Math.Clamp(
            viewportWidth / imagePixelWidth,
            MinimumZoom,
            MaximumZoom);
    }

    internal static double CalculateWheelZoom(double currentZoom, int delta)
    {
        if (!double.IsFinite(currentZoom) || currentZoom <= 0 || delta == 0)
        {
            return Math.Clamp(currentZoom, MinimumZoom, MaximumZoom);
        }

        var steps = delta / 120d;
        return Math.Clamp(
            currentZoom * Math.Pow(ZoomStep, steps),
            MinimumZoom,
            MaximumZoom);
    }

    internal static double CalculateAdaptiveWindowHeight(
        double currentWindowHeight,
        double viewportHeight,
        double scaledImageHeight,
        double minimumHeight,
        double maximumHeight)
    {
        if (!double.IsFinite(currentWindowHeight) ||
            !double.IsFinite(viewportHeight) ||
            !double.IsFinite(scaledImageHeight) ||
            !double.IsFinite(minimumHeight) ||
            !double.IsFinite(maximumHeight) ||
            currentWindowHeight <= 0 ||
            viewportHeight < 0 ||
            scaledImageHeight < 0 ||
            maximumHeight < minimumHeight)
        {
            return Math.Max(0, currentWindowHeight);
        }

        var windowChromeAndControls = Math.Max(
            0,
            currentWindowHeight - viewportHeight);
        return Math.Clamp(
            windowChromeAndControls + scaledImageHeight,
            minimumHeight,
            maximumHeight);
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardImageService.SetImageAsync(_capturedImage.Preview);
            _historyItem?.MarkCopied();
            StatusText.Text = "已复制到剪贴板。";
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ClipboardImageService.SetImageAsync(_capturedImage.Preview);
            _historyItem?.MarkCopied();
            Close();
        }
        catch
        {
            StatusText.Text = "剪贴板正被其他程序使用，请重试。";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var savedPath = CaptureFileService.SaveAsPng(
                _capturedImage,
                _saveDirectory);
            _historyItem?.MarkSaved(savedPath);
            StatusText.Text = $"已保存到 {savedPath}";
        }
        catch (Exception)
        {
            StatusText.Text = "保存失败，请检查保存位置和权限。";
        }
    }

    private void OnReselectClick(object sender, RoutedEventArgs e)
    {
        ReselectRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        EditRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        PinRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOcrClick(object sender, RoutedEventArgs e)
    {
        OcrRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
