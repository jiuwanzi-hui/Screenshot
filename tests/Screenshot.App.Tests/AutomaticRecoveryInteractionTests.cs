using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using System.Windows.Controls;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticRecoveryInteractionTests
{
    [Theory]
    [InlineData(ScrollCapturePointerAction.Click)]
    [InlineData(ScrollCapturePointerAction.Pause)]
    public async Task ClickDuringBacktrackingStopsInsteadOfBeingReinterpretedAsResume(ScrollCapturePointerAction click)
    {
        using var document = new Bitmap(180, 500, PixelFormat.Format32bppPArgb);
        var random = new Random(92913);
        for (var y = 0; y < document.Height; y++)
        for (var x = 0; x < document.Width; x++)
            document.SetPixel(x, y, Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
        using var initial = document.Clone(new Rectangle(0, 0, 180, 180), PixelFormat.Format32bppPArgb);
        using var unrelated = document.Clone(new Rectangle(0, 300, 180, 180), PixelFormat.Format32bppPArgb);
        var clicked = 0;
        var inputAfterClick = 0;
        var paused = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var corrections = new List<ScrollCaptureDirection?>();
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(
            new(new(1), new(1), new(0, 0, 180, 180), false), completion.Task, actions.Reader,
            stateChanged: state =>
            {
                if (state != ControlledScrollCaptureState.PausedDown) return;
                paused = true;
                _ = Task.Run(async () => { await Task.Delay(250); completion.TrySetResult(); });
            },
            initialFrame: new Bitmap(initial),
            driverFactory: target => new ControlledScrollDriver(target, (_, delta) =>
            {
                if (Volatile.Read(ref clicked) != 0) Interlocked.Increment(ref inputAfterClick);
                if (delta > 0 && Interlocked.Exchange(ref clicked, 1) == 0)
                    actions.Writer.TryWrite(click);
                return true;
            }, _ => new Bitmap(unrelated)),
            recoveryDirectionChanged: direction => corrections.Add(direction),
            cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.Equal(1, clicked);
        Assert.True(paused);
        Assert.Equal(0, inputAfterClick);
        Assert.Contains(ScrollCaptureDirection.Up, corrections);
        Assert.Null(corrections[^1]);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(initial, image.Bitmap);
    }

    [Theory]
    [InlineData(ControlledScrollCaptureState.PausedDown)]
    [InlineData(ControlledScrollCaptureState.PausedUp)]
    [InlineData(ControlledScrollCaptureState.PreparingPauseDown)]
    public void DelayedPauseNeverStartsMotion(ControlledScrollCaptureState state) =>
        Assert.Equal(state, ScrollCaptureService.ApplyControlledPointerAction(state, ScrollCapturePointerAction.Pause));

    [Fact]
    public async Task ReverseRecoveryCannotReplenishInputAtTheSamePosition()
    {
        var signedInput = 0;
        await using var driver = new ControlledScrollDriver(new(new(1), new(1), new(0, 0, 180, 180), false),
            (_, delta) => { Interlocked.Add(ref signedInput, delta); return true; },
            _ => new Bitmap(180, 180));
        // There has been no new forward input. Only the bounded allowance for
        // in-flight presentation may be undone, even across repeated rounds.
        Assert.True(driver.StepBackForRecovery(ScrollCaptureDirection.Down));
        Assert.True(driver.StepBackForRecovery(ScrollCaptureDirection.Down));
        Assert.False(driver.StepBackForRecovery(ScrollCaptureDirection.Down));
        Assert.False(driver.StepBackForRecovery(ScrollCaptureDirection.Down));
        Assert.Equal(240, signedInput);
    }

    [Fact]
    public void RecoveryDirectionIsVisibleWithoutMovingThePreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow { Left = 800, Top = 160 };
            try
            {
                window.QueueInteractionState(ControlledScrollCaptureState.ScrollingDown);
                window.QueueRecoveryDirection(ScrollCaptureDirection.Up);
                Assert.Contains("向上", ((TextBlock)window.FindName("StatusText")).Text);
                window.QueueRecoveryDirection(null);
                Assert.Contains("向下", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.Equal(800, window.Left);
                Assert.Equal(160, window.Top);
            }
            finally { window.CloseFromCoordinator(); }
        });
    }
}
