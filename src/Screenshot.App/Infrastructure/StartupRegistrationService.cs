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
            dynamic service = ConnectTaskScheduler();
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(_taskName);
            if (!(bool)task.Enabled)
            {
                return false;
            }

            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count < 1)
            {
                return false;
            }

            dynamic action = actions[1];
            if ((int)action.Type != TaskActionExec)
            {
                return false;
            }

            dynamic triggers = task.Definition.Triggers;
            var hasCurrentUserLogonTrigger = false;
            for (var index = 1; index <= (int)triggers.Count; index++)
            {
                dynamic trigger = triggers[index];
                if ((int)trigger.Type == TaskTriggerLogon &&
                    (string.IsNullOrWhiteSpace((string?)trigger.UserId) ||
                     string.Equals(
                         (string)trigger.UserId,
                         WindowsIdentity.GetCurrent().Name,
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
                    (string)action.Path,
                    (string?)action.Arguments ?? string.Empty));
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

        dynamic service = ConnectTaskScheduler();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description =
            $"SnapCut 登录后后台启动 ({userName})";
        definition.Principal.UserId = userSid;
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = 0;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.StartWhenAvailable = true;

        dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
        trigger.UserId = userName;

        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = path;
        action.Arguments = arguments;
        folder.RegisterTaskDefinition(
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
            dynamic service = ConnectTaskScheduler();
            dynamic folder = service.GetFolder("\\");
            folder.DeleteTask(_taskName, 0);
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

    private static object ConnectTaskScheduler()
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException(
                "当前 Windows 不支持任务计划程序服务。");
        var service = Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException(
                "无法创建任务计划程序服务对象。");
        ((dynamic)service).Connect();
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
