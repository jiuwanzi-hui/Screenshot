using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ResidentSelectionTests
{
    [Fact]
    public void NativeFrameRemainsPaintableAfterAcquisitionThreadExits()
    {
        NativeDesktopFrame? acquired = null;
        var thread = new Thread(() =>
        {
            acquired = new NativeDesktopFrame(new(0, 0, 7, 5));
            Marshal.WriteInt32(acquired.Pixels, 0x123456);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
        using var source = acquired!;
        using var target = new NativeDesktopFrame(new(0, 0, 7, 5));
        Assert.True(NativeDesktopFrame.BitBlt(target.DeviceContext, 0, 0, 7, 5, source.DeviceContext, 0, 0, 0x00CC0020));
        _ = GdiFlush();
        Assert.Equal(0x123456u, target.PixelAt(0, 0));
    }

    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [Theory]
    [InlineData(-200, -100, 500, 400)]
    [InlineData(500, 400, -200, -100)]
    [InlineData(-200, 400, 500, -100)]
    [InlineData(500, -100, -200, 400)]
    public void SelectionUsesPhysicalCoordinatesAcrossNegativeScreenOrigins(int x1, int y1, int x2, int y2)
    {
        var result = ResidentSelectionWindow.GetSelection(new(x1, y1), new(x2, y2),
            new(-1920, -1080, 4480, 2520), CaptureAspectRatio.Free);
        Assert.Equal(new ScreenRegion(-200, -100, 700, 500), result);
    }

    [Fact]
    public void DragOutsideDesktopIsClippedAndAspectRatioIsPreserved()
    {
        var desktop = new ScreenRegion(-1920, 0, 4480, 1440);
        var free = ResidentSelectionWindow.GetSelection(new(-2000, -100), new(3000, 2000), desktop, CaptureAspectRatio.Free);
        Assert.Equal(desktop, free);
        var square = ResidentSelectionWindow.GetSelection(new(100, 100), new(-2100, 1800), desktop, CaptureAspectRatio.Ratio1x1);
        Assert.Equal(square.Width, square.Height);
        Assert.Equal(square, ScreenRegion.Intersect(square, desktop));
    }

    [Fact]
    public async Task NativeDibTransferPreservesOpaqueRgbAndSelectionWithoutChangingSource()
    {
        using var frame = new NativeDesktopFrame(new(-18, 21, 13, 7));
        for (var y = 0; y < frame.Region.Height; y++)
            for (var x = 0; x < frame.Region.Width; x++)
                Marshal.WriteInt32(frame.Pixels, y * frame.Stride + x * 4,
                    (y % 2 == 0 ? 0 : unchecked((int)0x80000000)) | (x * 13 << 16) | (y * 23 << 8) | (x + y));
        using var stream = new MemoryStream();
        using var pipe = new FeatureProcessConnection(stream);
        var selection = new ScreenRegion(-17, 22, 8, 5);
        await pipe.SendAsync(new("command") { NativeFrame = frame, InitialSelection = selection, NativeSelectionId = 17 });
        stream.Position = 0;
        var result = await pipe.ReadAsync();
        using var image = result.Image!;
        Assert.Equal(frame.Region, image.SourceRegion);
        Assert.Equal(selection, result.InitialSelection);
        Assert.Equal(17, result.NativeSelectionId);
        for (var y = 0; y < frame.Region.Height; y++)
            for (var x = 0; x < frame.Region.Width; x++)
            {
                var expected = Color.FromArgb(255, x * 13, y * 23, x + y);
                Assert.Equal(expected.ToArgb(), image.Bitmap.GetPixel(x, y).ToArgb());
                Assert.Equal((uint)(expected.ToArgb() & 0xFFFFFF), frame.PixelAt(x - 18, y + 21));
            }
        frame.Dispose();
        frame.Dispose();
        Assert.Equal(IntPtr.Zero, frame.Pixels);
        Assert.Equal(IntPtr.Zero, frame.DeviceContext);
        Assert.Throws<ObjectDisposedException>(() => frame.PixelAt(0, 0));
    }

    [Fact]
    public async Task PreCancelledCaptureDoesNotOccupyTheCaptureGate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NativeDesktopFrame.CaptureAsync(new(0, 0, 10, 10), cancellation.Token));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(65537, 10)]
    [InlineData(65536, 65536)]
    public void InvalidDimensionsAreRejectedBeforeNativeAllocation(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeDesktopFrame(new(0, 0, width, height)));
}
