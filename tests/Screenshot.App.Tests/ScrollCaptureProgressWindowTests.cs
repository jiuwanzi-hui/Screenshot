using System.Reflection;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureProgressWindowTests
{
    [Fact]
    public void WaitingStateRequiresAClickToChooseTheScrollDirection()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow();
            try
            {
                window.QueueInteractionState(
                    ControlledScrollCaptureState.WaitingToStart);

                var status = Assert.IsType<System.Windows.Controls.TextBlock>(
                    window.FindName("StatusText"));
                var instruction = Assert.IsType<System.Windows.Controls.TextBlock>(
                    window.FindName("InstructionText"));
                Assert.Contains("等待选择方向", status.Text);
                Assert.Equal("单击向下；双击向上；右键取消", instruction.Text);
                Assert.Null(typeof(ScrollCaptureWheelMonitor).GetMethod(
                    "StartControlledCapture"));
            }
            finally
            {
                window.CloseFromCoordinator();
            }
        });
    }

    [Fact]
    public void RightClickCancelsOnlyAfterTheButtonIsReleased()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow();
            var cancelCount = 0;
            window.CancelRequested += (_, _) => cancelCount++;
            window.Show();

            try
            {
                var downMethod = typeof(ScrollCaptureProgressWindow).GetMethod(
                    "OnPreviewMouseRightButtonDown",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                var upMethod = typeof(ScrollCaptureProgressWindow).GetMethod(
                    "OnPreviewMouseRightButtonUp",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                Assert.NotNull(downMethod);
                Assert.NotNull(upMethod);

                var down = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Right)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewMouseRightButtonDownEvent,
                };
                downMethod.Invoke(window, [window, down]);
                Assert.True(down.Handled);
                Assert.Equal(0, cancelCount);

                var up = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Right)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewMouseRightButtonUpEvent,
                };
                upMethod.Invoke(window, [window, up]);
                Assert.True(up.Handled);
                Assert.Equal(1, cancelCount);
            }
            finally
            {
                window.CloseFromCoordinator();
            }
        });
    }

    [Fact]
    public void BringingPreviewToFrontDoesNotMoveItToTheOrigin()
    {
        var flagsField = typeof(ScrollCaptureProgressWindow).GetField(
            "BringToFrontFlags",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(flagsField);
        var flags = Assert.IsType<uint>(flagsField.GetRawConstantValue());

        const uint doNotMove = 0x0002;
        Assert.Equal(doNotMove, flags & doNotMove);
    }

    [Fact]
    public void NarrowsPreviewToRemainImmediatelyRightOfSelection()
    {
        var captureRegion = new ScreenRegion(628, 457, 1117, 296);
        var monitorBounds = new ScreenRegion(0, 0, 1965, 1080);
        var width = GetPreviewPhysicalWidth(captureRegion, monitorBounds);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height: 296);

        Assert.Equal(208, width);
        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }


    [Fact]
    public void UsesLeftSideWhenRightSideCannotFitTheActionButtons()
    {
        var captureRegion = new ScreenRegion(500, 160, 1250, 500);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1040);
        var width = GetPreviewPhysicalWidth(captureRegion, monitorBounds);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height: 500);

        Assert.Equal(300, width);
        Assert.Equal(captureRegion.X - width - 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }

    [Fact]
    public void PositionsPreviewImmediatelyRightWhenSpaceIsAvailable()
    {
        var captureRegion = new ScreenRegion(120, 160, 800, 500);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1040);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width: 300,
            height: 500);

        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }


    [Fact]
    public void PrefersRightSideWhenGapIsBarelyEnoughForMinWidth()
    {
        // Near-fit right gap must stay glued to the selection right edge, not
        // fall back to the far monitor edge or left side when right is usable.
        var captureRegion = new ScreenRegion(100, 100, 1600, 400);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1080);
        // rightX = 100+1600+12 = 1712; rightSpace = 1920-1712 = 208 (>= 200 min)
        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width: 300,
            height: 400);

        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(previewBounds.Width <= 208);
        Assert.True(previewBounds.Width >= 200);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }

    [Fact]
    public void ScalesPreviewWidthWithMonitorDpiSoButtonsAreNotClipped()
    {
        // Field report: at 200% scaling on a 3200x2000 display the window was
        // sized as 300 raw pixels = 150 DIP, clipping the action buttons. The
        // layout needs 300 DIP, i.e. 600 physical pixels on that monitor.
        var captureRegion = new ScreenRegion(200, 200, 1200, 800);
        var monitorBounds = new ScreenRegion(0, 0, 3200, 2000);

        var width = GetPreviewPhysicalWidth(
            captureRegion,
            monitorBounds,
            dpiScaleX: 2.0);

        Assert.Equal(600, width);
    }

    private static ScreenRegion ChoosePreviewBounds(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds,
        int width,
        int height)
    {
        var method = typeof(ScrollCaptureProgressWindow).GetMethod(
            "ChoosePreviewBounds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        // All layout expectations in this file assume a 100% scale monitor,
        // where the minimum usable width is 200 physical pixels.
        return Assert.IsType<ScreenRegion>(method.Invoke(
            null,
            [captureRegion, monitorBounds, width, height, 200]));
    }

    private static int GetPreviewPhysicalWidth(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds,
        double dpiScaleX = 1.0)
    {
        var method = typeof(ScrollCaptureProgressWindow).GetMethod(
            "GetPreviewPhysicalWidth",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<int>(method.Invoke(
            null,
            [captureRegion, monitorBounds, dpiScaleX]));
    }
}
