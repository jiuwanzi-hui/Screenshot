using System.IO;
using Microsoft.Win32;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;
    private readonly Func<string> _commandFactory;

    public StartupRegistrationService(
        string? valueName = null,
        Func<string>? commandFactory = null)
    {
        _valueName = valueName ?? AppMetadata.StartupRegistrationValueName;
        _commandFactory = commandFactory ?? CreateStartupCommand;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
        return key?.GetValue(_valueName) is string value &&
               string.Equals(
                   value.Trim(),
                   _commandFactory(),
                   StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项配置。");

        if (enabled)
        {
            key.SetValue(_valueName, _commandFactory(), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(_valueName, throwOnMissingValue: false);
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
                string.Equals(
                    Path.GetExtension(entryAssemblyPath),
                    ".dll",
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"{Quote(processPath)} {Quote(entryAssemblyPath)} --background";
            }
        }

        return $"{Quote(processPath)} --background";
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }
}
