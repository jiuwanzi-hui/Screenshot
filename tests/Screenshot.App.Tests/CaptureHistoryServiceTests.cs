using Screenshot.App.Capture;
using System.Reflection;
using System.IO;

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
    public void RemovesIndividualScreenshotsAndClearsTheRemainingHistory()
    {
        var virtualDesktop = VirtualScreen.GetBounds();
        var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1);
        var history = new CaptureHistoryService();
        using var firstImage = ScreenCaptureService.Capture(region);
        using var secondImage = ScreenCaptureService.Capture(region);
        var firstItem = history.Add(firstImage, capacity: 20);
        _ = history.Add(secondImage, capacity: 20);

        Assert.NotNull(firstItem);
        Assert.True(history.Remove(firstItem));
        Assert.Single(history.Items);
        Assert.Throws<InvalidOperationException>(firstItem.CreateCapturedImage);

        history.Clear();
        Assert.Empty(history.Items);
    }

    [Fact]
    public void VideoHistoryReloadsSavedRecordingsAndDeletesTheLocalFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"snapcut-video-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var videoPath = Path.Combine(directory, "SnapCut-20260804-203000.mp4");
        var unrelatedPath = Path.Combine(directory, "other.txt");

        try
        {
            File.WriteAllBytes(videoPath, new byte[2048]);
            File.WriteAllBytes(unrelatedPath, [1]);
            var firstSession = new VideoHistoryService();
            firstSession.Refresh(directory);

            var firstItem = Assert.Single(firstSession.Items);
            Assert.Equal(videoPath, firstItem.FilePath);
            Assert.Equal("2 KB", firstItem.FileSizeText);

            var nextSession = new VideoHistoryService();
            nextSession.Refresh(directory);
            var persistedItem = Assert.Single(nextSession.Items);
            Assert.True(nextSession.Delete(persistedItem));
            Assert.False(File.Exists(videoPath));
            Assert.True(File.Exists(unrelatedPath));
            Assert.Empty(nextSession.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VideoHistorySupportsSortingAndKeepsRenamedRecordingsVisible()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"snapcut-video-history-sort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var olderPath = Path.Combine(directory, "SnapCut-older.mp4");
        var newerPath = Path.Combine(directory, "SnapCut-newer.mp4");

        try
        {
            File.WriteAllBytes(olderPath, new byte[1024]);
            File.WriteAllBytes(newerPath, new byte[4096]);
            File.SetLastWriteTimeUtc(olderPath, new DateTime(2026, 8, 3, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 8, 4, 1, 0, 0, DateTimeKind.Utc));
            var history = new VideoHistoryService();

            history.Refresh(directory, VideoHistorySortMode.OldestFirst);
            Assert.Equal("SnapCut-older.mp4", history.Items[0].FileName);

            history.Refresh(directory, VideoHistorySortMode.LargestFirst);
            Assert.Equal("SnapCut-newer.mp4", history.Items[0].FileName);

            var renamedPath = VideoHistoryService.Rename(
                history.Items[0],
                "产品演示.mp4");
            Assert.Equal(
                Path.Combine(directory, "产品演示.mp4"),
                renamedPath);
            Assert.True(File.Exists(renamedPath));
            Assert.False(File.Exists(newerPath));

            history.Refresh(directory, VideoHistorySortMode.FileName);
            Assert.Contains(
                history.Items,
                item => item.FileName == "产品演示.mp4");
            Assert.Throws<ArgumentException>(() =>
                VideoHistoryService.NormalizeFileName("bad:name"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
                var videoList = Assert.IsType<System.Windows.Controls.ListBox>(
                    window.FindName("VideoHistoryListBox"));
                var screenshotTab = Assert.IsType<System.Windows.Controls.RadioButton>(
                    window.FindName("ScreenshotHistoryTab"));
                var videoTab = Assert.IsType<System.Windows.Controls.RadioButton>(
                    window.FindName("VideoHistoryTab"));

                Assert.True(window.IsVisible);
                Assert.Equal("历史查看", window.Title);
                Assert.True(screenshotTab.IsChecked);
                Assert.False(videoTab.IsChecked);
                Assert.Single(list.Items);
                Assert.Empty(videoList.Items);

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

    [Fact]
    public void HistoryWindowSelectsOneTabAndShowsSavedVideoRecordings()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"snapcut-video-history-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "SnapCut-20260804-132850.mp4"),
            new byte[2048]);
        File.WriteAllBytes(
            Path.Combine(directory, "SnapCut-20260804-205843.mp4"),
            new byte[4096]);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var window = new CaptureHistoryWindow(
                    new CaptureHistoryService(),
                    videoDirectory: directory);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var screenshotTab = Assert.IsType<System.Windows.Controls.RadioButton>(
                        window.FindName("ScreenshotHistoryTab"));
                    var videoTab = Assert.IsType<System.Windows.Controls.RadioButton>(
                        window.FindName("VideoHistoryTab"));
                    var screenshotPanel = Assert.IsType<System.Windows.Controls.Grid>(
                        window.FindName("ScreenshotHistoryPanel"));
                    var videoPanel = Assert.IsType<System.Windows.Controls.Grid>(
                        window.FindName("VideoHistoryPanel"));
                    var videoList = Assert.IsType<System.Windows.Controls.ListBox>(
                        window.FindName("VideoHistoryListBox"));
                    var videoEmptyText = Assert.IsType<System.Windows.Controls.TextBlock>(
                        window.FindName("VideoEmptyText"));
                    var statusText = Assert.IsType<System.Windows.Controls.TextBlock>(
                        window.FindName("HistoryStatusText"));
                    var sortComboBox = Assert.IsType<System.Windows.Controls.ComboBox>(
                        window.FindName("VideoSortComboBox"));
                    var clearButton = Assert.IsType<System.Windows.Controls.Button>(
                        window.FindName("ClearScreenshotHistoryButton"));

                    Assert.True(screenshotTab.IsChecked);
                    Assert.False(videoTab.IsChecked);
                    Assert.Equal(System.Windows.Visibility.Visible, screenshotPanel.Visibility);
                    Assert.Equal(System.Windows.Visibility.Collapsed, videoPanel.Visibility);
                    Assert.Equal(2, videoList.Items.Count);
                    Assert.Equal(4, sortComboBox.Items.Count);

                    screenshotTab.ApplyTemplate();
                    clearButton.ApplyTemplate();
                    var tabSurface = Assert.IsType<System.Windows.Controls.Border>(
                        screenshotTab.Template.FindName(
                            "TabSurface",
                            screenshotTab));
                    var buttonSurface = Assert.IsType<System.Windows.Controls.Border>(
                        clearButton.Template.FindName(
                            "Surface",
                            clearButton));
                    Assert.Equal(new System.Windows.CornerRadius(12), tabSurface.CornerRadius);
                    Assert.Equal(new System.Windows.CornerRadius(10), buttonSurface.CornerRadius);

                    videoTab.IsChecked = true;
                    window.UpdateLayout();

                    Assert.False(screenshotTab.IsChecked);
                    Assert.True(videoTab.IsChecked);
                    Assert.Equal(System.Windows.Visibility.Collapsed, screenshotPanel.Visibility);
                    Assert.Equal(System.Windows.Visibility.Visible, videoPanel.Visibility);
                    Assert.Equal(System.Windows.Visibility.Collapsed, videoEmptyText.Visibility);
                    Assert.Contains("已读取 2 个录屏文件", statusText.Text);

                    var videoCard = Assert.IsType<System.Windows.Controls.Border>(
                        videoList.ItemTemplate.LoadContent());
                    Assert.Equal(
                        new System.Windows.CornerRadius(12),
                        videoCard.CornerRadius);
                    Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(
                        videoCard.Effect);
                    var videoName = Assert.IsType<System.Windows.Controls.TextBlock>(
                        videoCard.FindName("VideoNameTextBlock"));
                    var videoNameEditor = Assert.IsType<System.Windows.Controls.TextBox>(
                        videoCard.FindName("VideoNameEditor"));
                    Assert.Equal(System.Windows.Input.Cursors.IBeam, videoName.Cursor);
                    Assert.Equal(System.Windows.Visibility.Collapsed, videoNameEditor.Visibility);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HistoryWindowCommitsInlineVideoRenameWithoutOpeningADialog()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"snapcut-video-inline-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var originalPath = Path.Combine(directory, "SnapCut-original.mp4");
        File.WriteAllBytes(originalPath, new byte[1024]);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var window = new CaptureHistoryWindow(
                    new CaptureHistoryService(),
                    videoDirectory: directory);
                try
                {
                    var item = Assert.Single(window.VideoItems);
                    var container = new System.Windows.Controls.Grid();
                    var label = new System.Windows.Controls.TextBlock
                    {
                        Tag = item,
                        Visibility = System.Windows.Visibility.Collapsed,
                    };
                    var editor = new System.Windows.Controls.TextBox
                    {
                        Tag = item,
                        Text = "会议录像",
                        Visibility = System.Windows.Visibility.Visible,
                    };
                    container.Children.Add(label);
                    container.Children.Add(editor);
                    var commitMethod = typeof(CaptureHistoryWindow).GetMethod(
                        "CommitVideoNameEdit",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    Assert.NotNull(commitMethod);
                    commitMethod.Invoke(window, [editor]);

                    Assert.False(File.Exists(originalPath));
                    Assert.True(File.Exists(Path.Combine(directory, "会议录像.mp4")));
                    Assert.Equal(System.Windows.Visibility.Visible, label.Visibility);
                    Assert.Equal(System.Windows.Visibility.Collapsed, editor.Visibility);
                    Assert.Contains(
                        window.VideoItems,
                        video => video.FileName == "会议录像.mp4");
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
