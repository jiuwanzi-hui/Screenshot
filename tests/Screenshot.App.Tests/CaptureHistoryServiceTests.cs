using Screenshot.App.Capture;
using System.Reflection;

namespace Screenshot.App.Tests;

public sealed class CaptureHistoryServiceTests
{
    [Fact]
    public void KeepsOnlyTheConfiguredNumberOfHistoryItems()
    {
        var virtualDesktop = VirtualScreen.GetBounds();
        var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1);
        var history = new CaptureHistoryService();

        using var firstImage = ScreenCaptureService.Capture(region);
        using var secondImage = ScreenCaptureService.Capture(region);

        _ = history.Add(firstImage, capacity: 1);
        var retainedItem = history.Add(secondImage, capacity: 1);

        Assert.NotNull(retainedItem);
        Assert.Single(history.Items);
        Assert.Same(retainedItem, history.Items[0]);
        Assert.Equal(1, retainedItem.Thumbnail.PixelWidth);
        Assert.Equal(1, retainedItem.Thumbnail.PixelHeight);
        using var restored = retainedItem.CreateCapturedImage();
        Assert.Equal(1, restored.Bitmap.Width);
        Assert.Equal(1, restored.Bitmap.Height);
    }

    [Fact]
    public void HistoryWindowRendersReadOnlyImageDimensionsWithoutCrashing()
    {
        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 2, 2);
            var history = new CaptureHistoryService();
            using var image = ScreenCaptureService.Capture(region);
            _ = history.Add(image, capacity: 20);
            var window = new CaptureHistoryWindow(history);
            CapturePreviewWindow? preview = null;

            try
            {
                window.Show();
                window.UpdateLayout();
                var list = Assert.IsType<System.Windows.Controls.ListBox>(
                    window.FindName("HistoryListBox"));

                Assert.True(window.IsVisible);
                Assert.Single(list.Items);

                var viewMethod = typeof(CaptureHistoryWindow).GetMethod(
                    "OnViewHistoryItemClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(viewMethod);
                var viewButton = new System.Windows.Controls.Button
                {
                    Tag = history.Items[0],
                };
                viewMethod.Invoke(
                    window,
                    [viewButton, new System.Windows.RoutedEventArgs()]);
                preview = Assert.Single(
                    System.Windows.Application.Current.Windows
                        .OfType<CapturePreviewWindow>());
                Assert.Equal("截图历史查看", preview.Title);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    Assert.IsType<System.Windows.Controls.Button>(
                        preview.FindName("EditButton")).Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    Assert.IsType<System.Windows.Controls.Button>(
                        preview.FindName("ConfirmButton")).Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    Assert.IsType<System.Windows.Controls.Button>(
                        preview.FindName("CloseButton")).Visibility);
            }
            finally
            {
                preview?.Close();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(900, 1800, 0.5)]
    [InlineData(800, 400, 2)]
    [InlineData(400, 40000, 0.02)]
    public void PreviewFitZoomUsesTheAvailableImageWidth(
        double viewportWidth,
        int imageWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            CapturePreviewWindow.CalculateFitWidthZoom(
                viewportWidth,
                imageWidth),
            precision: 6);
    }

    [Fact]
    public void PreviewWheelZoomIsIncrementalAndBounded()
    {
        var zoomedIn = CapturePreviewWindow.CalculateWheelZoom(1, 120);
        var zoomedOut = CapturePreviewWindow.CalculateWheelZoom(1, -120);

        Assert.True(zoomedIn > 1);
        Assert.True(zoomedOut < 1);
        Assert.Equal(
            8,
            CapturePreviewWindow.CalculateWheelZoom(8, 120));
        Assert.Equal(
            0.02,
            CapturePreviewWindow.CalculateWheelZoom(0.02, -120));
    }

    [Theory]
    [InlineData(560, 450, 220, 280, 900, 330)]
    [InlineData(560, 450, 1200, 280, 900, 900)]
    [InlineData(560, 450, 40, 280, 900, 280)]
    public void PreviewWindowHeightFollowsTheScaledImageWithoutExceedingTheScreen(
        double currentWindowHeight,
        double viewportHeight,
        double scaledImageHeight,
        double minimumHeight,
        double maximumHeight,
        double expected)
    {
        Assert.Equal(
            expected,
            CapturePreviewWindow.CalculateAdaptiveWindowHeight(
                currentWindowHeight,
                viewportHeight,
                scaledImageHeight,
                minimumHeight,
                maximumHeight));
    }
}
