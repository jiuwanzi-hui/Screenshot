using System.IO;
using System.Windows;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class ImageEditorWindowTests
{
    [Fact]
    public void CropRectangleClampsAllFourEdgesAndKeepsOnePixel()
    {
        Assert.Equal(
            new Int32Rect(10, 20, 260, 340),
            ImageCropWindow.CalculateCropRect(
                width: 300,
                height: 400,
                left: 10,
                top: 20,
                right: 30,
                bottom: 40));
        Assert.Equal(
            new Int32Rect(99, 79, 1, 1),
            ImageCropWindow.CalculateCropRect(
                width: 100,
                height: 80,
                left: 999,
                top: 999,
                right: 999,
                bottom: 999));
    }

    [Fact]
    public void CropWindowLoadsAndUpdatesAllFourEdges()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(20, 20));
            var window = new ImageCropWindow(image.Preview);
            try
            {
                window.Show();
                window.UpdateLayout();
                var leftSlider = Assert.IsType<System.Windows.Controls.Slider>(
                    window.FindName("LeftCropSlider"));
                var topSlider = Assert.IsType<System.Windows.Controls.Slider>(
                    window.FindName("TopCropSlider"));
                var rightSlider = Assert.IsType<System.Windows.Controls.Slider>(
                    window.FindName("RightCropSlider"));
                var bottomSlider = Assert.IsType<System.Windows.Controls.Slider>(
                    window.FindName("BottomCropSlider"));
                Assert.NotNull(leftSlider.Style);
                Assert.Same(leftSlider.Style, topSlider.Style);
                Assert.Same(leftSlider.Style, rightSlider.Style);
                Assert.Same(leftSlider.Style, bottomSlider.Style);
                leftSlider.Value = 2;
                topSlider.Value = 3;
                rightSlider.Value = 4;
                bottomSlider.Value = 5;

                Assert.Equal(
                    "14 x 12 px",
                    Assert.IsType<System.Windows.Controls.TextBlock>(
                        window.FindName("CropSizeText")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CropWindowFitsWidePreviewWithoutHorizontalScrolling()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(
                new System.Drawing.Bitmap(1200, 2400));
            var window = new ImageCropWindow(image.Preview);
            try
            {
                window.Show();
                window.UpdateLayout();
                var scrollViewer = Assert.IsType<System.Windows.Controls.ScrollViewer>(
                    window.FindName("CropPreviewScrollViewer"));
                var preview = Assert.IsType<System.Windows.Controls.Image>(
                    window.FindName("CropPreviewImage"));

                Assert.Equal(
                    System.Windows.Controls.ScrollBarVisibility.Disabled,
                    scrollViewer.HorizontalScrollBarVisibility);
                Assert.Equal(0, scrollViewer.ScrollableWidth, precision: 3);
                Assert.True(scrollViewer.ViewportWidth > 0);
                Assert.InRange(
                    preview.ActualWidth,
                    1,
                    scrollViewer.ViewportWidth + 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ShowsAndClosesAnEditorWindow()
    {
        var wasVisible = false;
        var wasClosed = false;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            var image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            var window = new ImageEditorWindow(image, Path.GetTempPath());

            window.Show();
            var shapeButton = Assert.IsType<System.Windows.Controls.RadioButton>(
                window.FindName("ShapeToolButton"));
            Assert.Equal(2, shapeButton.ContextMenu.Items.Count);
            Assert.NotNull(
                System.Windows.Application.Current.FindResource(
                    typeof(System.Windows.Controls.ContextMenu)));
            Assert.NotNull(
                System.Windows.Application.Current.FindResource(
                    typeof(System.Windows.Controls.MenuItem)));
            shapeButton.ContextMenu.PlacementTarget = shapeButton;
            shapeButton.ContextMenu.IsOpen = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
            var menuShell = Assert.IsType<System.Windows.Controls.Border>(
                shapeButton.ContextMenu.Template.LoadContent());
            Assert.Equal(new CornerRadius(9), menuShell.CornerRadius);
            Assert.NotEqual(
                DependencyProperty.UnsetValue,
                menuShell.ReadLocalValue(
                    System.Windows.Controls.Border.BackgroundProperty));
            shapeButton.ContextMenu.IsOpen = false;
            wasVisible = window.IsVisible;
            window.Close();
            wasClosed = !window.IsVisible;
        });

        Assert.True(wasVisible);
        Assert.True(wasClosed);
    }

    [Fact]
    public void EditorChromeFollowsSelectedLightAndDarkThemes()
    {
        WpfTestHost.Invoke(() =>
        {
            using var themeManager = new AppThemeManager();
            themeManager.Apply(AppTheme.AuroraMist);
            using var image = new CapturedImage(new System.Drawing.Bitmap(4, 4));
            var window = new ImageEditorWindow(
                image.Clone(),
                Path.GetTempPath());

            try
            {
                window.Show();
                window.UpdateLayout();
                AssertBrushStartColor(window.Background, "#F7F8FB");
                AssertBrushStartColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorShell")).Background,
                    "#FFFFFF");
                AssertBrushStartColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorToolbarPanel")).Background,
                    "#FFFFFF");

                themeManager.Apply(AppTheme.ForestNight);
                window.UpdateLayout();
                AssertBrushStartColor(window.Background, "#15181C");
                AssertBrushStartColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorShell")).Background,
                    "#20262C");
                AssertBrushStartColor(
                    Assert.IsType<System.Windows.Controls.Border>(
                        window.FindName("EditorToolbarPanel")).Background,
                    "#1A2026");
            }
            finally
            {
                window.Close();
                themeManager.Apply(AppTheme.AuroraMist);
            }
        });
    }

    private static void AssertBrushColor(Brush brush, string expected)
    {
        var solidBrush = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(
            (Color)ColorConverter.ConvertFromString(expected),
            solidBrush.Color);
    }

    private static void AssertBrushStartColor(Brush brush, string expected)
    {
        var gradient = Assert.IsType<LinearGradientBrush>(brush);
        Assert.Equal(
            (Color)ColorConverter.ConvertFromString(expected),
            gradient.GradientStops[0].Color);
    }
}
