using Screenshot.App.Infrastructure;
using System.Reflection;

namespace Screenshot.App.Tests;

public sealed class StartupRegistrationServiceTests : IDisposable
{
    private readonly string _valueName = $"Screenshot.App.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void EnablesAndDisablesAnIsolatedCurrentUserStartupRegistration()
    {
        var registrationService = new StartupRegistrationService(
            _valueName,
            () => "\"C:\\Program Files\\Screenshot.App\\Screenshot.exe\"");

        registrationService.SetEnabled(enabled: true);
        Assert.True(registrationService.IsEnabled());

        registrationService.SetEnabled(enabled: false);
        Assert.False(registrationService.IsEnabled());
    }

    [Fact]
    public void StartsInBackgroundFromTheWindowsStartupEntry()
    {
        var commandFactory = typeof(StartupRegistrationService).GetMethod(
            "CreateStartupCommand",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(commandFactory);
        var command = Assert.IsType<string>(commandFactory.Invoke(null, null));
        Assert.EndsWith(" --background", command, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        new StartupRegistrationService(_valueName).SetEnabled(enabled: false);
    }
}
