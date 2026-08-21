using System.IO;
using System.Windows;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class WindowPlacementServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void StoreRoundTripsWindowBoundsAndMaximizedState()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "window-placements.json");
        var expected = new WindowPlacementRecord(
            Left: 120,
            Top: 80,
            Right: 1240,
            Bottom: 900,
            IsMaximized: true);

        var store = new WindowPlacementStore(path);
        Assert.True(store.TrySave(WindowPlacementKeys.ImageEditor, expected));

        var reloadedStore = new WindowPlacementStore(path);
        Assert.True(reloadedStore.TryGet(
            WindowPlacementKeys.ImageEditor,
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StoreIgnoresMalformedPlacementFiles()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "window-placements.json");
        File.WriteAllText(path, "{ this is not valid json }");

        var store = new WindowPlacementStore(path);

        Assert.False(store.TryGet(WindowPlacementKeys.Settings, out _));
    }

    [Fact]
    public void StoreCanForgetAWindowPlacement()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "window-placements.json");
        var placement = new WindowPlacementRecord(10, 20, 310, 220, false);
        var store = new WindowPlacementStore(path);

        Assert.True(store.TrySave(WindowPlacementKeys.VideoRecordingControls, placement));
        Assert.True(store.TryRemove(WindowPlacementKeys.VideoRecordingControls));

        var reloadedStore = new WindowPlacementStore(path);
        Assert.False(reloadedStore.TryGet(
            WindowPlacementKeys.VideoRecordingControls,
            out _));
    }

    [Fact]
    public void VisiblePlacementIsNotMoved()
    {
        var placement = new WindowPlacementRecord(
            Left: 140,
            Top: 90,
            Right: 940,
            Bottom: 690,
            IsMaximized: false);
        var workArea = new WindowPlacementService.NativeRect(
            Left: 0,
            Top: 0,
            Right: 1920,
            Bottom: 1040);

        var constrained = WindowPlacementService.ConstrainToWorkArea(
            placement,
            workArea);

        Assert.Equal(placement, constrained);
    }

    [Fact]
    public void OversizedOffscreenPlacementIsMadeReachable()
    {
        var placement = new WindowPlacementRecord(
            Left: 4800,
            Top: -2200,
            Right: 7800,
            Bottom: 1800,
            IsMaximized: true);
        var workArea = new WindowPlacementService.NativeRect(
            Left: 0,
            Top: 0,
            Right: 1920,
            Bottom: 1040);

        var constrained = WindowPlacementService.ConstrainToWorkArea(
            placement,
            workArea);

        Assert.Equal(1920, constrained.Width);
        Assert.Equal(1040, constrained.Height);
        Assert.Equal(0, constrained.Left);
        Assert.Equal(0, constrained.Top);
        Assert.True(constrained.IsMaximized);
    }

    [Fact]
    public void TrackedWindowRestoresItsPreviousNormalBounds()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "window-placements.json");

        WpfTestHost.Invoke(() =>
        {
            var workArea = SystemParameters.WorkArea;
            var expectedLeft = workArea.Left + 70;
            var expectedTop = workArea.Top + 60;
            const double expectedWidth = 620;
            const double expectedHeight = 430;
            WindowPlacementService.Initialize(path);

            try
            {
                var first = new Window
                {
                    Width = expectedWidth,
                    Height = expectedHeight,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = expectedLeft,
                    Top = expectedTop,
                };
                Assert.False(WindowPlacementService.Track(first, "testWindow"));
                first.Show();
                first.UpdateLayout();
                first.Hide();
                first.Close();

                var restored = new Window
                {
                    Width = 320,
                    Height = 240,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                Assert.True(WindowPlacementService.Track(restored, "testWindow"));
                restored.Show();
                restored.UpdateLayout();

                Assert.InRange(restored.Left, expectedLeft - 2, expectedLeft + 2);
                Assert.InRange(restored.Top, expectedTop - 2, expectedTop + 2);
                Assert.InRange(
                    restored.ActualWidth,
                    expectedWidth - 2,
                    expectedWidth + 2);
                Assert.InRange(
                    restored.ActualHeight,
                    expectedHeight - 2,
                    expectedHeight + 2);
                restored.Close();
            }
            finally
            {
                WindowPlacementService.ResetForTests();
            }
        });
    }

    [Fact]
    public void PositionOnlyTrackingRestoresFloatingLocationAndKeepsCurrentSize()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "floating-position.json");

        WpfTestHost.Invoke(() =>
        {
            var workArea = SystemParameters.WorkArea;
            var expectedLeft = workArea.Left + 36;
            var expectedTop = workArea.Top + 42;
            WindowPlacementService.Initialize(path);

            try
            {
                var first = new Window
                {
                    Width = 52,
                    Height = 52,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = expectedLeft,
                    Top = expectedTop,
                };
                Assert.False(WindowPlacementService.TrackPosition(
                    first,
                    WindowPlacementKeys.FloatingCapture));
                first.Show();
                first.UpdateLayout();
                first.Close();

                var restored = new Window
                {
                    Width = 40,
                    Height = 40,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                Assert.True(WindowPlacementService.TrackPosition(
                    restored,
                    WindowPlacementKeys.FloatingCapture));
                restored.Show();
                restored.UpdateLayout();

                Assert.InRange(restored.Left, expectedLeft - 2, expectedLeft + 2);
                Assert.InRange(restored.Top, expectedTop - 2, expectedTop + 2);
                Assert.InRange(restored.ActualWidth, 39, 41);
                Assert.InRange(restored.ActualHeight, 39, 41);
                restored.Close();
            }
            finally
            {
                WindowPlacementService.ResetForTests();
            }
        });
    }

    [Fact]
    public void PositionTrackingCanStartAfterWindowSourceIsInitialized()
    {
        Directory.CreateDirectory(_testDirectory);
        var path = Path.Combine(_testDirectory, "late-position.json");

        WpfTestHost.Invoke(() =>
        {
            var workArea = SystemParameters.WorkArea;
            var expectedLeft = workArea.Left + 84;
            var expectedTop = workArea.Top + 76;
            WindowPlacementService.Initialize(path);

            try
            {
                var first = new Window
                {
                    Width = 240,
                    Height = 80,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                first.Show();
                Assert.False(WindowPlacementService.TrackPosition(
                    first,
                    "lateTrackedWindow"));
                first.Left = expectedLeft;
                first.Top = expectedTop;
                first.UpdateLayout();
                first.Close();

                var restored = new Window
                {
                    Width = 240,
                    Height = 80,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                Assert.True(WindowPlacementService.TrackPosition(
                    restored,
                    "lateTrackedWindow"));
                restored.Show();
                restored.UpdateLayout();
                Assert.InRange(
                    restored.Left,
                    expectedLeft - 2,
                    expectedLeft + 2);
                Assert.InRange(
                    restored.Top,
                    expectedTop - 2,
                    expectedTop + 2);
                restored.Close();
            }
            finally
            {
                WindowPlacementService.ResetForTests();
            }
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
