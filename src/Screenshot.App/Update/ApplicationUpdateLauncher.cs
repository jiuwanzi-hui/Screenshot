using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Reflection.PortableExecutable;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

public static class ApplicationUpdateLauncher
{
    internal const string RunnerFileName = "SnapCut.UpdateRunner.exe";
    public static void Launch(
        ApplicationUpdateInfo update,
        string downloadedPackagePath)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedPackagePath);

        if (AppMetadata.IsInstalled)
        {
            LaunchPortableRunner(update, downloadedPackagePath, installer: true);
            return;
        }

        LaunchPortableRunner(update, downloadedPackagePath);
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath, bool restartAfterInstallation = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/SILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add(restartAfterInstallation ? "/UPDATE=1" : "/UPDATE=0");
        startInfo.ArgumentList.Add($"/UPDATEPACKAGE={installerPath}");
        return startInfo;
    }

    private static void LaunchPortableRunner(
        ApplicationUpdateInfo update,
        string packagePath,
        bool installer = false)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("下载的免安装更新包不存在。", packagePath);
        }

        Directory.CreateDirectory(AppMetadata.UpdatesDirectoryPath);
        var runnerDirectory = Path.Combine(AppMetadata.UpdatesDirectoryPath, $"runner-{Guid.NewGuid():N}");
        // Do not match Inno's CloseApplicationsFilter=SnapCut.exe: the helper
        // must stay alive while its child installer replaces the application.
        var runnerPath = Path.Combine(runnerDirectory, RunnerFileName);
        try
        {
            // Always use the running version's updater. Extracting the target
            // EXE here also downgrades data protection and archive support.
            StageRunner(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"),
                runnerPath, frameworkDependent: !string.IsNullOrEmpty(typeof(ApplicationUpdateLauncher).Assembly.Location));

            var processPath = Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定当前程序路径。");
            var startInfo = new ProcessStartInfo
            {
                FileName = runnerPath,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add(installer ? PortableUpdateRunner.InstallerUpdateArgument : PortableUpdateRunner.UpdateArgument);
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add(AppContext.BaseDirectory);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
                CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(processPath);
            startInfo.ArgumentList.Add(ApplicationUpdateService.NormalizeVersion(update.Version));
            _ = Process.Start(startInfo) ??
                throw new InvalidOperationException("无法启动免安装版更新进程。");
        }
        catch
        {
            try
            {
                Directory.Delete(runnerDirectory, recursive: true);
            }
            catch
            {
            }

            throw;
        }
    }

    internal static void StageRunner(string executablePath, string runnerPath, bool frameworkDependent)
    {
        var source = Path.GetDirectoryName(executablePath)!;
        var destination = Path.GetDirectoryName(runnerPath)!;
        Directory.CreateDirectory(destination);
        File.Copy(executablePath, runnerPath);
        if (!frameworkDependent) return; // Published single-file packages carry their managed dependencies.
        foreach (var path in Directory.EnumerateFiles(source, "*.dll"))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.HasMetadata) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        }
        foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json" })
        {
            var path = Path.ChangeExtension(executablePath, suffix);
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        }
    }
}
