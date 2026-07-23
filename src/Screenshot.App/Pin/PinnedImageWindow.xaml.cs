using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Capture;
using WpfButton = System.Windows.Controls.Button;
using WpfSlider = System.Windows.Controls.Slider;

namespace Screenshot.App.Pin;

public partial class PinnedImageWindow : Window
{
    private const double ShadowInset = 24;
    private const double HeaderAndShadowHeight = 54;
    private readonly CapturedImage _capturedImage;
    private readonly Func<CapturedImage, Task>? _recognizeTextAsync;

    public PinnedImageWindow(
        CapturedImage capturedImage,
        Func<CapturedImage, Task>? recognizeTextAsync = null)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        _capturedImage = capturedImage;
        _recognizeTextAsync = recognizeTextAsync;
        InitializeComponent();
        DataContext = _capturedImage;
        OcrButton.IsEnabled = _recognizeTextAsync is not null;
        ApplyInitialSize();
        ApplyInitialPlacement();
    }

    protected override void OnClosed(EventArgs e)
    {
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
        BeginWindowDrag(e);
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (_recognizeTextAsync is null)
        {
            return;
        }

        OcrButton.IsEnabled = false;
        try
        {
            using var image = _capturedImage.Clone();
            await _recognizeTextAsync(image);
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
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
        var workArea = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var contentWidth = _capturedImage.Bitmap.Width / dpi.DpiScaleX;
        var contentHeight = _capturedImage.Bitmap.Height / dpi.DpiScaleY;
        var maximumWindowWidth = Math.Max(MinWidth, workArea.Width * 0.92);
        var maximumWindowHeight = Math.Max(MinHeight, workArea.Height * 0.90);
        var maximumContentWidth = Math.Max(1, maximumWindowWidth - ShadowInset);
        var maximumContentHeight = Math.Max(1, maximumWindowHeight - HeaderAndShadowHeight);
        var scale = Math.Min(
            1,
            Math.Min(
                maximumContentWidth / Math.Max(1, contentWidth),
                maximumContentHeight / Math.Max(1, contentHeight)));

        MaxWidth = Math.Max(MinWidth, workArea.Width);
        MaxHeight = Math.Max(MinHeight, workArea.Height);
        Width = Math.Max(MinWidth, (contentWidth * scale) + ShadowInset);
        Height = Math.Max(MinHeight, (contentHeight * scale) + HeaderAndShadowHeight);
    }

    private void ApplyInitialPlacement()
    {
        if (_capturedImage.SourceRegion is not { IsEmpty: false } sourceRegion)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var virtualScreen = VirtualScreen.GetBounds();
        var minimumLeft = virtualScreen.X / dpi.DpiScaleX;
        var minimumTop = virtualScreen.Y / dpi.DpiScaleY;
        var maximumLeft = minimumLeft +
                          (virtualScreen.Width / dpi.DpiScaleX) - Width;
        var maximumTop = minimumTop +
                         (virtualScreen.Height / dpi.DpiScaleY) - Height;
        var contentLeftInset = ShadowInset / 2;
        var contentTopInset = HeaderAndShadowHeight - contentLeftInset;
        var desiredLeft = (sourceRegion.X / dpi.DpiScaleX) - contentLeftInset;
        var desiredTop = (sourceRegion.Y / dpi.DpiScaleY) - contentTopInset;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(desiredLeft, minimumLeft, Math.Max(minimumLeft, maximumLeft));
        Top = Math.Clamp(desiredTop, minimumTop, Math.Max(minimumTop, maximumTop));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
