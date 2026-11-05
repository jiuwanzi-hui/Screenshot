using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Xunit.Abstractions;
using Point = System.Drawing.Point;

namespace Screenshot.App.Tests;

public sealed class NativeWindowDragTrackerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(125, 60)]
    [InlineData(4_000, 60)]
    [InlineData(4_000, 144)]
    [InlineData(4_000, 240)]
    [InlineData(8_000, 144)]
    public void MousePacketsNeverReachWpfDuringDragAndEachFrameUsesLatestPosition(int reportHz, int displayHz)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateHiddenWindow();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(handle)!;
            var input = new TestInput();
            var released = 0;
            using var tracker = new NativeWindowDragTracker(window, () => released++, input);
            var moves = 0;
            var positions = 0;
            var ups = 0;
            IntPtr Observe(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
            {
                if (message == 0x0200) moves++;
                if (message == 0x0047) positions++;
                if (message == 0x0202) ups++;
                return IntPtr.Zero;
            }
            source.AddHook(Observe);
            try
            {
                SendMessageW(handle, 0x0200, 0, 0);
                Assert.Equal(1, moves); // The hidden HWND really receives messages.
                var start = Bounds(window);
                var startCursor = input.Cursor;
                Assert.True(tracker.Start());
                var pump = Frames(tracker);
                moves = positions = 0;
                var frame = 0;
                var watch = Stopwatch.StartNew();
                for (var packet = 1; packet <= reportHz; packet++)
                {
                    input.Cursor = new Point(startCursor.X + packet % 301, startCursor.Y - packet % 197);
                    SendMessageW(handle, 0x0200, 1, new IntPtr(packet & 0x7fff));
                    if (packet * displayHz / reportHz <= frame) continue;
                    frame = packet * displayHz / reportHz;
                    var time = TimeSpan.FromSeconds((double)frame / displayHz);
                    pump.Present(time);
                    var presented = positions;
                    pump.Present(time); // WPF layout/repeated presentation cannot move twice.
                    Assert.Equal(presented, positions);
                    Assert.Equal(Offset(start, input.Cursor, startCursor), Bounds(window));
                }
                Assert.Equal(0, moves);
                Assert.InRange(positions, 1, displayHz);
                input.Cursor = new Point(startCursor.X - 401, startCursor.Y + 253);
                SendMessageW(handle, 0x0202, 0, 0); // Release between frames commits now.
                Assert.Equal(Offset(start, input.Cursor, startCursor), Bounds(window));
                Assert.Equal(1, released);
                Assert.Equal(1, ups); // WPF still receives the button release.
                Assert.False(tracker.IsActive);
                Assert.Equal(IntPtr.Zero, input.CapturedWindow);
                Assert.Null(FrameField.GetValue(tracker));
                var end = Bounds(window);
                input.Cursor = Point.Empty;
                pump.Present(TimeSpan.FromSeconds(2));
                Assert.Equal(end, Bounds(window));
                SendMessageW(handle, 0x0200, 0, 0);
                Assert.Equal(1, moves); // Normal WPF input resumes immediately.
                output.WriteLine($"{reportHz} packets / {displayHz} Hz budget: {positions} HWND moves, {watch.Elapsed.TotalMilliseconds:F1} ms offscreen batch, no per-packet WPF movement.");
            }
            finally { tracker.Dispose(); source.RemoveHook(Observe); window.Close(); }
        });
    }

    [Theory]
    [InlineData(0x0215)] // Another window takes capture.
    [InlineData(0x001F)] // Windows cancels the gesture.
    public void CancelStopsWithoutJumpingToANewerCursorAndAllowsAnotherDrag(int message)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateHiddenWindow();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var input = new TestInput();
            var released = 0;
            using var tracker = new NativeWindowDragTracker(window, () => released++, input);
            try
            {
                Assert.True(tracker.Start());
                var oldFrames = Frames(tracker);
                var start = Bounds(window);
                input.Cursor = new Point(-400, 650);
                if (message == 0x0215) input.CapturedWindow = new IntPtr(99);
                SendMessageW(handle, message, 0, new IntPtr(99));
                Assert.False(tracker.IsActive);
                Assert.Equal(start, Bounds(window));
                Assert.Equal(1, released);
                if (message == 0x0215) Assert.Equal(new IntPtr(99), input.CapturedWindow);
                Assert.True(tracker.Start());
                input.Cursor = new Point(-390, 670);
                oldFrames.Present(TimeSpan.FromSeconds(1));
                Assert.Equal(start, Bounds(window));
                Frames(tracker).Present(TimeSpan.FromSeconds(1));
                Assert.Equal(new Rectangle(start.X + 10, start.Y + 20, start.Width, start.Height), Bounds(window));
                tracker.Dispose();
                Assert.False(tracker.Start());
            }
            finally { tracker.Dispose(); window.Close(); }
        });
    }

    [Fact]
    public void FrameDetectsAMissedReleaseAndCaptureFailureLeavesNoWorker()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateHiddenWindow();
            _ = new WindowInteropHelper(window).EnsureHandle();
            var input = new TestInput { CanCapture = false };
            var released = 0;
            using var tracker = new NativeWindowDragTracker(window, () => released++, input);
            try
            {
                Assert.False(tracker.Start());
                Assert.Null(FrameField.GetValue(tracker));
                input.CanCapture = true;
                var cursor = input.Cursor;
                var start = Bounds(window);
                Assert.True(tracker.Start());
                input.Cursor = new Point(cursor.X + 31, cursor.Y - 29);
                input.IsLeftButtonDown = false;
                Frames(tracker).Present(TimeSpan.FromSeconds(1));
                Assert.False(tracker.IsActive);
                Assert.Equal(1, released);
                Assert.Equal(Offset(start, input.Cursor, cursor), Bounds(window));
            }
            finally { tracker.Dispose(); window.Close(); }
        });
    }

    [Fact]
    public void FourKhzQueuedMessagesDoNotStarveTheNativeFrameClock()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateHiddenWindow();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var input = new TestInput();
            using var tracker = new NativeWindowDragTracker(window, () => { }, input);
            var cursor = input.Cursor;
            var start = Bounds(window);
            var watch = Stopwatch.StartNew();
            var times = new List<double>();
            var packets = 0;
            var postFailed = false;
            var producerDone = false;
            var loop = new DispatcherFrame();
            var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
            EventHandler moved = (_, _) => times.Add(watch.Elapsed.TotalMilliseconds);
            window.LocationChanged += moved;
            deadline.Tick += (_, _) => loop.Continue = false;
            var producer = new Thread(() =>
            {
                for (var batch = 1; batch <= 250; batch++)
                {
                    var remaining = batch * 4 - watch.Elapsed.TotalMilliseconds;
                    if (remaining > 0) Thread.Sleep(TimeSpan.FromMilliseconds(remaining));
                    input.Cursor = new Point(cursor.X + batch, cursor.Y - batch);
                    for (var packet = 0; packet < 16; packet++)
                    {
                        if (!PostMessageW(handle, 0x0200, 1, 0)) postFailed = true;
                        packets++;
                    }
                }
                Volatile.Write(ref producerDone, true);
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => loop.Continue = false));
            }) { IsBackground = true };
            try
            {
                Assert.True(tracker.Start());
                watch.Restart();
                deadline.Start();
                producer.Start();
                Dispatcher.PushFrame(loop);
                tracker.Stop();
                Assert.True(Volatile.Read(ref producerDone), "The dispatcher did not finish the bounded input workload.");
                Assert.False(postFailed);
                Assert.Equal(4_000, packets);
                Assert.InRange(times.Count, 15, 300);
                Assert.Equal(Offset(start, input.Cursor, cursor), Bounds(window));
                output.WriteLine($"Queued 4000 mouse messages over {watch.Elapsed.TotalMilliseconds:F0} ms: {times.Count} position updates; first={times[0]:F1} ms; max frame gap={times.Zip(times.Skip(1), (a, b) => b - a).Max():F1} ms. Hidden window; no physical mouse injection.");
            }
            finally { deadline.Stop(); tracker.Dispose(); window.LocationChanged -= moved; window.Close(); }
        });
    }

    [Fact]
    public void NativeFramesMoveWithoutWpfRenderingAndWorkersExitAfterEachDrag()
    {
        var workers = new List<Thread>();
        WpfTestHost.Invoke(() =>
        {
            var window = CreateHiddenWindow();
            _ = new WindowInteropHelper(window).EnsureHandle();
            var input = new TestInput();
            using var tracker = new NativeWindowDragTracker(window, () => { }, input);
            try
            {
                for (var gesture = 0; gesture < 3; gesture++)
                {
                    var start = Bounds(window);
                    var cursor = input.Cursor;
                    Assert.True(tracker.Start());
                    var pump = Frames(tracker);
                    var clock = typeof(PointerFramePump).GetField("_nativeClock", PrivateInstance)!.GetValue(pump)!;
                    workers.Add((Thread)typeof(NativePointerFrameClock).GetField("_thread", PrivateInstance)!.GetValue(clock)!);
                    input.Cursor = new Point(cursor.X + 17, cursor.Y + 11);
                    var loop = new DispatcherFrame();
                    var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
                    EventHandler moved = (_, _) => loop.Continue = false;
                    window.LocationChanged += moved;
                    timeout.Tick += (_, _) => loop.Continue = false;
                    timeout.Start();
                    try { Dispatcher.PushFrame(loop); }
                    finally { timeout.Stop(); window.LocationChanged -= moved; }
                    Assert.Equal(Offset(start, input.Cursor, cursor), Bounds(window));
                    tracker.Stop();
                    Assert.Null(FrameField.GetValue(tracker));
                }
            }
            finally { tracker.Dispose(); window.Close(); }
        });
        // Wait only in the test runner, never in the UI/release path.
        foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(2)), "A stopped drag kept its native clock thread alive.");
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo FrameField = typeof(NativeWindowDragTracker).GetField("_frames", PrivateInstance)!;
    private static PointerFramePump Frames(NativeWindowDragTracker tracker) => Assert.IsType<PointerFramePump>(FrameField.GetValue(tracker));
    private static Rectangle Bounds(Window window)
    {
        Assert.True(MonitorGeometryService.TryGetWindowBounds(window, out var bounds));
        return bounds;
    }
    private static Rectangle Offset(Rectangle bounds, Point cursor, Point start) =>
        new(bounds.X + cursor.X - start.X, bounds.Y + cursor.Y - start.Y, bounds.Width, bounds.Height);
    private static Window CreateHiddenWindow() => new()
    {
        Width = 180, Height = 120, Left = -20_000, Top = -20_000,
        WindowStyle = WindowStyle.None, AllowsTransparency = true,
        ShowActivated = false, ShowInTaskbar = false,
    };

    private sealed class TestInput : IWindowDragInput
    {
        private long _cursor = ((long)200 << 32) | unchecked((uint)-1500);
        public Point Cursor
        {
            get { var value = Volatile.Read(ref _cursor); return new Point((int)value, (int)(value >> 32)); }
            set => Volatile.Write(ref _cursor, ((long)value.Y << 32) | (uint)value.X);
        }
        public IntPtr CapturedWindow { get; set; }
        public bool IsLeftButtonDown { get; set; } = true;
        public bool CanCapture { get; set; } = true;
        public bool TryGetCursorPosition(out Point point) { point = Cursor; return true; }
        public bool Capture(IntPtr window) { if (CanCapture) CapturedWindow = window; return CanCapture; }
        public void Release() => CapturedWindow = IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
