using Xunit;

namespace Screenshot.App.Tests;

/// <summary>
/// Opt-in xUnit attributes for tests that create real topmost windows or
/// inject global desktop input. They are skipped by default so a normal build
/// cannot hang testhost when the interactive desktop is busy or locked.
/// Set SNAPCUT_RUN_DESKTOP_TESTS=1 to run them explicitly.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (!DesktopIntegrationTestsEnabled)
        {
            Skip = "Desktop integration tests are opt-in; set SNAPCUT_RUN_DESKTOP_TESTS=1 to run.";
        }
    }

    internal static bool DesktopIntegrationTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("SNAPCUT_RUN_DESKTOP_TESTS"),
            "1",
            StringComparison.Ordinal);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        if (!DesktopFactAttribute.DesktopIntegrationTestsEnabled)
        {
            Skip = "Desktop integration tests are opt-in; set SNAPCUT_RUN_DESKTOP_TESTS=1 to run.";
        }
    }
}
