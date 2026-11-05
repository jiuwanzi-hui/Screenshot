using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureInputProtectionTests
{
    private static readonly ScreenRegion Region = new(100, 100, 300, 240);
    private static readonly ScreenRegion Preview = new(420, 80, 260, 400);

    [Theory]
    [InlineData(0x0201, 0x0202)]
    [InlineData(0x0207, 0x0208)]
    [InlineData(0x020B, 0x020C)]
    public void ManualModeBlocksClicksInsideAndOutsideSelectionButAllowsPreview(int down, int up)
    {
        using var monitor = Create();
        monitor.BlockNonWheelInput();
        foreach (var point in new[] { (150, 150), (800, 600) })
        {
            Assert.Equal(1, Send(monitor, down, point.Item1, point.Item2));
            Assert.Equal(1, Send(monitor, up, point.Item1, point.Item2));
        }
        Assert.Equal(0, Send(monitor, down, 500, 200));
        Assert.Equal(0, Send(monitor, up, 500, 200));
        Assert.False(monitor.PointerActions.TryRead(out _));
    }

    [Fact]
    public void WheelOnlyScrollsSelectionAndPreview()
    {
        using var monitor = Create();
        monitor.BlockNonWheelInput();
        Assert.Equal(0, Send(monitor, 0x020A, 150, 150, -120));
        Assert.True(monitor.WheelEvents.TryRead(out var delta));
        Assert.Equal(-120, delta);
        Assert.Equal(1, Send(monitor, 0x020A, 800, 600, -120));
        Assert.Equal(0, Send(monitor, 0x020A, 500, 200, -120));
        Assert.False(monitor.WheelEvents.TryRead(out _));
        Assert.Equal(0, Send(monitor, 0x0200, 800, 600));
    }

    [Fact]
    public void DragReleaseStaysWithTheSurfaceWhereItStarted()
    {
        using var monitor = Create();
        monitor.BlockNonWheelInput();
        Assert.Equal(0, Send(monitor, 0x0201, 500, 200));
        Assert.Equal(0, Send(monitor, 0x0202, 800, 600));
        Assert.Equal(1, Send(monitor, 0x0201, 800, 600));
        Assert.Equal(1, Send(monitor, 0x0202, 500, 200));
    }

    [Fact]
    public void AutomaticClickStillControlsCaptureWhileOutsideClicksStayBlocked()
    {
        using var monitor = Create();
        monitor.EnableControlledCaptureInput();
        monitor.ConfigureClickDeferral(() => false);
        Assert.Equal(1, Send(monitor, 0x0201, 150, 150));
        Assert.Equal(1, Send(monitor, 0x0202, 150, 150));
        Assert.True(monitor.PointerActions.TryRead(out var action));
        Assert.Equal(ScrollCapturePointerAction.Pause, action);
        Assert.Equal(1, Send(monitor, 0x0201, 800, 600));
        Assert.Equal(1, Send(monitor, 0x0202, 800, 600));
        Assert.False(monitor.PointerActions.TryRead(out _));
    }

    [Fact]
    public void RightClickCancelsOnReleaseAndDisposalRestoresInput()
    {
        var cancelled = 0;
        using var monitor = Create(() => cancelled++);
        monitor.BlockNonWheelInput();
        Assert.Equal(1, Send(monitor, 0x0204, 150, 150));
        Assert.Equal(0, cancelled);
        Assert.Equal(1, Send(monitor, 0x0205, 500, 200));
        Assert.Equal(1, cancelled);
        monitor.Dispose();
        Assert.Equal(0, Send(monitor, 0x0201, 150, 150));
        Assert.Equal(0, Send(monitor, 0x0204, 800, 600));
        Assert.Equal(0, Send(monitor, 0x020A, 800, 600, -120));
    }

    private static ScrollCaptureWheelMonitor Create(Action? cancel = null) =>
        new(Region, null, Preview.Contains, cancel, installHook: false);

    private static long Send(ScrollCaptureWheelMonitor monitor, int message, int x, int y, short delta = 0)
    {
        // Feed synthetic records directly to the callback. No global hooks
        // are installed and no mouse/keyboard input is injected.
        var data = new MouseRecord { X = x, Y = y, MouseData = unchecked((uint)(delta << 16)), Time = 1000 };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MouseRecord>());
        try
        {
            Marshal.StructureToPtr(data, pointer, false);
            return ((IntPtr)typeof(ScrollCaptureWheelMonitor).GetMethod("OnMouseHook",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(monitor, [0, new IntPtr(message), pointer])!).ToInt64();
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseRecord
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInformation;
    }
}
