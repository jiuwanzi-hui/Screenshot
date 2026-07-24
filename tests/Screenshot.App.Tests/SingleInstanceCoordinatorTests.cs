using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondInstanceSignalsThePrimaryInstance()
    {
        var instanceName = $"Screenshot.App.Tests.{Guid.NewGuid():N}";
        var activationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var primary = SingleInstanceCoordinator.TryAcquire(
            instanceName,
            () => activationRequested.TrySetResult());

        using var secondary = SingleInstanceCoordinator.TryAcquire(
            instanceName,
            static () => { });

        Assert.NotNull(primary);
        Assert.Null(secondary);
        await activationRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void InstanceCanBeAcquiredAgainAfterThePrimaryIsDisposed()
    {
        var instanceName = $"Screenshot.App.Tests.{Guid.NewGuid():N}";
        var primary = SingleInstanceCoordinator.TryAcquire(
            instanceName,
            static () => { });

        Assert.NotNull(primary);
        primary.Dispose();

        using var replacement = SingleInstanceCoordinator.TryAcquire(
            instanceName,
            static () => { });
        Assert.NotNull(replacement);
    }
}
