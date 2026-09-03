using System.ComponentModel;
using System.Diagnostics;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ElevationLaunchServiceTests
{
    [Fact]
    public void DefaultSettingsRequestAdministratorPrivileges()
    {
        Assert.True(AppSettings.CreateDefault().RequestAdministratorPrivileges);
    }

    [Fact]
    public void StartsElevatedCopyAndPreservesStartupArguments()
    {
        ProcessStartInfo? receivedStartInfo = null;
        var service = new ElevationLaunchService(
            isElevated: () => false,
            processPathProvider: () => @"C:\Program Files\SnapCut\SnapCut.exe",
            startProcess: startInfo =>
            {
                receivedStartInfo = startInfo;
                return Process.GetCurrentProcess();
            });

        var result = service.TryRelaunchElevated(
            AppSettings.CreateDefault() with { RequestAdministratorPrivileges = true },
            ["--background", "--updated", "3.1.0"]);

        Assert.True(result.RelaunchStarted);
        Assert.Null(result.Warning);
        Assert.NotNull(receivedStartInfo);
        Assert.Equal("runas", receivedStartInfo.Verb);
        Assert.True(receivedStartInfo.UseShellExecute);
        Assert.Equal(
            ["--background", "--updated", "3.1.0", ElevationLaunchService.ElevatedRelaunchArgument],
            receivedStartInfo.ArgumentList);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void DoesNotRequestElevationWhenItIsNotNeeded(
        bool requestAdministratorPrivileges,
        bool isElevated,
        bool containsRelaunchMarker)
    {
        var startCount = 0;
        var service = new ElevationLaunchService(
            isElevated: () => isElevated,
            processPathProvider: () => "SnapCut.exe",
            startProcess: _ =>
            {
                startCount++;
                return Process.GetCurrentProcess();
            });
        var arguments = containsRelaunchMarker
            ? new[] { ElevationLaunchService.ElevatedRelaunchArgument }
            : Array.Empty<string>();

        var result = service.TryRelaunchElevated(
            AppSettings.CreateDefault() with
            {
                RequestAdministratorPrivileges = requestAdministratorPrivileges,
            },
            arguments);

        Assert.False(result.RelaunchStarted);
        Assert.Null(result.Warning);
        Assert.Equal(0, startCount);
    }

    [Fact]
    public void ContinuesNormallyAndExplainsTheLimitationWhenUacIsDeclined()
    {
        var service = new ElevationLaunchService(
            isElevated: () => false,
            processPathProvider: () => "SnapCut.exe",
            startProcess: _ => throw new Win32Exception(1223));

        var result = service.TryRelaunchElevated(
            AppSettings.CreateDefault() with { RequestAdministratorPrivileges = true },
            ["--background"]);

        Assert.False(result.RelaunchStarted);
        Assert.Equal(
            "SnapCut 未以管理员权限启动；在以管理员权限运行的应用窗口中，鼠标截图、钉图及组合快捷键可能无法触发。",
            result.Warning);
    }
}
