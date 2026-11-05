using System.IO;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class CaptureModeTransitionTests
{
    [DesktopTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectionStaysVisibleUntilReplacementIsReadyWithoutReleasingCaptureState(bool scroll, bool withSnapshot)
    {
        CaptureOverlayWindow? overlay = null;
        PinnedImageManager? pins = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedNotifications = 0;
        Task Start(ScreenRegion _)
        {
            started.TrySetResult();
            return finished.Task;
        }
        try
        {
            WpfTestHost.Invoke(() =>
            {
                pins = new PinnedImageManager();
                var screen = VirtualScreen.GetBounds();
                var snapshot = new CapturedImage(new System.Drawing.Bitmap(screen.Width, screen.Height), screen);
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pins,
                    StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                    InitialSelection = new ScreenRegion(screen.X + 100, screen.Y + 100, 480, 240),
                    StartVideoRecordingAsync = Start,
                    StartScrollCaptureAsync = Start,
                    StartScrollCaptureWithSnapshotAsync = withSnapshot ? (region, image) =>
                    {
                        image?.Dispose();
                        return Start(region);
                    } : null,
                    CaptureClosed = () => closedNotifications++,
                }, snapshot);
            });
            // Allow the existing inline editor to finish preparing the selection.
            await Task.Delay(200);
            WpfTestHost.Invoke(() =>
            {
                var button = Assert.IsType<Button>(overlay!.FindName(scroll ? "ScrollCaptureButton" : "RecordButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            WpfTestHost.Invoke(() =>
            {
                Assert.True(overlay!.IsVisible);
                Assert.Equal(0, closedNotifications);
                CaptureOverlayWindow.CloseActiveInteractiveSelectionForTransition();
                Assert.False(overlay.IsVisible);
            });
            await Task.Delay(200);
            Assert.Equal(0, closedNotifications);
            finished.TrySetResult();
        }
        finally
        {
            finished.TrySetResult();
            WpfTestHost.Invoke(() => { overlay?.Close(); pins?.Dispose(); });
        }
    }

    [DesktopFact]
    public void ResolvingTargetDoesNotHideOrReparentTheVisiblePreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var region = new ScreenRegion(screen.X + 100, screen.Y + 100, 480, 240);
            var window = new ScrollCaptureProgressWindow();
            var changes = new List<bool>();
            try
            {
                window.ConfigureForCaptureRegion(region);
                window.IsVisibleChanged += (_, args) => changes.Add((bool)args.NewValue);
                window.Show();
                window.Left = region.X;
                window.Top = region.Y;
                window.UpdateLayout();
                Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(
                    region, out var target, ignoreCaptureProcess: true));
                Assert.NotNull(target);
                Assert.True(window.IsVisible);
                Assert.Null(window.Owner);
                Assert.Equal([true], changes);
            }
            finally { window.CloseFromCoordinator(); }
        });
    }
}
