using System.Windows;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureFeedbackWindowTests
{
    [Fact]
    public async Task FeedbackAnimationClosesWithoutUserInteraction()
    {
        Task? feedbackTask = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        WpfTestHost.Invoke(() =>
        {
            var virtualScreen = VirtualScreen.GetBounds();
            feedbackTask = CaptureFeedbackWindow.ShowAsync(new ScreenRegion(
                virtualScreen.X + Math.Min(80, virtualScreen.Width / 8),
                virtualScreen.Y + Math.Min(80, virtualScreen.Height / 8),
                Math.Min(320, Math.Max(1, virtualScreen.Width - 160)),
                Math.Min(200, Math.Max(1, virtualScreen.Height - 160))));
        });

        await Assert.IsAssignableFrom<Task>(feedbackTask)
            .WaitAsync(TimeSpan.FromSeconds(3));
        stopwatch.Stop();
        Assert.InRange(stopwatch.ElapsedMilliseconds, 900, 2_900);
    }

    [Fact]
    public void FeedbackWindowCannotActivateOrInterceptTheTaskbar()
    {
        WpfTestHost.Invoke(() =>
        {
            var virtualScreen = VirtualScreen.GetBounds();
            var window = new CaptureFeedbackWindow(new ScreenRegion(
                virtualScreen.X,
                virtualScreen.Y,
                Math.Min(200, virtualScreen.Width),
                Math.Min(120, virtualScreen.Height)));
            try
            {
                Assert.True(window.Topmost);
                Assert.True(window.AllowsTransparency);
                Assert.False(window.ShowActivated);
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.IsHitTestVisible);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal("SnapCut 截图反馈", window.Title);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ToastPrefersBelowTheCompletedSelection()
    {
        var position = CaptureFeedbackWindow.CalculateToastPosition(
            new Rect(300, 160, 400, 260),
            new System.Windows.Size(166, 40),
            new System.Windows.Size(1200, 800));

        Assert.Equal(417, position.X);
        Assert.Equal(432, position.Y);
    }

    [Fact]
    public void ToastMovesAboveSelectionNearBottomEdge()
    {
        var position = CaptureFeedbackWindow.CalculateToastPosition(
            new Rect(300, 640, 400, 140),
            new System.Windows.Size(166, 40),
            new System.Windows.Size(1200, 800));

        Assert.Equal(417, position.X);
        Assert.Equal(588, position.Y);
    }

    [Fact]
    public void ToastRemainsVisibleForNearlyFullScreenSelection()
    {
        var position = CaptureFeedbackWindow.CalculateToastPosition(
            new Rect(0, 0, 1200, 800),
            new System.Windows.Size(166, 40),
            new System.Windows.Size(1200, 800));

        Assert.InRange(position.X, 12, 1022);
        Assert.InRange(position.Y, 12, 748);
    }
}
