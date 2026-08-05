using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSlider = System.Windows.Controls.Slider;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Pin;

public partial class PinnedImageWindow : Window
{
    private const double ShadowInset = 24;
    private const double HeaderAndShadowHeight = 54;
    private readonly CapturedImage _capturedImage;
    private readonly Func<CapturedImage, Task<OcrRecognitionResult>>?
        _recognizeTextAsync;
    private readonly Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
        _translateTextAsync;
    private OcrRecognitionResult? _recognition;
    private IReadOnlyList<OcrTextRegion> _displayedRegions = [];
    private IReadOnlyList<OcrTextRegion> _translatedRegions = [];
    private Task _textRecognitionTask = Task.CompletedTask;
    private bool _isShowingTranslation;
    private bool _isClosed;

    public PinnedImageWindow(
        CapturedImage capturedImage,
        Func<CapturedImage, Task<OcrRecognitionResult>>? recognizeTextAsync = null,
        Func<OcrRecognitionResult, Task<TranslationSegmentsResult>>?
            translateTextAsync = null)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        _capturedImage = capturedImage;
        _recognizeTextAsync = recognizeTextAsync;
        _translateTextAsync = translateTextAsync;
        InitializeComponent();
        DataContext = _capturedImage;
        TranslateButton.IsEnabled = false;
        ApplyInitialSize();
        if (_capturedImage.SourceRegion is { IsEmpty: false })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    internal Task TextRecognitionTask => _textRecognitionTask;

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        _capturedImage.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is WpfButton or WpfSlider)
        {
            return;
        }

        BeginWindowDrag(e);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetImage(_capturedImage.Preview);
        }
        catch (COMException)
        {
        }
    }

    private void OnOpacityValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsSelectableTextSource(e.OriginalSource))
        {
            return;
        }

        BeginWindowDrag(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyInitialPlacement();
        _textRecognitionTask = RecognizeTextAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        ApplyInitialPlacement();
    }

    private async Task RecognizeTextAsync()
    {
        if (_recognizeTextAsync is null || _isClosed)
        {
            HeaderStatusText.Text = "钉图";
            TranslateButton.ToolTip = "当前未配置文字识别";
            return;
        }

        HeaderStatusText.Text = "正在识别图片文字…";
        try
        {
            using var image = _capturedImage.Clone();
            var recognition = await _recognizeTextAsync(image);
            if (_isClosed)
            {
                return;
            }

            _recognition = recognition;
            if (!recognition.IsSuccess)
            {
                HeaderStatusText.Text = "文字识别失败";
                TranslateButton.ToolTip = recognition.ErrorMessage ?? "文字识别失败";
                return;
            }

            if (recognition.Regions.Count == 0)
            {
                HeaderStatusText.Text = "钉图 · 未识别到文字";
                TranslateButton.ToolTip = "图片中没有可翻译的文字";
                return;
            }

            _displayedRegions = recognition.Regions;
            _isShowingTranslation = false;
            RenderSelectableTextOverlay();
            HeaderStatusText.Text = "钉图 · 文字可选择复制";
            TranslateButton.Content = "翻译";
            TranslateButton.IsEnabled = _translateTextAsync is not null;
            TranslateButton.ToolTip = _translateTextAsync is null
                ? "请先在设置中启用翻译"
                : "翻译图片文字并覆盖显示";
        }
        catch
        {
            if (!_isClosed)
            {
                HeaderStatusText.Text = "文字识别失败";
                TranslateButton.ToolTip = "请检查 OCR 语言设置";
            }
        }
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        await TranslateTextAsync();
    }

    internal async Task TranslateTextAsync()
    {
        if (_isClosed ||
            _translateTextAsync is null ||
            _recognition is not { IsSuccess: true, Regions.Count: > 0 } recognition)
        {
            return;
        }

        if (_translatedRegions.Count > 0)
        {
            if (_isShowingTranslation)
            {
                ShowOriginalText();
            }
            else
            {
                ShowTranslatedText();
            }

            return;
        }

        TranslateButton.IsEnabled = false;
        HeaderStatusText.Text = "正在翻译图片文字…";
        try
        {
            var translation = await _translateTextAsync(recognition);
            if (_isClosed)
            {
                return;
            }

            if (!translation.IsSuccess)
            {
                HeaderStatusText.Text = "翻译失败";
                TranslateButton.ToolTip = translation.ErrorMessage ?? "翻译失败";
                TranslateButton.IsEnabled = true;
                return;
            }

            if (translation.Segments.Count != recognition.Regions.Count)
            {
                HeaderStatusText.Text = "翻译结果不完整";
                TranslateButton.ToolTip = "翻译服务返回的分段数量不一致";
                TranslateButton.IsEnabled = true;
                return;
            }

            _translatedRegions = recognition.Regions
                .Select((region, index) => new OcrTextRegion(
                    translation.Segments[index],
                    Math.Max(0, region.X - 4),
                    Math.Max(0, region.Y - 3),
                    Math.Max(20, region.Width + 8),
                    Math.Max(24, region.Height + 12))
                {
                    EstimatedFontSize = region.EstimatedFontSize,
                })
                .ToArray();
            ShowTranslatedText();
        }
        catch
        {
            if (!_isClosed)
            {
                HeaderStatusText.Text = "翻译失败";
                TranslateButton.ToolTip = "请检查翻译服务设置";
                TranslateButton.IsEnabled = true;
            }
        }
    }

    private void ShowOriginalText()
    {
        if (_recognition is not { IsSuccess: true } recognition)
        {
            return;
        }

        _displayedRegions = recognition.Regions;
        _isShowingTranslation = false;
        RenderSelectableTextOverlay();
        HeaderStatusText.Text = "钉图 · 原文可选择复制";
        TranslateButton.Content = "译文";
        TranslateButton.ToolTip = "显示已缓存的译文";
        TranslateButton.IsEnabled = true;
    }

    private void ShowTranslatedText()
    {
        _displayedRegions = _translatedRegions;
        _isShowingTranslation = true;
        RenderSelectableTextOverlay();
        HeaderStatusText.Text = "钉图 · 译文可选择复制";
        TranslateButton.Content = "原文";
        TranslateButton.ToolTip = "显示原始文字";
        TranslateButton.IsEnabled = true;
    }

    private void OnImageViewportSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (_displayedRegions.Count > 0)
        {
            RenderSelectableTextOverlay();
        }
    }

    private void RenderSelectableTextOverlay()
    {
        TextOverlay.Children.Clear();
        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        var pixelWidth = _capturedImage.Preview.PixelWidth;
        var pixelHeight = _capturedImage.Preview.PixelHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0 ||
            pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(
            viewportWidth / pixelWidth,
            viewportHeight / pixelHeight);
        var renderedWidth = pixelWidth * scale;
        var renderedHeight = pixelHeight * scale;
        var imageOffsetX = (viewportWidth - renderedWidth) / 2;
        var imageOffsetY = (viewportHeight - renderedHeight) / 2;

        foreach (var region in _displayedRegions)
        {
            var width = Math.Max(12, region.Width * scale + 2);
            var height = Math.Max(16, region.Height * scale + 2);
            var preferredFontSize = Math.Max(10, region.Height * scale * 0.78);
            var fontSize = _isShowingTranslation
                ? TranslationTextLayout.FitFontSize(
                    region.Text,
                    Math.Max(8, width - 4),
                    Math.Max(8, height - 2),
                    preferredFontSize)
                : preferredFontSize;
            var textBox = new WpfTextBox
            {
                Text = region.Text,
                Width = width,
                Height = height,
                Padding = _isShowingTranslation
                    ? new Thickness(2, 0, 2, 0)
                    : new Thickness(0),
                Background = _isShowingTranslation
                    ? new SolidColorBrush(WpfColor.FromRgb(15, 23, 26))
                    : WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = WpfCursors.IBeam,
                FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = _isShowingTranslation
                    ? FontWeights.SemiBold
                    : FontWeights.Normal,
                Foreground = _isShowingTranslation
                    ? WpfBrushes.White
                    : WpfBrushes.Transparent,
                IsReadOnly = true,
                IsTabStop = false,
                SelectionBrush = new SolidColorBrush(
                    WpfColor.FromArgb(150, 46, 175, 165)),
                SelectionTextBrush = _isShowingTranslation
                    ? WpfBrushes.White
                    : WpfBrushes.Transparent,
                TextWrapping = _isShowingTranslation
                    ? TextWrapping.Wrap
                    : TextWrapping.NoWrap,
            };
            Canvas.SetLeft(textBox, imageOffsetX + (region.X * scale));
            Canvas.SetTop(textBox, imageOffsetY + (region.Y * scale));
            TextOverlay.Children.Add(textBox);
        }
    }

    internal static bool IsSelectableTextSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is WpfTextBox)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void BeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
        e.Handled = true;
    }

    private void OnResizeEdgeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var previousWidth = Width;
            Width = Math.Clamp(Width - e.HorizontalChange, MinWidth, MaxWidth);
            Left += previousWidth - Width;
        }
        else if (direction.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var previousHeight = Height;
            Height = Math.Clamp(Height - e.VerticalChange, MinHeight, MaxHeight);
            Top += previousHeight - Height;
        }
        else if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, MaxHeight);
        }
    }

    private void ApplyInitialSize()
    {
        var referenceBounds = _capturedImage.SourceRegion is { IsEmpty: false } region
            ? new DrawingRectangle(region.X, region.Y, region.Width, region.Height)
            : System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ??
              System.Windows.Forms.SystemInformation.VirtualScreen;
        var workArea = MonitorGeometryService.GetWorkArea(referenceBounds);
        var dpi = MonitorGeometryService.GetDpiScale(referenceBounds);
        var workAreaWidth = workArea.Width / dpi.X;
        var workAreaHeight = workArea.Height / dpi.Y;
        var contentWidth = _capturedImage.Bitmap.Width / dpi.X;
        var contentHeight = _capturedImage.Bitmap.Height / dpi.Y;
        var maximumWindowWidth = Math.Max(MinWidth, workAreaWidth * 0.92);
        var maximumWindowHeight = Math.Max(MinHeight, workAreaHeight * 0.90);
        var maximumContentWidth = Math.Max(1, maximumWindowWidth - ShadowInset);
        var maximumContentHeight = Math.Max(1, maximumWindowHeight - HeaderAndShadowHeight);
        var scale = Math.Min(
            1,
            Math.Min(
                maximumContentWidth / Math.Max(1, contentWidth),
                maximumContentHeight / Math.Max(1, contentHeight)));

        MaxWidth = Math.Max(MinWidth, workAreaWidth);
        MaxHeight = Math.Max(MinHeight, workAreaHeight);
        Width = Math.Max(MinWidth, (contentWidth * scale) + ShadowInset);
        Height = Math.Max(MinHeight, (contentHeight * scale) + HeaderAndShadowHeight);
    }

    private void ApplyInitialPlacement()
    {
        if (_capturedImage.SourceRegion is not { IsEmpty: false } sourceRegion)
        {
            return;
        }

        if (!MonitorGeometryService.TryGetWindowBounds(this, out var windowBounds))
        {
            return;
        }

        var sourceBounds = new DrawingRectangle(
            sourceRegion.X,
            sourceRegion.Y,
            sourceRegion.Width,
            sourceRegion.Height);
        var workArea = MonitorGeometryService.GetWorkArea(sourceBounds);
        var dpi = MonitorGeometryService.GetDpiScale(sourceBounds);
        var contentLeftInset = (int)Math.Round((ShadowInset / 2) * dpi.X);
        var contentTopInset = (int)Math.Round(
            (HeaderAndShadowHeight - (ShadowInset / 2)) * dpi.Y);
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - windowBounds.Width);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - windowBounds.Height);
        var desiredLeft = Math.Clamp(
            sourceRegion.X - contentLeftInset,
            workArea.Left,
            maximumLeft);
        var desiredTop = Math.Clamp(
            sourceRegion.Y - contentTopInset,
            workArea.Top,
            maximumTop);
        _ = MonitorGeometryService.TryMoveWindow(
            this,
            desiredLeft,
            desiredTop);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
