using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

public static class ApplicationUpdateLauncher
{
    public static void Launch(
        ApplicationUpdateInfo update,
        string downloadedPackagePath)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedPackagePath);

        if (AppMetadata.IsInstalled)
        {
            LaunchInstaller(downloadedPackagePath);
            return;
        }

        LaunchPortableRunner(update, downloadedPackagePath);
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath)
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
        startInfo.ArgumentList.Add("/UPDATE=1");
        startInfo.ArgumentList.Add($"/UPDATEPACKAGE={installerPath}");
        return startInfo;
    }

    private static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("下载的安装程序不存在。", installerPath);
        }

        _ = Process.Start(CreateInstallerStartInfo(installerPath)) ??
            throw new InvalidOperationException("无法启动更新安装程序。");
    }

    private static void LaunchPortableRunner(
        ApplicationUpdateInfo update,
        string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("下载的免安装更新包不存在。", packagePath);
        }

        Directory.CreateDirectory(AppMetadata.UpdatesDirectoryPath);
        var runnerPath = Path.Combine(
            AppMetadata.UpdatesDirectoryPath,
            $"Screenshot.UpdateRunner-{Guid.NewGuid():N}.exe");
        try
        {
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var executable = archive.Entries.SingleOrDefault(entry =>
                    string.Equals(
                        entry.FullName.Replace('\\', '/'),
                        "Screenshot.exe",
                        StringComparison.OrdinalIgnoreCase));
                if (executable is null || executable.Length <= 0)
                {
                    throw new InvalidDataException("免安装更新包中缺少 Screenshot.exe。");
                }

                executable.ExtractToFile(runnerPath, overwrite: true);
            }

            var processPath = Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定当前程序路径。");
            var startInfo = new ProcessStartInfo
            {
                FileName = runnerPath,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(PortableUpdateRunner.UpdateArgument);
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
                File.Delete(runnerPath);
            }
            catch
            {
            }

            throw;
        }
    }
}
