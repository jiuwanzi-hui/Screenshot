using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Screenshot.App.Tests;

public sealed class CaptureRecoveryTests
{
    [Fact]
    public async Task DesktopChangesCoalesceIntoOneCompositionFlushBeforeCaptureOnTheWorker()
    {
        var flushes = 0;
        var source = new CaptureStartupFrameSource(() =>
        {
            WpfTestHost.Invoke(() => { }); // The caller must not block the dispatcher.
            Interlocked.Increment(ref flushes);
        });
        Task<CapturedImage>? capture = null;
        WpfTestHost.Invoke(() =>
        {
            source.RequestCompositionRefresh();
            source.RequestCompositionRefresh();
            Assert.Equal(0, flushes);
            capture = source.CaptureAsync(_ =>
            {
                Assert.Equal(1, flushes);
                return new CapturedImage(new Bitmap(8, 8));
            }, CancellationToken.None);
        });
        using var first = await capture!.WaitAsync(TimeSpan.FromSeconds(5));
        using var second = await source.CaptureAsync(_ => new CapturedImage(new Bitmap(8, 8)), CancellationToken.None);
        Assert.Equal(1, flushes);
        source.RequestCompositionRefresh();
        using var third = await source.CaptureAsync(_ => new CapturedImage(new Bitmap(8, 8)), CancellationToken.None);
        Assert.Equal(2, flushes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(255)]
    public void DesktopReservedBytesCannotMakePreviewOrExportTransparent(byte reserved)
    {
        using var bitmap = ScreenCaptureService.CreateDesktopBitmap(new ScreenRegion(-7, -4, 3, 2));
        var data = bitmap.LockBits(new Rectangle(0, 0, 3, 2), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (var y = 0; y < 2; y++)
                Marshal.Copy(new byte[] { 0, 0, 0, reserved, 30, 60, 90, reserved, 255, 255, 255, reserved },
                    0, IntPtr.Add(data.Scan0, y * data.Stride), 12);
        }
        finally { bitmap.UnlockBits(data); }
        using var frame = new CapturedImage(bitmap);
        var preview = frame.WarmPreview();
        Assert.True(preview.IsFrozen);
        // Bgr32 ignores the undefined byte without a second full-screen conversion.
        Assert.Equal(PixelFormats.Bgr32, preview.Format);
        var rgba = new FormatConvertedBitmap(preview, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[24];
        rgba.CopyPixels(pixels, 12, 0);
        for (var i = 3; i < pixels.Length; i += 4) Assert.Equal(255, pixels[i]);
        Assert.Equal(new byte[] { 30, 60, 90, 255 }, pixels[4..8]);

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(preview));
        encoder.Save(stream);
        stream.Position = 0;
        using var exported = new Bitmap(stream);
        Assert.Equal(System.Drawing.Color.FromArgb(90, 60, 30).ToArgb(), exported.GetPixel(1, 1).ToArgb());
        Assert.Equal(255, exported.GetPixel(0, 0).A); // black is valid opaque content
    }

    [Fact]
    public void ImportedImagesStillPreserveTheirIntentionalTransparency()
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, System.Drawing.Color.FromArgb(64, 100, 120, 140));
        var pixels = new byte[4];
        CapturedImage.ToBitmapSource(bitmap).CopyPixels(pixels, 4, 0);
        Assert.Equal(new byte[] { 140, 120, 100, 64 }, pixels);
    }

    [Fact]
    public async Task DelayedPresentationWaitsForHitTestingWithoutPrematureFallback()
    {
        var probes = 0;
        Assert.True(await CaptureInputReadiness.WaitAsync(() => ++probes >= 3, () => true,
            () => Assert.Fail("A healthy slow frame must not switch rendering."), CancellationToken.None));
        Assert.Equal(3, probes);
    }

    [Fact]
    public async Task MissingLayeredSurfaceRecoversOnceThenVerifiesAgain()
    {
        var recovered = 0;
        Assert.True(await CaptureInputReadiness.WaitAsync(() => recovered == 1, () => true,
            () => recovered++, CancellationToken.None, recoveryDelay: TimeSpan.Zero));
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task UnrecoverableSurfaceStopsWithinBudgetWithoutRepeatedRendering()
    {
        var recovered = 0;
        var probes = 0;
        Assert.False(await CaptureInputReadiness.WaitAsync(() => { probes++; return false; }, () => true,
            () => recovered++, CancellationToken.None, TimeSpan.FromMilliseconds(100), TimeSpan.Zero));
        Assert.Equal(1, recovered);
        Assert.InRange(probes, 2, 6); // wait-based, never a busy rendering loop
    }

    [Fact]
    public async Task LosingForegroundCannotCommitMaskOrStealItBack()
    {
        var foregroundChecks = 0;
        Assert.False(await CaptureInputReadiness.WaitAsync(() => false, () => ++foregroundChecks == 1,
            () => Assert.Fail("Lost foreground must not trigger rendering retries."), CancellationToken.None));
        Assert.Equal(2, foregroundChecks);
    }

    [Fact]
    public async Task EscapeCancelsReadinessWait()
    {
        using var cancellation = new CancellationTokenSource();
        var task = CaptureInputReadiness.WaitAsync(() => false, () => true,
            () => Assert.Fail("Cancellation must not start fallback."), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task VisibleInputSurfaceDoesNotRequireForegroundPermission()
    {
        Assert.True(await CaptureInputReadiness.WaitAsync(() => true, () => false,
            () => Assert.Fail("A visible surface must not change its renderer."), CancellationToken.None));
    }

    [Fact]
    public async Task TopmostDialogIsReorderedBeforeTryingSoftwareRendering()
    {
        var raises = 0;
        Assert.True(await CaptureInputReadiness.WaitAsync(() => raises == 1, () => true,
            () => Assert.Fail("Occlusion is not a render failure."), CancellationToken.None,
            recoverInput: () => raises++));
        Assert.Equal(1, raises);
    }

    [Fact]
    public async Task UncoveredSurfaceCanProceedWhenFocusRemainsWithDialog()
    {
        var raises = 0;
        Assert.True(await CaptureInputReadiness.WaitAsync(() => raises == 1, () => false,
            () => Assert.Fail("Focus denial must not force software rendering."), CancellationToken.None,
            recoverInput: () => raises++));
        Assert.Equal(1, raises);
    }

    [Fact]
    public async Task TimedOutNativeCopyDoesNotMultiplyWorkersAndDisposesLateFrame()
    {
        var source = new CaptureStartupFrameSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var frame = new CapturedImage(new Bitmap(8, 8));
        var first = source.CaptureAsync(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
            return frame;
        }, CancellationToken.None, TimeSpan.FromMilliseconds(80));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.CaptureAsync(_ =>
            {
                Assert.Fail("The previous driver still owns the capture slot.");
                return frame;
            }, CancellationToken.None));
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try { _ = frame.Preview; return false; }
            catch (ObjectDisposedException) { return true; }
        }, TimeSpan.FromSeconds(3)));
        using var next = await source.CaptureAsync(_ => new CapturedImage(new Bitmap(8, 8)), CancellationToken.None);
        Assert.Equal(8, next.Preview.PixelWidth);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForNativeCopyToReturn()
    {
        var source = new CaptureStartupFrameSource();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = source.CaptureAsync(token =>
        {
            try
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException();
            }
            finally { ended.TrySetResult(); }
        }, cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(ended.Task.IsCompleted);
        }
        finally { release.Set(); }
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
