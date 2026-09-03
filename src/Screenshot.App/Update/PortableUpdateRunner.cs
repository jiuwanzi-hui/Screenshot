using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

public static class PortableUpdateRunner
{
    public const string UpdateArgument = "--apply-portable-update";
    public const string UpdateFailedArgument = "--update-failed";
    private const string CleanupArgument = "--cleanup-update-runner";
    private const string RunnerProcessArgument = "--update-runner-pid";
    private const string CleanupPackageArgument = "--cleanup-update-package";

    public static bool IsUpdateRequest(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0 &&
               string.Equals(
                   arguments[0],
                   UpdateArgument,
                   StringComparison.OrdinalIgnoreCase);
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
            Exception? applyFailure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    ApplyPackage(packagePath, targetDirectory);
                    applyFailure = null;
                    break;
                }
                catch (Exception exception) when (attempt == 0)
                {
                    applyFailure = exception;
                    Thread.Sleep(350);
                }
            }

            if (applyFailure is not null)
            {
                throw new IOException("更新文件写入失败。", applyFailure);
            }

            File.Delete(packagePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = restartPath,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("--updated");
            startInfo.ArgumentList.Add(version);
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
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    string.Equals(
                        relativePath.TrimEnd(Path.DirectorySeparatorChar),
                        AppMetadata.DataDirectoryName,
                        StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(
                        AppMetadata.DataDirectoryName + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        relativePath.TrimEnd(Path.DirectorySeparatorChar),
                        AppMetadata.TranslationModelsDirectoryName,
                        StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith(
                        AppMetadata.TranslationModelsDirectoryName +
                            Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(
                    stagingDirectory,
                    relativePath));
                var stagingRoot = EnsureTrailingSeparator(
                    Path.GetFullPath(stagingDirectory));
                if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("更新包包含不安全的文件路径。");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            foreach (var sourcePath in Directory.EnumerateFiles(
                         stagingDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, sourcePath);
                var destinationPath = Path.GetFullPath(Path.Combine(
                    targetDirectory,
                    relativePath));
                if (!destinationPath.StartsWith(
                        EnsureTrailingSeparator(targetDirectory),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("更新目标路径无效。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

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
