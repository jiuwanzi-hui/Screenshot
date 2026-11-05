using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using WpfPoint = System.Windows.Point;

namespace Screenshot.App.Tests;

public sealed class PointerPresentationTests
{
    [Fact]
    public void NativeFrameRequestsCoalesceAndNeverReplayAStoppedGesture()
    {
        WpfTestHost.Invoke(() =>
        {
            var frames = 0;
            using var clock = new NativePointerFrameClock(() => frames++, () => { });
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var active = typeof(NativePointerFrameClock).GetField("_activeGeneration", flags)!;
            var queue = typeof(NativePointerFrameClock).GetMethod("QueueFrame", flags)!;
            void Drain() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            active.SetValue(clock, 1);
            for (var report = 0; report < 2_000; report++) queue.Invoke(clock, [1]);
            Drain();
            Assert.Equal(1, frames);
            queue.Invoke(clock, [1]);
            // A delayed request belongs to the previous gesture, even if the
            // next gesture has started before the dispatcher can deliver it.
            active.SetValue(clock, 2);
            Drain();
            Assert.Equal(1, frames);
            queue.Invoke(clock, [2]);
            Drain();
            Assert.Equal(2, frames);
            queue.Invoke(clock, [2]);
            clock.Stop();
            Drain();
            Assert.Equal(2, frames);
        });
    }

    [Fact]
    public void NativeClockCanStopAndRestartWithoutBlockingTheDispatcher()
    {
        WpfTestHost.Invoke(() =>
        {
            var frames = 0;
            var ownerThread = Environment.CurrentManagedThreadId;
            using var clock = new NativePointerFrameClock(() =>
            {
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
                frames++;
            }, () => { });
            var dispatcherLoop = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            { Interval = TimeSpan.FromMilliseconds(120) };
            var ticks = 0;
            var priorFrames = 0;
            var minimumFramesPerGesture = int.MaxValue;
            timer.Tick += (_, _) =>
            {
                minimumFramesPerGesture = Math.Min(minimumFramesPerGesture, frames - priorFrames);
                priorFrames = frames;
                clock.Stop();
                if (++ticks == 4) dispatcherLoop.Continue = false;
                else clock.Start();
            };
            try
            {
                clock.Start();
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(dispatcherLoop);
                Assert.True(minimumFramesPerGesture > 0, "A quick stop/start lost its wake-up.");
            }
            finally { timer.Stop(); clock.Stop(); }
        });
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void TwoThousandReportsPerSecondPresentLatestSampleWithoutAQueue(int displayHz)
    {
        WpfTestHost.Invoke(() =>
        {
            var cursor = 0;
            var presented = 0;
            var frames = 0;
            using var pump = new PointerFramePump(() => { presented = cursor; frames++; });
            pump.Start();
            for (var packet = 1; packet <= 2_000; packet++)
            {
                cursor = packet;
                pump.Start(); // Repeated input must not add render subscriptions.
                var time = TimeSpan.FromSeconds((double)(packet * displayHz / 2_000) / displayHz);
                pump.Present(time);
                var beforeLayout = frames;
                pump.Present(time);
                Assert.Equal(beforeLayout, frames);
                if (packet * displayHz % 2_000 == 0) Assert.Equal(cursor, presented);
            }
            Assert.InRange(frames, displayHz, displayHz + 1);
            Assert.Equal(2_000, presented);
            pump.Stop();
            cursor = 9_999;
            pump.Present(TimeSpan.FromSeconds(2));
            Assert.Equal(2_000, presented);
            pump.Start();
            pump.Present(TimeSpan.FromSeconds(2));
            Assert.Equal(cursor, presented);
        });
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 7)]
    [InlineData(7, 2)]
    [InlineData(12, 13)]
    [InlineData(300, 200)]
    public void ThinBorderCoversEveryEdgeAndLeavesTheInteriorEmpty(int width, int height)
    {
        var selection = new Rectangle(-400, -200, width, height);
        var strips = NativeSelectionBorder.GetStrips(selection);
        foreach (var strip in strips.Where(r => r.Width > 0 && r.Height > 0))
            Assert.True(selection.Contains(strip));
        for (var y = selection.Top; y < selection.Bottom; y++)
        for (var x = selection.Left; x < selection.Right; x++)
        {
            var isEdge = x - selection.Left < 3 || selection.Right - x <= 3 ||
                         y - selection.Top < 3 || selection.Bottom - y <= 3;
            Assert.Equal(isEdge, strips.Any(r => r.Contains(x, y)));
        }
    }

    [Fact]
    public void FourKBorderBackingPixelsScaleWithPerimeter()
    {
        var strips = NativeSelectionBorder.GetStrips(new Rectangle(0, 0, 3840, 2160));
        Assert.InRange(strips.Sum(r => (long)r.Width * r.Height * 4), 1, 150_000);
    }

    [Fact]
    public void BorderResizeReusesNativeAndManagedStorageAfterWarmingThePerimeter()
    {
        using var bitmap = new NativeBorderBitmap();
        var start = System.Drawing.Color.FromArgb(30, 100, 200);
        var end = System.Drawing.Color.FromArgb(90, 180, 40);
        bitmap.Paint(3840, 3, 0, 3840, start, end);
        var dc = bitmap.DeviceContext;
        var pixelsField = typeof(NativeBorderBitmap).GetField("_pixels", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pixels = pixelsField.GetValue(bitmap);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 480; frame++)
            bitmap.Paint(300 + frame * 7 % 3500, 3, 0, 3840, start, end);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(dc, bitmap.DeviceContext);
        Assert.Same(pixels, pixelsField.GetValue(bitmap));
        Assert.InRange(allocated, 0, 4096);
        bitmap.Dispose();
        Assert.Equal(IntPtr.Zero, bitmap.DeviceContext);
    }

    [Theory]
    [InlineData(100, 3, 0, 100)]
    [InlineData(3, 140, 97, 100)]
    [InlineData(1, 1, 0, 1)]
    public void BorderDibPreservesOpaqueGradientPixelsAcrossResizes(int width, int height, int offset, int totalWidth)
    {
        using var bitmap = new NativeBorderBitmap();
        bitmap.Paint(400, 3, 0, 400, System.Drawing.Color.Black, System.Drawing.Color.White);
        bitmap.Paint(width, height, offset, totalWidth, System.Drawing.Color.Black, System.Drawing.Color.White);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bits = (IntPtr)typeof(NativeBorderBitmap).GetField("_bits", flags)!.GetValue(bitmap)!;
        var stride = (int)typeof(NativeBorderBitmap).GetField("_width", flags)!.GetValue(bitmap)!;
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var color = System.Drawing.Color.FromArgb(Marshal.ReadInt32(bits, (row * stride + column) * 4));
            Assert.Equal(255, color.A);
            Assert.Equal(color.R, color.G);
            Assert.Equal(color.G, color.B);
            Assert.Equal(255 * (offset + column) / Math.Max(1, totalWidth - 1), color.R);
        }
    }

    [Theory]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.CurvedArrow)]
    [InlineData(EditorTool.Mosaic)]
    public void CoalescedStrokePreservesTurnsAndReleaseEndpoint(EditorTool tool)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new ImageEditorCanvas();
            editor.InitializeAnnotationOverlay(400, 300, 400, 300);
            editor.SelectTool(tool);
            try
            {
                Field("_isDrawing").SetValue(editor, true);
                Field("_drawingStartPoint").SetValue(editor, new WpfPoint(10, 10));
                Method("CreateDrawingPreview").Invoke(editor, [new WpfPoint(10, 10)]);
                var corners = new[] { new WpfPoint(90, 10), new WpfPoint(90, 90), new WpfPoint(10, 90) };
                foreach (var point in corners) Method("QueuePointerUpdate").Invoke(editor, [point]);
                Method("OnPointerRendering").Invoke(editor, [null, EventArgs.Empty]);
                var endpoint = new WpfPoint(20, 30);
                Method("FlushPointerUpdate").Invoke(editor, [endpoint]);
                var points = Assert.IsType<List<WpfPoint>>(Field("_brushPoints").GetValue(editor));
                foreach (var corner in corners) Assert.Contains(corner, points);
                Assert.Equal(endpoint, points[^1]);
                if (tool != EditorTool.CurvedArrow)
                {
                    var preview = Assert.IsType<System.Windows.Shapes.Polyline>(Field("_drawingPreview").GetValue(editor));
                    Assert.Equal(points, preview.Points.ToList());
                }
                Assert.False((bool)Field("_isPointerRenderingSubscribed").GetValue(editor)!);
            }
            finally { editor.Reset(); }
        });
    }

    private static FieldInfo Field(string name) => typeof(ImageEditorCanvas).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(ImageEditorCanvas).GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;
}
