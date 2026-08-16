using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public void SharedColorPickerUsesLeftClickAndRightClickPaletteBehavior()
    {
        WpfTestHost.Invoke(() =>
        {
            var picker = new SharedColorPickerControl();
            var committed = new List<System.Windows.Media.Color>();
            int[]? savedPalette = null;
            var closeRequests = 0;
            picker.ColorCommitted += color => committed.Add(color);
            picker.PaletteChanged += colors => savedPalette = colors;
            picker.CloseRequested += (_, _) => closeRequests++;
            picker.SetState(
                System.Windows.Media.Color.FromRgb(0, 255, 0),
                [0x2F80ED]);
            var panel = Assert.IsType<UniformGrid>(
                picker.FindName("RecentColorsPanel"));

            Assert.True(picker.TryHandlePaletteRightClick(panel.Children[0]));
            Assert.NotNull(savedPalette);
            Assert.Equal(0x00FF00, savedPalette![0]);
            Assert.Empty(committed);
            Assert.Equal(0, closeRequests);

            panel = Assert.IsType<UniformGrid>(
                picker.FindName("RecentColorsPanel"));
            Assert.IsType<Button>(panel.Children[0]).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(
                System.Windows.Media.Color.FromRgb(0, 255, 0),
                Assert.Single(committed));
            Assert.Equal(1, closeRequests);
        });
    }

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
    public void CropWindowUsesASelectionRectangleInsteadOfEdgeSliders()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(20, 20));
            var window = new ImageCropWindow(image.Preview);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.IsType<System.Windows.Controls.Grid>(
                    window.FindName("CropInteractionSurface"));
                Assert.IsType<System.Windows.Shapes.Rectangle>(
                    window.FindName("CropSelection"));
                Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("CropMask"));
                Assert.IsType<System.Windows.Controls.Primitives.Thumb>(
                    window.FindName("CropMoveThumb"));
                Assert.IsType<System.Windows.Controls.Primitives.Thumb>(
                    window.FindName("CropBottomRightThumb"));
                Assert.Null(window.FindName("LeftCropSlider"));
                Assert.Equal(new Int32Rect(0, 0, 20, 20), window.SelectedCropRect);
                Assert.Equal(
                    "20 x 20 px",
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
    public void SelectionRectangleMapsDirectlyToSourcePixels()
    {
        Assert.Equal(
            new Int32Rect(250, 200, 500, 400),
            ImageCropWindow.CalculateCropRectFromSelection(
                new Rect(10, 20, 400, 240),
                new Rect(110, 80, 200, 120),
                pixelWidth: 1000,
                pixelHeight: 800));
    }

    [Fact]
    public void ExistingCropSelectionCanMoveAndResizeWithinTheImage()
    {
        var bounds = new Rect(0, 0, 400, 300);
        var selection = new Rect(100, 80, 160, 120);

        Assert.Equal(
            new Rect(130, 60, 160, 120),
            ImageCropWindow.AdjustSelectionRect(
                selection,
                bounds,
                "Move",
                horizontalChange: 30,
                verticalChange: -20));
        Assert.Equal(
            new Rect(80, 80, 180, 150),
            ImageCropWindow.AdjustSelectionRect(
                selection,
                bounds,
                "BottomLeft",
                horizontalChange: -20,
                verticalChange: 30));
    }

    [Fact]
    public void MovingCropSelectionPreservesItsPixelDimensions()
    {
        var cropRect = new Int32Rect(100, 80, 400, 300);
        var moved = ImageCropWindow.MoveCropRectWithoutResizing(
            cropRect,
            new Rect(10, 20, 500, 400),
            new Rect(135, 130, 200, 150),
            pixelWidth: 1000,
            pixelHeight: 800);

        Assert.Equal(new Int32Rect(250, 220, 400, 300), moved);
    }

    [Fact]
    public void CropHandlesRemainFullyClickableAtImageCorners()
    {
        var bounds = new Rect(0, 0, 400, 300);

        Assert.Equal(
            new Point(0, 0),
            ImageCropWindow.CalculateVisibleHandlePosition(
                bounds, 0, 0, 12, 12));
        Assert.Equal(
            new Point(388, 288),
            ImageCropWindow.CalculateVisibleHandlePosition(
                bounds, 400, 300, 12, 12));
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
