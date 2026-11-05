using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressDimensionsWaitForPreviewBitmapAccess(bool readWidth)
    {
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using var frame = new Bitmap(64, 96, PixelFormat.Format32bppPArgb);
        Assert.True(composer.TryAddFrame(frame, ScrollCaptureOptions.Default, out _));
        var sync = typeof(ScrollCaptureComposer).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(composer)!;
        var segments = (LinkedList<Bitmap>)typeof(ScrollCaptureComposer)
            .GetField("_segments", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(composer)!;
        using var started = new ManualResetEventSlim();
        Task<int> read;
        lock (sync)
        {
            var segment = segments.First!.Value;
            var pixels = segment.LockBits(new Rectangle(0, 0, 64, 96), ImageLockMode.ReadOnly, segment.PixelFormat);
            try
            {
                read = Task.Run(() =>
                {
                    started.Set();
                    return readWidth ? composer.OutputWidth : composer.OutputHeight;
                });
                Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
                // Hold the same gate used by preview rendering. GDI+ also
                // rejects dimension reads while its pixels are in use.
                Assert.False(SpinWait.SpinUntil(() => read.IsCompleted, TimeSpan.FromMilliseconds(100)));
            }
            finally { segment.UnlockBits(pixels); }
        }
        Assert.Equal(readWidth ? 64 : 96, await read.WaitAsync(TimeSpan.FromSeconds(3)));
        using var result = composer.Compose();
        Assert.Equal(frame.Size, result.Size);
    }
}
