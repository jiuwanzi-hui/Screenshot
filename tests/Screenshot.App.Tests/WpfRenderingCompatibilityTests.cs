using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class WpfRenderingCompatibilityTests
{
    [Fact]
    public void PhysicalDesktopOutputUsesHardwareRendering()
    {
        Assert.Equal(
            System.Windows.Interop.RenderMode.Default,
            WpfRenderingCompatibility.GetCompatibleRenderMode(
            [
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "NVIDIA GeForce RTX",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "ToDesk Virtual Display Adapter",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
            ]));
    }

    [Fact]
    public void VirtualOnlyDesktopUsesSoftwareRendering()
    {
        Assert.Equal(
            System.Windows.Interop.RenderMode.SoftwareOnly,
            WpfRenderingCompatibility.GetCompatibleRenderMode(
            [
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "GameViewer Virtual Display Adapter",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "OrayIddDriver Device",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
            ]));
    }

    [Fact]
    public void SoftwareRenderingCanBeForcedForCompatibility()
    {
        Assert.Equal(
            System.Windows.Interop.RenderMode.SoftwareOnly,
            WpfRenderingCompatibility.GetCompatibleRenderMode(
            [
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "Intel Graphics",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
            ],
            ["SnapCut.exe", "--software-rendering"]));
    }

    [Fact]
    public void HardwareRenderingCanBeForcedForDiagnostics()
    {
        Assert.Equal(
            System.Windows.Interop.RenderMode.Default,
            WpfRenderingCompatibility.GetCompatibleRenderMode(
            [
                new WpfRenderingCompatibility.DisplayAdapterDescriptor(
                    "ToDesk Virtual Display Adapter",
                    AttachedToDesktop: true,
                    IsMirroringOrRemote: false),
            ],
            ["SnapCut.exe", "--hardware-rendering"]));
    }
}
