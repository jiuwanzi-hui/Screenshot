using System.Diagnostics;

namespace SnapCut.Mac.Text;

internal sealed class MacKeychainCredentialStore
{
    private const string Service = "com.jiuwanzi.snapcut.translation";
    private const string Account = "api-key";

    public static string? Load()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var result = Run(
            "find-generic-password",
            "-a", Account,
            "-s", Service,
            "-w");
        return result.ExitCode == 0
            ? result.Output.Trim()
            : null;
    }

    public static bool Save(string? value)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            var removed = Run(
                "delete-generic-password",
                "-a", Account,
                "-s", Service);
            return removed.ExitCode is 0 or 44;
        }

        return Run(
            "add-generic-password",
            "-U",
            "-a", Account,
            "-s", Service,
            "-w", value.Trim()).ExitCode == 0;
    }

    private static CommandResult Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var command = Process.Start(startInfo);
        if (command is null)
        {
            return new CommandResult(-1, string.Empty);
        }

        var output = command.StandardOutput.ReadToEnd();
        command.WaitForExit();
        return new CommandResult(command.ExitCode, output);
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
