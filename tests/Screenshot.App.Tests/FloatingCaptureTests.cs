using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class FloatingCaptureTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FloatingWindowProvidesExpectedCommandsWithoutTaskbarEntry()
    {
        Directory.CreateDirectory(_testDirectory);
        WindowPlacementService.Initialize(
            Path.Combine(_testDirectory, "placements.json"));

        WpfTestHost.Invoke(() =>
        {
            var window = new FloatingCaptureWindow();
            try
            {
                Assert.True(window.Topmost);
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.ShowActivated);
                Assert.Equal("SnapCut 悬浮按钮", window.Title);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal(40, window.Width);
                Assert.Equal(40, window.Height);
                var floatingButton = Assert.IsType<Border>(
                    window.FindName("FloatingButton"));
                Assert.Equal(
                    System.Windows.Media.Colors.Transparent,
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(
                        floatingButton.Background).Color);
                Assert.Equal(new Thickness(0), floatingButton.BorderThickness);
                var floatingButtonIcon = Assert.IsType<Image>(
                    window.FindName("FloatingButtonIcon"));
                Assert.Equal(32, floatingButtonIcon.Width);
                Assert.Equal(32, floatingButtonIcon.Height);
                Assert.IsType<Button>(window.FindName("RegionCaptureMenuButton"));
                Assert.IsType<Button>(window.FindName("ScrollCaptureMenuButton"));
                Assert.IsType<Button>(window.FindName("VideoRecordingMenuButton"));
                Assert.IsType<Button>(window.FindName("PinCaptureMenuButton"));
                Assert.IsType<Button>(window.FindName("AllScreensCaptureMenuButton"));
                Assert.Equal(
                    "历史查看",
                    Assert.IsType<Button>(
                        window.FindName("HistoryMenuButton")).Content);
                Assert.IsType<Popup>(window.FindName("FeatureMenuPopup"));
                var firstSeparator = Assert.IsType<Border>(
                    window.FindName("RegionScrollMenuSeparator"));
                var secondSeparator = Assert.IsType<Border>(
                    window.FindName("ScrollVideoMenuSeparator"));
                var thirdSeparator = Assert.IsType<Border>(
                    window.FindName("VideoPinMenuSeparator"));
                var fourthSeparator = Assert.IsType<Border>(
                    window.FindName("PinAllScreensMenuSeparator"));
                var fifthSeparator = Assert.IsType<Border>(
                    window.FindName("AllScreensHistoryMenuSeparator"));
                Assert.Equal(1, firstSeparator.Height);
                Assert.Equal(1, secondSeparator.Height);
                Assert.Equal(1, thirdSeparator.Height);
                Assert.Equal(1, fourthSeparator.Height);
                Assert.Equal(1, fifthSeparator.Height);
                Assert.Equal(74, Assert.IsType<Button>(
                    window.FindName("RegionCaptureMenuButton")).MinWidth);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VideoRecordingCommandRaisesRequest()
    {
        Directory.CreateDirectory(_testDirectory);
        WindowPlacementService.Initialize(
            Path.Combine(_testDirectory, "video-command-placements.json"));

        WpfTestHost.Invoke(() =>
        {
            var window = new FloatingCaptureWindow();
            var requested = false;
            window.VideoRecordingRequested += (_, _) => requested = true;
            try
            {
                var button = Assert.IsType<Button>(
                    window.FindName("VideoRecordingMenuButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PinAndAllScreensCommandsRaiseRequests()
    {
        Directory.CreateDirectory(_testDirectory);
        WindowPlacementService.Initialize(
            Path.Combine(_testDirectory, "capture-command-placements.json"));

        WpfTestHost.Invoke(() =>
        {
            var window = new FloatingCaptureWindow();
            var pinRequested = false;
            var allScreensRequested = false;
            window.PinCaptureRequested += (_, _) => pinRequested = true;
            window.AllScreensCaptureRequested += (_, _) =>
                allScreensRequested = true;
            try
            {
                Assert.IsType<Button>(window.FindName("PinCaptureMenuButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(window.FindName("AllScreensCaptureMenuButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(pinRequested);
                Assert.True(allScreensRequested);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(0, 200, (int)FloatingDockEdge.Left)]
    [InlineData(760, 200, (int)FloatingDockEdge.Right)]
    [InlineData(300, 0, (int)FloatingDockEdge.Top)]
    [InlineData(300, 560, (int)FloatingDockEdge.Bottom)]
    [InlineData(300, 200, (int)FloatingDockEdge.None)]
    public void NearestDockEdgeUsesTheCurrentMonitorWorkArea(
        int x,
        int y,
        int expected)
    {
        var workArea = new System.Drawing.Rectangle(0, 0, 800, 600);
        var bounds = new System.Drawing.Rectangle(x, y, 40, 40);

        var edge = FloatingCaptureLayout.FindNearestDockEdge(
            bounds,
            workArea,
            threshold: 24);

        Assert.Equal((FloatingDockEdge)expected, edge);
    }

    [Fact]
    public void DockingSupportsNegativeCoordinateSecondaryMonitor()
    {
        var workArea = new System.Drawing.Rectangle(-1920, -120, 1920, 1080);
        var bounds = new System.Drawing.Rectangle(-900, 400, 40, 40);

        var docked = FloatingCaptureLayout.DockToWorkArea(
            bounds,
            workArea,
            FloatingDockEdge.Right);

        Assert.Equal(-40, docked.X);
        Assert.Equal(400, docked.Y);
        Assert.True(workArea.Contains(docked));
    }

    [Theory]
    [InlineData(400, 300, (int)FloatingMenuDirection.Top)]
    [InlineData(400, 50, (int)FloatingMenuDirection.Right)]
    [InlineData(400, 0, (int)FloatingMenuDirection.Bottom)]
    [InlineData(760, 300, (int)FloatingMenuDirection.Left)]
    public void MenuPlacementUsesTopRightBottomLeftPriority(
        int x,
        int y,
        int expected)
    {
        var direction = FloatingCaptureLayout.ChooseMenuDirection(
            new System.Drawing.Rectangle(x, y, 40, 40),
            new System.Drawing.Size(100, 110),
            new System.Drawing.Rectangle(0, 0, 800, 600),
            gap: 7);

        Assert.Equal((FloatingMenuDirection)expected, direction);
    }

    [Fact]
    public void CloseContextCommandRaisesPersistentCloseRequest()
    {
        Directory.CreateDirectory(_testDirectory);
        WindowPlacementService.Initialize(
            Path.Combine(_testDirectory, "close-command-placements.json"));

        WpfTestHost.Invoke(() =>
        {
            var window = new FloatingCaptureWindow();
            var closeRequested = false;
            window.CloseRequested += (_, _) => closeRequested = true;
            try
            {
                var button = Assert.IsType<Border>(
                    window.FindName("FloatingButton"));
                var menuItem = Assert.IsType<MenuItem>(
                    Assert.Single(button.ContextMenu!.Items));
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(closeRequested);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(10, 10, 200, 120, true)]
    [InlineData(-20, 10, 200, 120, false)]
    [InlineData(10, 10, 1000, 120, false)]
    public void ReusableRegionMustRemainFullyInsideCurrentVirtualScreen(
        int x,
        int y,
        int width,
        int height,
        bool expected)
    {
        var result = RegionCaptureCoordinator.TryGetReusableRegion(
            new ScreenRegion(x, y, width, height),
            new ScreenRegion(0, 0, 800, 600));

        Assert.Equal(expected, result.HasValue);
    }

    public void Dispose()
    {
        WindowPlacementService.ResetForTests();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
