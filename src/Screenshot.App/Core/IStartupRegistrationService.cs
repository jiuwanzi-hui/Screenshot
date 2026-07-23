namespace Screenshot.App.Core;

public interface IStartupRegistrationService
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}
