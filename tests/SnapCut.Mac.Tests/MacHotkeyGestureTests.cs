using SnapCut.Mac.App;

namespace SnapCut.Mac.Tests;

public sealed class MacHotkeyGestureTests
{
    [Fact]
    public void MatchesOnlyTheConfiguredModifiers()
    {
        var gesture = MacHotkeyGesture.CaptureDefault;
        var expected = (ulong)(MacHotkeyModifiers.Command | MacHotkeyModifiers.Shift);

        Assert.True(gesture.Matches(0, expected));
        Assert.False(gesture.Matches(1, expected));
        Assert.False(gesture.Matches(
            0,
            expected | (ulong)MacHotkeyModifiers.Option));
    }

    [Fact]
    public void IgnoresUnrelatedSystemFlagBits()
    {
        var gesture = MacHotkeyGesture.ScrollDefault;
        var flags = (ulong)gesture.Modifiers | (1UL << 16);

        Assert.True(gesture.Matches(gesture.KeyCode, flags));
    }
}
