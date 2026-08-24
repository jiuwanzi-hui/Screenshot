using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record ElevationLaunchResult(
    bool RelaunchStarted,
    string? Warning);

/// <summary>
/// Starts a second copy through UAC while keeping the normal manifest level.
/// This lets a declined prompt fall back to a usable non-elevated instance.
/// </summary>
public sealed class ElevationLaunchService
{
    public const string ElevatedRelaunchArgument = "--elevated-relaunch";

    private readonly Func<bool> _isElevated;
    private readonly Func<string?> _processPathProvider;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public ElevationLaunchService(
        Func<bool>? isElevated = null,
        Func<string?>? processPathProvider = null,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        _isElevated = isElevated ?? IsCurrentProcessElevated;
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
        _startProcess = startProcess ?? Process.Start;
    }

    public bool ShouldRequestElevation(
        AppSettings settings,
        IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(arguments);

        return settings.RequestAdministratorPrivileges &&
               !_isElevated() &&
               !arguments.Any(argument => string.Equals(
                   argument,
                   ElevatedRelaunchArgument,
                   StringComparison.OrdinalIgnoreCase));
    }

    public ElevationLaunchResult TryRelaunchElevated(
        AppSettings settings,
        IEnumerable<string> arguments)
    {
        var argumentList = arguments.ToArray();
        if (!ShouldRequestElevation(settings, argumentList))
        {
            return new ElevationLaunchResult(RelaunchStarted: false, Warning: null);
        }

        var processPath = _processPathProvider();
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return new ElevationLaunchResult(
                RelaunchStarted: false,
                Warning: "无法请求管理员权限；SnapCut 将以普通权限继续运行，高权限窗口中的鼠标截图快捷键可能无法触发。");
        }

        try
        {
            var process = _startProcess(CreateElevatedStartInfo(processPath, argumentList));
            if (process is not null)
            {
                return new ElevationLaunchResult(RelaunchStarted: true, Warning: null);
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new ElevationLaunchResult(
                RelaunchStarted: false,
                Warning: "SnapCut 未以管理员权限启动；在以管理员权限运行的应用窗口中，鼠标截图、钉图及组合快捷键可能无法触发。");
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return new ElevationLaunchResult(
            RelaunchStarted: false,
            Warning: "无法请求管理员权限；SnapCut 将以普通权限继续运行，高权限窗口中的鼠标截图快捷键可能无法触发。");
    }

    public static ProcessStartInfo CreateElevatedStartInfo(
        string processPath,
        IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        foreach (var argument in arguments.Where(argument => !string.Equals(
                     argument,
                     ElevatedRelaunchArgument,
                     StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(ElevatedRelaunchArgument);
        return startInfo;
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
