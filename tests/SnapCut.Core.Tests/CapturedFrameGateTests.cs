using SnapCut.Core;
using static SnapCut.Core.Tests.TestImages;

namespace SnapCut.Core.Tests;

public sealed class CapturedFrameGateTests
{
    [Fact]
    public void AcceptsTheFirstFrameAndRejectsAnIdenticalRepeat()
    {
        var gate = new CapturedFrameGate();
        var frame = CreateTexturedFrame(160, 120, seed: 1);

        Assert.True(gate.HasChanged(frame));
        gate.AcceptPending();

        Assert.False(gate.HasChanged(frame));
    }

    [Fact]
    public void DetectsAScrolledFrame()
    {
        var gate = new CapturedFrameGate();
        var content = CreateTexturedFrame(160, 200, seed: 1);
        var first = content.CropRows(0, 120);
        var scrolled = content.CropRows(40, 120);

        Assert.True(gate.HasChanged(first));
        gate.AcceptPending();

        Assert.True(gate.HasChanged(scrolled));
    }

    [Fact]
    public void IgnoresAFrameThatWasNeverAccepted()
    {
        var gate = new CapturedFrameGate();
        var first = CreateTexturedFrame(160, 120, seed: 1);
        var second = CreateTexturedFrame(160, 120, seed: 2);

        Assert.True(gate.HasChanged(first));
        // Not accepted: the gate still compares against nothing.
        Assert.True(gate.HasChanged(second));
        gate.Accept(second);

        Assert.False(gate.HasChanged(second));
    }

    private static PixelImage CreateTexturedFrame(int width, int height, int seed)
    {
        var frame = new PixelImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetPixel(
                    frame,
                    x,
                    y,
                    Rgb(
                        (byte)((x * 19 + y * 31 + seed * 71) & 0xff),
                        (byte)((x * 43 + y * 17 + seed * 13) & 0xff),
                        (byte)((x * 7 + y * 29 + seed * 47) & 0xff)));
            }
        }

        return frame;
    }
}
