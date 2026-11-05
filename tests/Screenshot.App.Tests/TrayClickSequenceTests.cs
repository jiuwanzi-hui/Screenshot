using Screenshot.App.Infrastructure;
namespace Screenshot.App.Tests;

public sealed class TrayClickSequenceTests
{
    [Fact]
    public void SingleClickRunsOnceAfterDoubleClickInterval()
    {
        var sequence = new TrayClickSequence();
        Assert.True(sequence.LeftReleased());
        Assert.True(sequence.CompleteSingleClick());
        Assert.False(sequence.CompleteSingleClick());
    }
    [Fact]
    public void DoubleClickCancelsCaptureIncludingItsFinalMouseRelease()
    {
        var sequence = new TrayClickSequence();
        Assert.True(sequence.LeftReleased());
        sequence.DoubleClicked();
        Assert.False(sequence.LeftReleased());
        Assert.False(sequence.CompleteSingleClick());
        Assert.True(sequence.LeftReleased());
        Assert.True(sequence.CompleteSingleClick());
    }
}
