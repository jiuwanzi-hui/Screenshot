using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticScrollRecoveryBudgetTests
{
    [Fact]
    public void ReconnectingToTheSameViewportCannotReplenishRecoveryForever()
    {
        var budget = new AutomaticScrollRecoveryBudget();
        budget.Reset(800);
        Assert.True(budget.TryRecover(800));
        Assert.True(budget.TryRecover(800));
        Assert.False(budget.TryRecover(800));
        Assert.False(budget.TryRecover(800));
        Assert.False(budget.TryRecover(805));
        Assert.True(budget.TryRecover(850));
        Assert.True(budget.TryRecover(850));
        Assert.False(budget.TryRecover(850));
        budget.Reset(850); // Explicit user resume grants another bounded attempt.
        Assert.True(budget.TryRecover(850));
    }
}
