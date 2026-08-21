using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class WpfRenderingCompatibilityTests
{
    [Fact]
    public void WpfUsesSoftwareRenderingForHeadlessCompatibility()
    {
        Assert.Equal(
            System.Windows.Interop.RenderMode.SoftwareOnly,
            WpfRenderingCompatibility.GetCompatibleRenderMode());
    }
}
