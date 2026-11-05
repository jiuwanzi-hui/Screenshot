using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class FeaturePixelTransferTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedReadsPreserveEveryPixelAndTheFollowingMessage(bool native)
    {
        const int width = 1025, height = 139;
        using var stream = new FragmentedStream();
        using var connection = new FeatureProcessConnection(stream);
        using var frame = new NativeDesktopFrame(new ScreenRegion(-1200, 50, width, height));
        var pixels = new byte[frame.Stride * height];
        new Random(73).NextBytes(pixels);
        // Native desktop alpha is intentionally undefined; the receiver must
        // display it as opaque while ordinary images preserve their alpha.
        Marshal.Copy(pixels, 0, frame.Pixels, pixels.Length);
        using var bitmap = new Bitmap(width, height, frame.Stride,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb, frame.Pixels);
        using var image = new CapturedImage((Bitmap)bitmap.Clone(), frame.Region);
        await connection.SendAsync(new("prepare-capture-frame") { NativeSelectionId = 7,
            NativeFrame = native ? frame : null, Image = native ? null : image });
        await connection.SendAsync(new("after-image") { Sequence = 17 });
        stream.Position = 0;
        var received = await connection.ReadAsync();
        using var restored = received.Image!;
        Assert.Equal(7, received.NativeSelectionId);
        var preview = typeof(CapturedImage).GetField("_preview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(restored);
        Assert.NotNull(preview);
        Assert.Same(preview, restored.Preview);
        Assert.True(restored.Preview.IsFrozen);
        Assert.Equal(frame.Region, restored.SourceRegion);
        var data = restored.Bitmap.LockBits(new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, restored.Bitmap.PixelFormat);
        try
        {
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + data.Stride * y, row, 0, row.Length);
                Assert.Equal(pixels.AsSpan(y * row.Length, row.Length).ToArray(), row);
            }
        }
        finally { restored.Bitmap.UnlockBits(data); }
        Assert.Equal(native ? 255 : pixels[3], restored.Bitmap.GetPixel(0, 0).A);
        var next = await connection.ReadAsync();
        Assert.Equal("after-image", next.Kind);
        Assert.Equal(17, next.Sequence);
    }

    [Fact]
    public async Task FullResolutionFrameTravelsOverActualPipeWithoutCompression()
    {
        var name = "SnapCut.PixelTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        using var sender = new FeatureProcessConnection(server);
        using var receiver = new FeatureProcessConnection(client);
        using var frame = new NativeDesktopFrame(new ScreenRegion(0, 0, 3840, 2160));
        Marshal.WriteInt32(frame.Pixels, 0x00112233);
        Marshal.WriteInt32(frame.Pixels, (2160 * frame.Stride) - 4, 0x00445566);
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            var read = receiver.ReadAsync();
            await sender.SendAsync(new("command") { NativeFrame = frame }).WaitAsync(TimeSpan.FromSeconds(10));
            var message = await read.WaitAsync(TimeSpan.FromSeconds(10));
            using var image = message.Image!;
            Assert.Equal(Color.FromArgb(255, 0x11, 0x22, 0x33), image.Bitmap.GetPixel(0, 0));
            Assert.Equal(Color.FromArgb(255, 0x44, 0x55, 0x66), image.Bitmap.GetPixel(3839, 2159));
            output.WriteLine($"4K BGRA pipe transfer {i + 1}: {watch.Elapsed.TotalMilliseconds:F1} ms.");
        }
    }

    private sealed class FragmentedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(617, buffer.Length)], token);
    }
}
