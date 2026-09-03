using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Screenshot.App.Capture;
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
    public const string LegacyPersistentElevationTaskName = "SnapCut.Elevated";
    private const string StablePersistentElevationTaskName = "SnapCut.Elevated.Persistent";

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

        CaptureTimingDiagnostics.Mark(
            "elevation-relaunch-requested",
            $"pid={Environment.ProcessId} args={string.Join(',', argumentList)}");

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
                CaptureTimingDiagnostics.Mark(
                    "elevation-runas-accepted",
                    $"childPid={process.Id}");
                // Do not synchronously poll for the replacement process here.
                // That wait used to hold the original UI thread for up to
                // three seconds immediately after UAC returned, which made
                // the pointer appear frozen. The child owns the same
                // single-instance event and will become primary as soon as
                // it reaches startup.
                process.Dispose();
                return new ElevationLaunchResult(
                    RelaunchStarted: true,
                    Warning: null);
            }

            CaptureTimingDiagnostics.Mark("elevation-runas-no-process");
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

    /// <summary>
    /// Registers an enabled task with the highest available token. Its
    /// one-time schedule is deliberately placed in the future so the existing
    /// startup Run entry remains the only automatic launch trigger; SnapCut
    /// starts this task explicitly during normal launches.
    /// </summary>
    public static bool TryEnsurePersistentElevationTask(
        string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            Core.StartupDiagnostics.LogElevation("TryEnsurePersistentElevationTask: processPath is null or empty");
            return false;
        }

        try
        {
            var taskName = GetPersistentElevationTaskName(processPath);
            Core.StartupDiagnostics.LogElevation($"TryEnsurePersistentElevationTask: creating task '{taskName}' for '{processPath}'");

            var startInfo = CreateSchtasksStartInfo("/Create");
            startInfo.ArgumentList.Add("/TN");
            startInfo.ArgumentList.Add(taskName);
            startInfo.ArgumentList.Add("/SC");
            startInfo.ArgumentList.Add("ONCE");
            startInfo.ArgumentList.Add("/ST");
            startInfo.ArgumentList.Add("00:00");
            startInfo.ArgumentList.Add("/SD");
            // schtasks parses /SD using the system task-scheduler format.
            // Chinese Windows rejects the US-style 01/01/2099 form with
            // ERROR_INVALID_DATE, which silently disabled persistent UAC.
            startInfo.ArgumentList.Add("2099/01/01");
            startInfo.ArgumentList.Add("/RL");
            startInfo.ArgumentList.Add("HIGHEST");
            startInfo.ArgumentList.Add("/IT");
            startInfo.ArgumentList.Add("/TR");
            startInfo.ArgumentList.Add(BuildTaskCommand(processPath, [
                "--background",
                ElevatedRelaunchArgument,
            ]));
            startInfo.ArgumentList.Add("/F");
            // The task is intentionally enabled: Windows refuses to execute
            // a disabled task through `schtasks /Run`. Its future schedule
            // prevents an additional automatic launch at logon.
            var created = RunSchtasks(startInfo);
            Core.StartupDiagnostics.LogElevation($"TryEnsurePersistentElevationTask: path-specific task creation result={created}");

            if (created)
            {
                // Older builds used a global task name whose action could
                // point at a different checkout. Remove that stale alias
                // after the path-specific task has been created.
                TryRemoveTask(LegacyPersistentElevationTaskName);
                var stableInfo = CreateSchtasksStartInfo("/Create");
                stableInfo.ArgumentList.Add("/TN");
                stableInfo.ArgumentList.Add(StablePersistentElevationTaskName);
                stableInfo.ArgumentList.Add("/SC");
                stableInfo.ArgumentList.Add("ONCE");
                stableInfo.ArgumentList.Add("/ST");
                stableInfo.ArgumentList.Add("00:00");
                stableInfo.ArgumentList.Add("/SD");
                stableInfo.ArgumentList.Add("2099/01/01");
                stableInfo.ArgumentList.Add("/RL");
                stableInfo.ArgumentList.Add("HIGHEST");
                stableInfo.ArgumentList.Add("/IT");
                stableInfo.ArgumentList.Add("/TR");
                stableInfo.ArgumentList.Add(BuildTaskCommand(processPath, [
                    "--background",
                    ElevatedRelaunchArgument,
                ]));
                stableInfo.ArgumentList.Add("/F");
                var stableCreated = RunSchtasks(stableInfo);
                Core.StartupDiagnostics.LogElevation($"TryEnsurePersistentElevationTask: stable task creation result={stableCreated}");
            }

            return created;
        }
        catch (Exception ex)
        {
            Core.StartupDiagnostics.LogElevation($"TryEnsurePersistentElevationTask: exception={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Uses the task created after the first UAC consent. A normal launch must
    /// only query and run the task: changing a task action requires elevation
    /// itself and would turn every subsequent launch back into a UAC prompt.
    /// The elevated child refreshes the task action when it starts.
    /// </summary>
    public static bool TryRunPersistentElevationTask(
        string processPath,
        IEnumerable<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            Core.StartupDiagnostics.LogElevation("TryRunPersistentElevationTask: processPath is null or empty");
            return false;
        }

        try
        {
            var taskName = GetPersistentElevationTaskName(processPath);
            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: querying task '{taskName}'");

            var queryInfo = CreateSchtasksStartInfo("/Query");
            queryInfo.ArgumentList.Add("/TN");
            queryInfo.ArgumentList.Add(taskName);
            // schtasks /Query supports TABLE, LIST and CSV output. XML is an
            // export format for /QueryXml on newer systems, not a valid /FO
            // value; using it made every startup report a missing task and
            // repeatedly fall back to a second UAC process.
            queryInfo.ArgumentList.Add("/FO");
            queryInfo.ArgumentList.Add("LIST");
            var taskXml = ReadSchtasksOutput(queryInfo);

            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: task query result hasXml={taskXml is not null}");

            // 对于便携版，只要任务存在就尝试运行，不验证路径
            // 因为便携版可能每次解压到不同位置
            if (taskXml is not null)
            {
                Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: running task '{taskName}'");
                var ran = RunPersistentTask(taskName);
                Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: task run result={ran}");
                if (ran)
                {
                    CaptureTimingDiagnostics.Mark(
                        "elevation-task-run-accepted",
                        $"task={taskName}");
                    return true;
                }
            }

            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: trying stable task '{StablePersistentElevationTaskName}'");
            var stableQueryInfo = CreateSchtasksStartInfo("/Query");
            stableQueryInfo.ArgumentList.Add("/TN");
            stableQueryInfo.ArgumentList.Add(StablePersistentElevationTaskName);
            stableQueryInfo.ArgumentList.Add("/FO");
            stableQueryInfo.ArgumentList.Add("LIST");
            var stableTaskXml = ReadSchtasksOutput(stableQueryInfo);
            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: stable task query result hasXml={stableTaskXml is not null}");

            if (stableTaskXml is not null)
            {
                Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: running stable task '{StablePersistentElevationTaskName}'");
                var ran = RunPersistentTask(StablePersistentElevationTaskName);
                Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: stable task run result={ran}");
                if (ran)
                {
                    CaptureTimingDiagnostics.Mark(
                        "elevation-task-run-accepted",
                        $"task={StablePersistentElevationTaskName}");
                    return true;
                }
            }

            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: no valid task found");
            CaptureTimingDiagnostics.Mark(
                "elevation-task-missing",
                $"task={taskName} stable={StablePersistentElevationTaskName}");
            return false;
        }
        catch (Exception ex)
        {
            Core.StartupDiagnostics.LogElevation($"TryRunPersistentElevationTask: exception={ex.Message}");
            return false;
        }
    }

    public static bool TryRemovePersistentElevationTask(string? processPath = null)
    {
        var removed = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                removed = TryRemoveTask(GetPersistentElevationTaskName(processPath));
            }

            // Also remove the pre-3.7.9 global alias. It is unsafe because it
            // has no relationship to the executable that is being launched.
            removed = TryRemoveTask(LegacyPersistentElevationTaskName) || removed;
            return removed;
        }
        catch
        {
            return false;
        }
    }

    public static string GetPersistentElevationTaskName(string processPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        // 对于便携版，使用固定的任务名而不是基于路径的哈希
        // 这样即使解压到不同位置也能复用同一个提权任务
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return $"{fileName}.Elevated.Portable";
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

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static ProcessStartInfo CreateSchtasksStartInfo(string command)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static string BuildTaskCommand(
        string processPath,
        IEnumerable<string> arguments)
    {
        // /TR receives one command-line string. Keep the executable path in
        // ordinary Windows quotes; backslash-escaped quotes are written
        // literally into the task XML and make Task Scheduler report a
        // successful launch while failing with ERROR_FILE_NOT_FOUND.
        var escapedPath = processPath.Replace("\"", "", StringComparison.Ordinal);
        var escapedArguments = arguments.Select(argument =>
            argument.Contains(' ', StringComparison.Ordinal)
                ? $"\"{argument.Replace("\"", "", StringComparison.Ordinal)}\""
                : argument);
        return $"\"{escapedPath}\" {string.Join(' ', escapedArguments)}";
    }

    private static bool RunSchtasks(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit(3000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        return process.ExitCode == 0;
    }

    private static string? ReadSchtasksOutput(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(3000) || process.ExitCode != 0)
        {
            return null;
        }

        return output;
    }

    private static bool TryRemoveTask(string taskName)
    {
        try
        {
            var startInfo = CreateSchtasksStartInfo("/Delete");
            startInfo.ArgumentList.Add("/TN");
            startInfo.ArgumentList.Add(taskName);
            startInfo.ArgumentList.Add("/F");
            return RunSchtasks(startInfo);
        }
        catch
        {
            return false;
        }
    }

    private static bool RunPersistentTask(string taskName)
    {
        var runInfo = CreateSchtasksStartInfo("/Run");
        runInfo.ArgumentList.Add("/TN");
        runInfo.ArgumentList.Add(taskName);
        return RunSchtasks(runInfo);
    }

    private static bool WaitForReplacementProcess(string processPath)
    {
        var expectedPath = Path.GetFullPath(processPath);
        var expectedName = Path.GetFileNameWithoutExtension(expectedPath);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName(expectedName))
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var actualPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(actualPath) &&
                        string.Equals(
                            Path.GetFullPath(actualPath),
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // An elevated process can temporarily deny module access;
                    // keep polling until the startup window expires.
                }
                finally
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
