using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

public static class PortableUpdateRunner
{
    public const string UpdateArgument = "--apply-portable-update";
    public const string InstallerUpdateArgument = "--apply-installer-update";
    public const string UpdateFailedArgument = "--update-failed";
    private const string CleanupArgument = "--cleanup-update-runner";
    private const string RunnerProcessArgument = "--update-runner-pid";
    private const string CleanupPackageArgument = "--cleanup-update-package";

    public static bool IsUpdateRequest(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0 &&
               (string.Equals(arguments[0], UpdateArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arguments[0], InstallerUpdateArgument, StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 6 || !int.TryParse(arguments[3], out var processId))
        {
            return 2;
        }

        string? restartPath = null;
        string? targetDirectory = null;
        string? version = null;
        try
        {
            var packagePath = Path.GetFullPath(arguments[1]);
            targetDirectory = EnsureTrailingSeparator(
                Path.GetFullPath(arguments[2]));
            restartPath = Path.GetFullPath(arguments[4]);
            version = arguments[5];
            ValidateUpdatePaths(packagePath, targetDirectory, restartPath);
            WaitForProcessExit(processId);
            var settingsSnapshot = RollbackSettingsSnapshot.Create(targetDirectory,
                typeof(PortableUpdateRunner).Assembly.GetName().Version!, Version.Parse(version));
            // Preflight settings before touching program files. The current
            // version also reads this numeric representation; on apply failure
            // it can restart with the same preferences. Never restart an older
            // executable with incompatible settings after a settings-write error.
            settingsSnapshot?.Apply();
            Exception? applyFailure = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (string.Equals(arguments[0], InstallerUpdateArgument, StringComparison.OrdinalIgnoreCase))
                    {
                        RunInstaller(packagePath, targetDirectory);
                    }
                    else
                    {
                        ApplyPackage(packagePath, targetDirectory);
                    }
                    applyFailure = null;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    applyFailure = exception;
                    if (attempt < 2) Thread.Sleep(1000 * (attempt + 1));
                }
            }

            if (applyFailure is not null)
            {
                throw new IOException("更新文件写入失败。", applyFailure);
            }

            // Failure to remove a cache file must not turn a successful install
            // into an error. The restarted application can retry this cleanup.
            try { File.Delete(packagePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var startInfo = new ProcessStartInfo
            {
                FileName = restartPath,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("--updated");
            startInfo.ArgumentList.Add(version);
            startInfo.ArgumentList.Add(CleanupPackageArgument);
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add(CleanupArgument);
            startInfo.ArgumentList.Add(Environment.ProcessPath ?? string.Empty);
            startInfo.ArgumentList.Add(RunnerProcessArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
                CultureInfo.InvariantCulture));
            _ = Process.Start(startInfo);
            return 0;
        }
        catch (Exception exception)
        {
            TryRecordFailureAndRestart(
                targetDirectory,
                restartPath,
                version,
                exception);
            return 1;
        }
    }

    private static void RunInstaller(string packagePath, string targetDirectory)
    {
        // The helper owns restart/retry, so Inno must not restart the app on its
        // own. Explicitly retain the current installation directory.
        var startInfo = ApplicationUpdateLauncher.CreateInstallerStartInfo(packagePath, restartAfterInstallation: false);
        startInfo.ArgumentList.Add("/DIR=" + targetDirectory.TrimEnd(Path.DirectorySeparatorChar));
        using var installer = Process.Start(startInfo) ??
            throw new IOException("无法启动覆盖安装程序。");
        installer.WaitForExit();
        if (installer.ExitCode is 2 or 5) throw new OperationCanceledException("用户取消了安装。");
        if (installer.ExitCode != 0) throw new IOException($"覆盖安装未完成，退出码 {installer.ExitCode}。");
    }

    private static void TryRecordFailureAndRestart(
        string? targetDirectory,
        string? restartPath,
        string? version,
        Exception exception)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory) ||
            string.IsNullOrWhiteSpace(restartPath))
        {
            return;
        }

        try
        {
            var updatesDirectory = Path.Combine(
                targetDirectory,
                AppMetadata.DataDirectoryName,
                AppMetadata.UpdatesDirectoryName);
            Directory.CreateDirectory(updatesDirectory);
            var failurePath = Path.Combine(updatesDirectory, "last-update-failure.txt");
            File.WriteAllText(
                failurePath,
                $"{DateTimeOffset.Now:O}\n版本: {version ?? "未知"}\n{exception}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = restartPath,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(UpdateFailedArgument);
            startInfo.ArgumentList.Add(version ?? string.Empty);
            startInfo.ArgumentList.Add(CleanupArgument);
            startInfo.ArgumentList.Add(Environment.ProcessPath ?? string.Empty);
            startInfo.ArgumentList.Add(RunnerProcessArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _ = Process.Start(startInfo);
        }
        catch
        {
            // The updater must not mask its original failure with a second
            // process or filesystem error.
        }
    }

    public static void ScheduleCleanup(IReadOnlyList<string> arguments)
    {
        SchedulePackageCleanup(arguments);
        var cleanupIndex = IndexOf(arguments, CleanupArgument);
        var processIndex = IndexOf(arguments, RunnerProcessArgument);
        if (cleanupIndex < 0 || cleanupIndex + 1 >= arguments.Count ||
            processIndex < 0 || processIndex + 1 >= arguments.Count ||
            !int.TryParse(arguments[processIndex + 1], out var processId))
        {
            return;
        }

        var runnerPath = Path.GetFullPath(arguments[cleanupIndex + 1]);
        var updatesDirectory = EnsureTrailingSeparator(
            Path.GetFullPath(AppMetadata.UpdatesDirectoryPath));
        if (!runnerPath.StartsWith(updatesDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            WaitForProcessExit(processId);
            RetryDelete(runnerPath);
            var runnerDirectory = Path.GetDirectoryName(runnerPath)!;
            if (string.Equals(Path.GetDirectoryName(runnerDirectory) + Path.DirectorySeparatorChar,
                    updatesDirectory, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(runnerDirectory).StartsWith("runner-", StringComparison.Ordinal) &&
                Guid.TryParseExact(Path.GetFileName(runnerDirectory)[7..], "N", out _) &&
                string.Equals(Path.GetFileName(runnerPath), ApplicationUpdateLauncher.RunnerFileName, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(runnerDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });
    }

    private static void SchedulePackageCleanup(IReadOnlyList<string> arguments)
    {
        var packageIndex = IndexOf(arguments, CleanupPackageArgument);
        if (packageIndex < 0 || packageIndex + 1 >= arguments.Count)
        {
            return;
        }

        var packagePath = Path.GetFullPath(arguments[packageIndex + 1]);
        var updatesDirectory = EnsureTrailingSeparator(
            Path.GetFullPath(AppMetadata.UpdatesDirectoryPath));
        if (!packagePath.StartsWith(updatesDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Thread.Sleep(1500);
            RetryDelete(packagePath);
        });
    }

    private static void RetryDelete(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }

    internal static void ApplyPackage(string packagePath, string targetDirectory)
    {
        var stagingDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(packagePath))!,
            $"apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            PortableUpdateArchive.ExtractToDirectory(packagePath, stagingDirectory);

            CommitStagedFiles(stagingDirectory, targetDirectory);

            // The application used to ship as Screenshot.exe. When updating a
            // directory that still holds the old-name binaries, remove them so
            // the folder does not keep a runnable stale copy.
            foreach (var legacyFileName in new[]
                     {
                         "Screenshot.exe",
                         "Screenshot.dll",
                         "Screenshot.pdb",
                         "Screenshot.deps.json",
                         "Screenshot.runtimeconfig.json",
                     })
            {
                try
                {
                    File.Delete(Path.Combine(targetDirectory, legacyFileName));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    internal static void CommitStagedFiles(string stagingDirectory, string targetDirectory)
    {
        var targetRoot = EnsureTrailingSeparator(Path.GetFullPath(targetDirectory));
        var backupDirectory = stagingDirectory + "-backup";
        var committed = new List<(string Destination, string? Backup)>();
        var preserveBackup = false;
        try
        {
            foreach (var source in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(stagingDirectory, source);
                // Staged trees must also preserve user data when supplied by a
                // caller other than our archive extractor.
                if (PortableUpdateArchive.IsUserDataPath(relative)) continue;
                var destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新目标路径无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = Path.Combine(backupDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(destination, backup);
                }
                committed.Add((destination, backup));
                // Staging is on the same volume as the installation. Moving a
                // complete file avoids truncating a working EXE on copy failure.
                File.Move(source, destination);
            }
        }
        catch (Exception applyFailure)
        {
            var restoreFailures = new List<Exception>();
            foreach (var file in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (file.Backup is not null) File.Move(file.Backup, file.Destination, overwrite: true);
                    else File.Delete(file.Destination);
                }
                catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                {
                    restoreFailures.Add(restoreFailure);
                }
            }
            if (restoreFailures.Count > 0)
            {
                preserveBackup = true;
                throw new IOException($"更新中断，原文件备份保留在 {backupDirectory}。",
                    new AggregateException(new[] { applyFailure }.Concat(restoreFailures)));
            }
            throw;
        }
        finally
        {
            if (!preserveBackup && Directory.Exists(backupDirectory))
            {
                try { Directory.Delete(backupDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void ValidateUpdatePaths(
        string packagePath,
        string targetDirectory,
        string restartPath)
    {
        var updatesDirectory = EnsureTrailingSeparator(Path.Combine(
            targetDirectory,
            AppMetadata.DataDirectoryName,
            AppMetadata.UpdatesDirectoryName));
        if (!packagePath.StartsWith(updatesDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                restartPath,
                Path.Combine(targetDirectory, "SnapCut.exe"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新路径未通过安全检查。");
        }
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                throw new TimeoutException("等待旧程序退出超时。");
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }
}
