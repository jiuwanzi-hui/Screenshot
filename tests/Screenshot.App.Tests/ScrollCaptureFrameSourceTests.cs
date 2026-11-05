using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureFrameSourceTests
{
    [Fact]
    public async Task CropsTheSameWindowSurfaceIncludingOccludedEdgesAndReleasesIt()
    {
        using var page = new Bitmap(240, 140, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(page)) { g.Clear(Color.White); g.FillRectangle(Brushes.DarkSlateGray, 180, 40, 40, 60); }
        var fake = new FakeSource(() => new(new CapturedImage(new Bitmap(page)), new(-240, 80, 240, 140)));
        await using (var source = new ScrollCaptureFrameSource(() => fake, CancellationToken.None))
        {
            var target = new ScrollCaptureTarget(new(1), new(1), new(-120, 110, 110, 100), false)
                { FrameSource = source, PreviewRegionProvider = () => new(-60, 80, 60, 140) };
            using var actual = ForegroundWindowCaptureService.CaptureScrollTargetRegion(target);
            using var expected = page.Clone(new Rectangle(120, 30, 110, 100), PixelFormat.Format32bppPArgb);
            ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, actual);
            // No fresh frame is a stationary surface, not a reason to recapture
            // the visible preview or use a possibly black window DC.
            fake.Next = () => null;
            using var stationary = source.Capture(target);
            ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, stationary);
        }
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task CancellationStopsFirstFrameWaitingAndDisposesCapture()
    {
        var fake = new FakeSource(() => null);
        using var cancellation = new CancellationTokenSource(80);
        await using (var source = new ScrollCaptureFrameSource(() => fake, cancellation.Token))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                source.Capture(new(new(1), new(1), new(0, 0, 240, 140), false))));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task SelectionOutsideWindowKeepsFixedDesktopEdgesWithoutFreezingLivePixels()
    {
        using var backdrop = new Bitmap(240, 140, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(backdrop)) graphics.Clear(Color.LightGray);
        using var liveWindow = new Bitmap(200, 100, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(liveWindow)) graphics.Clear(Color.Brown);
        var fake = new FakeSource(() => new(new CapturedImage(new Bitmap(liveWindow)), new(20, 20, 200, 100)));
        await using var source = new ScrollCaptureFrameSource(() => fake, CancellationToken.None, _ =>
        {
            var desktop = new Bitmap(backdrop);
            using var graphics = Graphics.FromImage(desktop);
            graphics.FillRectangle(Brushes.Black, 235, 0, 5, 140);
            graphics.FillRectangle(Brushes.Black, 180, 30, 40, 80);
            return desktop;
        });
        var target = new ScrollCaptureTarget(new(1), new(1), new(0, 0, 240, 140), false)
        {
            PreviewRegionProvider = () => new(180, 30, 40, 80),
            BorderRegions = [new(235, 0, 5, 140), new(20, 20, 5, 100)],
            BorderBackdrop = (backdrop, new(0, 0, 240, 140)),
        };
        using var result = source.Capture(target);
        using var expected = new Bitmap(backdrop);
        using (var graphics = Graphics.FromImage(expected)) graphics.DrawImageUnscaled(liveWindow, 20, 20);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, result);
    }

    [DesktopFact]
    public async Task CompositedWindowRemainsCompleteBehindVisiblePreviewAndBorder()
    {
        Window? window = null;
        ScrollCaptureProgressWindow? preview = null;
        NativeSelectionFrameWindow? border = null;
        TextBlock? text = null;
        nint handle = 0;
        ScreenRegion region = default;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new Window { Width = 540, Height = 320, Left = 90, Top = 90,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false, Title = "SnapCut capture verification" };
                text = new TextBlock { Text = "Long screenshot verification\nOriginal window content", Margin = new Thickness(16),
                    FontSize = 16, Foreground = System.Windows.Media.Brushes.Black };
                window.Content = text;
                window.Show(); window.UpdateLayout();
                handle = new WindowInteropHelper(window).Handle;
                Assert.True(ForegroundWindowCaptureService.TryGetClientScreenRegion(handle, out region));
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var source = new ScrollCaptureFrameSource(handle, timeout.Token);
            var target = new ScrollCaptureTarget(handle, handle, region, false) { FrameSource = source };
            using var expected = await Task.Run(() => source.Capture(target));
            WpfTestHost.Invoke(() =>
            {
                preview = new ScrollCaptureProgressWindow();
                preview.ConfigureForCaptureRegion(region);
                preview.Left = window!.Left + 330; preview.Top = window.Top + 20;
                preview.Show(); preview.UpdateLayout();
                border = new NativeSelectionFrameWindow();
                border.Update(new Rectangle(region.X, region.Y, region.Width, region.Height));
                preview.BringToFront();
            });
            var previewBounds = preview!.GetScreenRegion();
            target = target with { PreviewRegionProvider = preview.GetScreenRegion };
            for (var i = 0; i < 3; i++)
            {
                using var captured = await Task.Run(() => source.Capture(target));
                ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, captured);
                WpfTestHost.Invoke(() => { Assert.True(preview.IsVisible); Assert.Equal(previewBounds, preview.GetScreenRegion()); });
            }
            WpfTestHost.Invoke(() => { text!.Text = "Updated window content\nStill visible under the preview"; window!.UpdateLayout(); });
            var changed = false;
            for (var attempt = 0; attempt < 20 && !changed; attempt++)
            {
                using var captured = await Task.Run(() => source.Capture(target));
                using var oldSample = new ManualScrollFrame(expected);
                using var newSample = new ManualScrollFrame(captured);
                changed = !oldSample.IsExactOverlap(newSample, 0, 0, region.Height);
                Assert.Equal(Color.White.ToArgb(), captured.GetPixel(region.Width - 25, region.Height / 2).ToArgb());
                if (!changed) await Task.Delay(20, timeout.Token);
            }
            Assert.True(changed, "Window capture did not deliver updated content.");
        }
        finally
        {
            WpfTestHost.Invoke(() => { border?.Dispose(); preview?.CloseFromCoordinator(); window?.Close(); });
        }
    }

    private sealed class FakeSource(Func<RealtimeFrame?> next) : IRealtimeCaptureSource
    {
        internal Func<RealtimeFrame?> Next = next;
        internal bool Disposed;
        public Task<RealtimeFrame?> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Next()); }
        public void Dispose() => Disposed = true;
    }
}
