using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class PointerDesktopTests(ITestOutputHelper output)
{
    [DesktopFact]
    public void NativeSelectionKeepsRenderingWithTheMaskAndSizeBadgeVisible()
    {
        WpfTestHost.Invoke(() =>
        {
            var host = new System.Windows.Window
            {
                Width = 32, Height = 32, Left = 30, Top = 30,
                WindowStyle = System.Windows.WindowStyle.None,
                AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                ShowActivated = false, ShowInTaskbar = false,
            };
            using var mask = new NativeSelectionMaskWindow();
            using var badge = new NativeSelectionSizeWindow();
            using var border = new NativeSelectionFrameWindow();
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            var loop = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            { Interval = TimeSpan.FromSeconds(2) };
            var times = new List<double>();
            var costs = new List<double>();
            var watch = Stopwatch.StartNew();
            var wpfFrames = 0;
            EventHandler observe = (_, _) => wpfFrames++;
            try
            {
                host.Show();
                border.AttachMaskWindow(mask, desktop);
                border.AttachSizeWindow(badge);
                border.SetMaskEnabled(true);
                border.SetSizeEnabled(true);
                border.StartCustomTracking(_ =>
                {
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    var bounds = new Rectangle(desktop.Left + 80, desktop.Top + 80,
                        300 + (int)(100 * Math.Sin(watch.Elapsed.TotalSeconds * 4)),
                        200 + (int)(80 * Math.Cos(watch.Elapsed.TotalSeconds * 4)));
                    var started = Stopwatch.GetTimestamp();
                    border.Update(bounds);
                    costs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return bounds;
                }, captureMouse: false);
                System.Windows.Media.CompositionTarget.Rendering += observe;
                timer.Tick += (_, _) => loop.Continue = false;
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(loop);
                Assert.True(times.Count > 5, "The compositor stopped delivering selection frames.");
                var intervals = times.Zip(times.Skip(1), (a, b) => b - a).Where(gap => gap > 1).Order().ToArray();
                output.WriteLine($"Live selection: nativeFrames={times.Count}, WPF frames={wpfFrames}, median={intervals[intervals.Length / 2]:F2} ms, p95={intervals[(int)(intervals.Length * .95)]:F2} ms, max={intervals[^1]:F2} ms");
                output.WriteLine($"Frame work: median={costs.Order().ElementAt(costs.Count / 2):F2} ms, p95={costs.Order().ElementAt((int)(costs.Count * .95)):F2} ms");
            }
            finally
            {
                timer.Stop();
                System.Windows.Media.CompositionTarget.Rendering -= observe;
                border.StopTracking();
                host.Close();
            }
        });
    }

    [DesktopFact]
    public void NativeBorderAndMaskCommitTheSameLatestBoundsWithoutAQueuedCatchUp()
    {
        WpfTestHost.Invoke(() =>
        {
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var mask = new NativeSelectionMaskWindow();
            using var frame = new NativeSelectionFrameWindow();
            frame.AttachMaskWindow(mask, desktop);
            frame.SetMaskEnabled(true);
            var requested = new Rectangle(desktop.X + 80, desktop.Y + 80, 100, 100);
            frame.StartCustomTracking(_ => requested, captureMouse: false);
            var pump = (PointerFramePump)typeof(NativeSelectionFrameWindow)
                .GetField("_frames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
            var samples = new List<double>();
            try
            {
                for (var displayFrame = 1; displayFrame <= 120; displayFrame++)
                {
                    // A burst of newer input arrives before each display frame.
                    for (var packet = 0; packet < 34; packet++)
                        requested = displayFrame is >= 60 and < 90
                            ? new Rectangle(desktop.X + 80 + displayFrame % 15,
                                desktop.Y + 80 + displayFrame % 12, 301, 205)
                            : new Rectangle(desktop.X + 80, desktop.Y + 80,
                                100 + (displayFrame * 34 + packet) % Math.Max(200, desktop.Width - 180),
                                100 + (displayFrame * 17 + packet) % Math.Max(100, desktop.Height - 180));
                    var started = Stopwatch.GetTimestamp();
                    pump.Present(TimeSpan.FromSeconds(displayFrame / 120d));
                    samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.True(frame.TryGetLastBounds(out var actual));
                    Assert.Equal(requested, actual);
                    Assert.True(GetWindowRect(frame.Handle, out var native));
                    Assert.Equal(requested.Left, native.Left);
                    Assert.Equal(requested.Top, native.Top);
                    Assert.Equal(requested.Width, native.Right - native.Left);
                    Assert.Equal(Math.Min(3, requested.Height), native.Bottom - native.Top);
                    var border = typeof(NativeSelectionFrameWindow).GetField("_border",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
                    var handles = (IntPtr[])typeof(NativeSelectionBorder).GetField("_windows",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(border)!;
                    var strips = NativeSelectionBorder.GetStrips(requested);
                    for (var i = 0; i < strips.Length; i++)
                    {
                        Assert.True(GetWindowRect(handles[i], out var edge));
                        Assert.Equal(strips[i], Rectangle.FromLTRB(edge.Left, edge.Top, edge.Right, edge.Bottom));
                    }
                    var region = CreateRectRgn(0, 0, 0, 0);
                    try
                    {
                        Assert.NotEqual(0, GetWindowRgn(mask.Handle, region));
                        Assert.False(PtInRegion(region, requested.Left - desktop.X + 10,
                            requested.Top - desktop.Y + 10));
                        Assert.True(PtInRegion(region, 10, 10));
                    }
                    finally { _ = DeleteObject(region); }
                }
                // Releasing between frames commits the last sample immediately.
                requested = new Rectangle(desktop.X + 80, desktop.Y + 80, 301, 205);
                frame.StopTracking(hide: false);
                Assert.True(frame.TryGetLastBounds(out var final));
                Assert.Equal(requested, final);
                Assert.False(pump.IsActive);
                if (Environment.GetEnvironmentVariable("SNAPCUT_POINTER_PREVIEW_PATH") is { Length: > 0 } previewPath)
                {
                    // Optional local verification image; never a production log.
                    var capture = new Rectangle(requested.X - 25, requested.Y - 25,
                        requested.Width + 50, requested.Height + 50);
                    using var bitmap = new Bitmap(capture.Width, capture.Height);
                    using (var graphics = Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(capture.Location, Point.Empty, capture.Size);
                    bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                output.WriteLine($"Native selection: first={samples[0]:F2} ms, median={samples.Order().ElementAt(60):F2} ms, p95={samples.Order().ElementAt(114):F2} ms, max={samples.Max():F2} ms");
            }
            finally { frame.StopTracking(); }
        });
    }

    [DesktopFact]
    public void PinMoveCommitsReleaseBeforeTheNextFrameAndDoesNotWaitForAWorker()
    {
        WpfTestHost.Invoke(() =>
        {
            var originalCursor = System.Windows.Forms.Cursor.Position;
            var window = new System.Windows.Window
            {
                Width = 180, Height = 120, Left = 100, Top = 100,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false,
            };
            NativeWindowDragTracker? tracker = null;
            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(GetWindowRect(handle, out var start));
                System.Windows.Forms.Cursor.Position = new Point(start.Left + 20, start.Top + 20);
                tracker = new NativeWindowDragTracker(window, () => { });
                Assert.True(tracker.Start());
                System.Windows.Forms.Cursor.Position = new Point(start.Left + 67, start.Top + 53);
                tracker.Stop();
                Assert.True(GetWindowRect(handle, out var end));
                Assert.Equal(start.Left + 47, end.Left);
                Assert.Equal(start.Top + 33, end.Top);
                Assert.Equal(start.Right - start.Left, end.Right - end.Left);
                Assert.False(tracker.IsActive);
            }
            finally
            {
                tracker?.Stop();
                window.Close();
                System.Windows.Forms.Cursor.Position = originalCursor;
            }
        });
    }

    [DesktopFact]
    public void RecordingTrailRetainsItsFirstSampleAndStopsWhenDisabled()
    {
        WpfTestHost.Invoke(() =>
        {
            var originalCursor = System.Windows.Forms.Cursor.Position;
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            var overlay = new RecordingInputOverlayWindow(new ScreenRegion(
                desktop.X, desktop.Y, desktop.Width, desktop.Height));
            try
            {
                overlay.Show();
                overlay.SetMouseTrailEnabled(true);
                var frames = (PointerFramePump)typeof(RecordingInputOverlayWindow)
                    .GetField("_pointerFrames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
                var canvas = (System.Windows.Controls.Canvas)overlay.FindName("MouseTrailCanvas");
                System.Windows.Forms.Cursor.Position = new Point(desktop.X + 100, desktop.Y + 100);
                frames.Present(TimeSpan.FromMilliseconds(10));
                System.Windows.Forms.Cursor.Position = new Point(desktop.X + 130, desktop.Y + 120);
                frames.Present(TimeSpan.FromMilliseconds(20));
                Assert.Single(canvas.Children.Cast<System.Windows.UIElement>());
                overlay.SetPaused(true);
                Assert.False(frames.IsActive);
                Assert.Empty(canvas.Children.Cast<System.Windows.UIElement>());
                overlay.SetPaused(false);
                Assert.True(frames.IsActive);
                overlay.SetMouseTrailEnabled(false);
                Assert.False(frames.IsActive);
            }
            finally
            {
                overlay.Close();
                System.Windows.Forms.Cursor.Position = originalCursor;
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
