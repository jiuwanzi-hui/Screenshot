using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class WpfRenderingCompatibilityTests
{
    [Theory]
    [InlineData(new[] { "SnapCut.exe", "--software-rendering" }, "--software-rendering")]
    [InlineData(new[] { "SnapCut.exe", "--HARDWARE-RENDERING" }, "--hardware-rendering")]
    [InlineData(new[] { "--hardware-rendering", "--software-rendering" }, "--software-rendering")]
    [InlineData(new[] { "--SOFTWARE-RENDERING", "--hardware-rendering" }, "--software-rendering")]
    [InlineData(new[] { "SnapCut.exe", "--background", "--updated", "4.1.7" }, null)]
    [InlineData(new[] { "--software-rendering=false", "--hardware-rendering-extra" }, null)]
    [InlineData(new string[0], null)]
    public void ChildRenderingOverridePreservesExplicitChoiceAndExcludesUnrelatedArguments(
        string[] arguments, string? expected)
    {
        Assert.Equal(expected, WpfRenderingCompatibility.GetExplicitRenderingArgument(arguments));
    }

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
