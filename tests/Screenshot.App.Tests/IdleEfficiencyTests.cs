using System.Reflection;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class IdleEfficiencyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void NeverThrottlesVisibleWorkOrCapture(bool capturing, bool visible, bool efficient) =>
        Assert.Equal(efficient, IdleEfficiencyController.CanUseEfficiency(capturing, visible));

    [Fact]
    public void IdleTransitionsRestorePerformanceBeforeWorkAndDisposeStopsTheTimer()
    {
        WpfTestHost.Invoke(() =>
        {
            var changes = new List<bool>();
            using var controller = new IdleEfficiencyController(changes.Add);
            var idle = typeof(IdleEfficiencyController).GetMethod("OnIdle", BindingFlags.NonPublic | BindingFlags.Instance)!;
            idle.Invoke(controller, [null, EventArgs.Empty]);
            Assert.True(controller.IsEfficient);
            controller.SetCaptureActive(true);
            Assert.False(controller.IsEfficient);
            idle.Invoke(controller, [null, EventArgs.Empty]);
            Assert.False(controller.IsEfficient);
            controller.SetCaptureActive(false);
            idle.Invoke(controller, [null, EventArgs.Empty]);
            Assert.True(controller.IsEfficient);
            controller.Wake();
            Assert.False(controller.IsEfficient);
            idle.Invoke(controller, [null, EventArgs.Empty]);
            controller.Dispose();
            Assert.False(controller.IsEfficient);
            var timer = (System.Windows.Threading.DispatcherTimer)typeof(IdleEfficiencyController)
                .GetField("_idleTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
            Assert.False(timer.IsEnabled);
            Assert.Equal([true, false, true, false, true, false], changes);
        });
    }
}
