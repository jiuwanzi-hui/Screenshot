using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

/// <summary>
/// Registers the normal background launch as a current-user logon task.
/// </summary>
public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string StartupRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private readonly string _taskName;
    private readonly Func<string> _commandFactory;

    public StartupRegistrationService(
        string? valueName = null,
        Func<string>? commandFactory = null)
    {
        _taskName = valueName ?? AppMetadata.StartupRegistrationValueName;
        _commandFactory = commandFactory ?? CreateStartupCommand;
    }

    public bool IsEnabled()
    {
        try
        {
            using var com = new ComDispatchScope();
            var service = ConnectTaskScheduler(com);
            var folder = com.Call(service, "GetFolder", "\\");
            var task = com.Call(folder, "GetTask", _taskName);
            if (!(bool)com.Get(task, "Enabled"))
            {
                return false;
            }

            var definition = com.Get(task, "Definition");
            var actions = com.Get(definition, "Actions");
            if ((int)com.Get(actions, "Count") < 1)
            {
                return false;
            }

            var action = com.Get(actions, "Item", 1);
            if ((int)com.Get(action, "Type") != TaskActionExec)
            {
                return false;
            }

            var triggers = com.Get(definition, "Triggers");
            using var identity = WindowsIdentity.GetCurrent();
            var hasCurrentUserLogonTrigger = false;
            for (var index = 1; index <= (int)com.Get(triggers, "Count"); index++)
            {
                var trigger = com.Get(triggers, "Item", index);
                if ((int)com.Get(trigger, "Type") == TaskTriggerLogon &&
                    (string.IsNullOrWhiteSpace((string?)com.Get(trigger, "UserId")) ||
                     string.Equals(
                         (string)com.Get(trigger, "UserId"),
                         identity.Name,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    hasCurrentUserLogonTrigger = true;
                    break;
                }
            }

            if (!hasCurrentUserLogonTrigger)
            {
                return false;
            }

            return CommandsEqual(
                _commandFactory(),
                BuildCommand(
                    (string)com.Get(action, "Path"),
                    (string?)com.Get(action, "Arguments") ?? string.Empty));
        }
        catch (COMException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // Keep the legacy Run value until the task has been registered
            // successfully. This makes migration and online updates
            // recoverable if Task Scheduler is unavailable.
            RegisterLogonTask();
            DeleteLegacyRegistryValue();
            return;
        }

        DeleteTaskIfPresent();
        DeleteLegacyRegistryValue();
    }

    private void RegisterLogonTask()
    {
        var (path, arguments) = SplitCommand(_commandFactory());
        using var identity = WindowsIdentity.GetCurrent();
        var userName = identity.Name;
        var userSid = identity.User?.Value ??
            throw new InvalidOperationException("无法确定当前用户。");

        using var com = new ComDispatchScope();
        var service = ConnectTaskScheduler(com);
        var folder = com.Call(service, "GetFolder", "\\");
        var definition = com.Call(service, "NewTask", 0);
        com.Set(com.Get(definition, "RegistrationInfo"), "Description", $"SnapCut 登录后后台启动 ({userName})");
        var principal = com.Get(definition, "Principal");
        com.Set(principal, "UserId", userSid);
        com.Set(principal, "LogonType", TaskLogonInteractiveToken);
        com.Set(principal, "RunLevel", 0);
        var settings = com.Get(definition, "Settings");
        com.Set(settings, "DisallowStartIfOnBatteries", false);
        com.Set(settings, "StopIfGoingOnBatteries", false);
        com.Set(settings, "StartWhenAvailable", true);
        var trigger = com.Call(com.Get(definition, "Triggers"), "Create", TaskTriggerLogon);
        com.Set(trigger, "UserId", userName);
        var action = com.Call(com.Get(definition, "Actions"), "Create", TaskActionExec);
        com.Set(action, "Path", path);
        com.Set(action, "Arguments", arguments);
        com.Call(folder, "RegisterTaskDefinition",
            _taskName,
            definition,
            TaskCreateOrUpdate,
            null,
            null,
            TaskLogonInteractiveToken,
            null);
    }

    private void DeleteTaskIfPresent()
    {
        try
        {
            using var com = new ComDispatchScope();
            var service = ConnectTaskScheduler(com);
            var folder = com.Call(service, "GetFolder", "\\");
            com.Call(folder, "DeleteTask", _taskName, 0);
        }
        catch (COMException)
        {
        }
        catch (FileNotFoundException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static object ConnectTaskScheduler(ComDispatchScope com)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException(
                "当前 Windows 不支持任务计划程序服务。");
        var service = Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException(
                "无法创建任务计划程序服务对象。");
        com.Track(service);
        com.Call(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
        return service;
    }

    private void DeleteLegacyRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            StartupRegistryPath,
            writable: true);
        key?.DeleteValue(_taskName, throwOnMissingValue: false);
    }

    private static string CreateStartupCommand()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Environment.GetCommandLineArgs().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(entryAssemblyPath) &&
                string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                return $"{Quote(processPath)} {Quote(entryAssemblyPath)} --background";
            }
        }

        return $"{Quote(processPath)} --background";
    }

    private static (string Path, string Arguments) SplitCommand(string command)
    {
        var value = command.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("开机启动命令为空。");
        }

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return (
                    value[1..closingQuote],
                    value[(closingQuote + 1)..].Trim());
            }
        }

        var separator = value.IndexOfAny([' ', '\t']);
        return separator < 0
            ? (value, string.Empty)
            : (value[..separator], value[separator..].Trim());
    }

    private static string BuildCommand(string path, string arguments) =>
        $"{Quote(path)} {arguments.Trim()}".TrimEnd();

    private static bool CommandsEqual(string expected, string actual)
    {
        var expectedParts = SplitCommand(expected);
        var actualParts = SplitCommand(actual);
        return string.Equals(
                   Path.GetFullPath(expectedParts.Path),
                   Path.GetFullPath(actualParts.Path),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   expectedParts.Arguments.Trim(),
                   actualParts.Arguments.Trim(),
                   StringComparison.Ordinal);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
