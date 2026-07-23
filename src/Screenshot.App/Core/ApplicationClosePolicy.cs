namespace Screenshot.App.Core;

public static class ApplicationClosePolicy
{
    public static bool ShouldHideWindow(
        bool exitRequested,
        WindowCloseBehavior closeBehavior)
    {
        return !exitRequested &&
            closeBehavior == WindowCloseBehavior.MinimizeToBackground;
    }

    public static bool ShouldExitApplication(
        bool exitRequested,
        WindowCloseBehavior closeBehavior)
    {
        return !exitRequested &&
            closeBehavior == WindowCloseBehavior.ExitApplication;
    }
}
