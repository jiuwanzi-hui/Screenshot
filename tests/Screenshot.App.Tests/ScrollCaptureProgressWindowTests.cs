using System.Reflection;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureProgressWindowTests
{
    [Fact]
    public void BringingPreviewToFrontDoesNotMoveItToTheOrigin()
    {
        var flagsField = typeof(ScrollCaptureProgressWindow).GetField(
            "BringToFrontFlags",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(flagsField);
        var flags = Assert.IsType<uint>(flagsField.GetRawConstantValue());

        const uint doNotMove = 0x0002;
        Assert.Equal(doNotMove, flags & doNotMove);
    }

    [Fact]
    public void NarrowsPreviewToRemainImmediatelyRightOfSelection()
    {
        var captureRegion = new ScreenRegion(628, 457, 1117, 296);
        var monitorBounds = new ScreenRegion(0, 0, 1965, 1080);
        var width = GetPreviewPhysicalWidth(captureRegion, monitorBounds);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height: 296);

        Assert.Equal(208, width);
        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }


    [Fact]
    public void UsesLeftSideWhenRightSideCannotFitTheActionButtons()
    {
        var captureRegion = new ScreenRegion(500, 160, 1250, 500);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1040);
        var width = GetPreviewPhysicalWidth(captureRegion, monitorBounds);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width,
            height: 500);

        Assert.Equal(300, width);
        Assert.Equal(captureRegion.X - width - 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }

    [Fact]
    public void PositionsPreviewImmediatelyRightWhenSpaceIsAvailable()
    {
        var captureRegion = new ScreenRegion(120, 160, 800, 500);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1040);

        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width: 300,
            height: 500);

        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }


    [Fact]
    public void PrefersRightSideWhenGapIsBarelyEnoughForMinWidth()
    {
        // Near-fit right gap must stay glued to the selection right edge, not
        // fall back to the far monitor edge or left side when right is usable.
        var captureRegion = new ScreenRegion(100, 100, 1600, 400);
        var monitorBounds = new ScreenRegion(0, 0, 1920, 1080);
        // rightX = 100+1600+12 = 1712; rightSpace = 1920-1712 = 208 (>= 200 min)
        var previewBounds = ChoosePreviewBounds(
            captureRegion,
            monitorBounds,
            width: 300,
            height: 400);

        Assert.Equal(captureRegion.X + captureRegion.Width + 12, previewBounds.X);
        Assert.True(previewBounds.Width <= 208);
        Assert.True(previewBounds.Width >= 200);
        Assert.True(ScreenRegion.Intersect(previewBounds, captureRegion).IsEmpty);
    }

    private static ScreenRegion ChoosePreviewBounds(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds,
        int width,
        int height)
    {
        var method = typeof(ScrollCaptureProgressWindow).GetMethod(
            "ChoosePreviewBounds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<ScreenRegion>(method.Invoke(
            null,
            [captureRegion, monitorBounds, width, height]));
    }

    private static int GetPreviewPhysicalWidth(
        ScreenRegion captureRegion,
        ScreenRegion monitorBounds)
    {
        var method = typeof(ScrollCaptureProgressWindow).GetMethod(
            "GetPreviewPhysicalWidth",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<int>(method.Invoke(
            null,
            [captureRegion, monitorBounds]));
    }
}
