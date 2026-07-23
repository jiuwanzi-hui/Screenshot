using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class ApplicationClosePolicyTests
{
    [Fact]
    public void HidesTheWindowForAnOrdinaryCloseRequest()
    {
        Assert.True(ApplicationClosePolicy.ShouldHideWindow(
            exitRequested: false,
            WindowCloseBehavior.MinimizeToBackground));
    }

    [Fact]
    public void AllowsTheWindowToCloseDuringExplicitApplicationExit()
    {
        Assert.False(ApplicationClosePolicy.ShouldHideWindow(
            exitRequested: true,
            WindowCloseBehavior.MinimizeToBackground));
        Assert.False(ApplicationClosePolicy.ShouldExitApplication(
            exitRequested: true,
            WindowCloseBehavior.ExitApplication));
    }

    [Fact]
    public void RequestsApplicationExitWhenConfiguredForExit()
    {
        Assert.False(ApplicationClosePolicy.ShouldHideWindow(
            exitRequested: false,
            WindowCloseBehavior.ExitApplication));
        Assert.True(ApplicationClosePolicy.ShouldExitApplication(
            exitRequested: false,
            WindowCloseBehavior.ExitApplication));
    }
}
