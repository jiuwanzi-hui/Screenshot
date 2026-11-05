using System.Diagnostics;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class CaptureStartupSchedulingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(DispatcherPriority.Background)]
    [InlineData(DispatcherPriority.ContextIdle)]
    public void StartupMakesProgressDuringSustainedInputInsteadOfWaitingForIdle(DispatcherPriority previousPriority)
    {
        WpfTestHost.Invoke(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var turns = 0;
            var startedAt = -1;
            var readyAt = -1;
            var previousAt = -1;
            double startupMs = 0, previousMs = 0;
            var clock = Stopwatch.StartNew();
            void InputWork()
            {
                // Only this off-screen test dispatcher is occupied. No desktop
                // input is injected and no system CPU/memory setting changes.
                Thread.Sleep(8);
                if (++turns < 100) dispatcher.BeginInvoke(DispatcherPriority.Input, InputWork);
            }
            dispatcher.BeginInvoke(DispatcherPriority.Input, InputWork);
            dispatcher.BeginInvoke(previousPriority, () =>
            {
                previousAt = turns;
                previousMs = clock.Elapsed.TotalMilliseconds;
                frame.Continue = false;
            });
            CaptureStartupDispatcher.Queue(dispatcher, () =>
            {
                startedAt = turns;
                CaptureStartupDispatcher.Queue(dispatcher, () =>
                {
                    readyAt = turns;
                    startupMs = clock.Elapsed.TotalMilliseconds;
                });
            });
            Assert.Equal(-1, startedAt); // Never execute inside the hotkey callback.
            Dispatcher.PushFrame(frame);
            Assert.InRange(startedAt, 0, 1);
            Assert.InRange(readyAt, 1, 2);
            Assert.Equal(100, previousAt);
            output.WriteLine($"Simulated input load: previous {previousPriority} = {previousMs:F1} ms; startup stages = {startupMs:F1} ms.");
        });
    }
}
