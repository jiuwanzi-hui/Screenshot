using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed record LocalLargeTranslationModelStatus(
    bool IsInstalled,
    string InstallationDirectory,
    long DownloadSize,
    long InstalledSize,
    long AvailableSpace,
    string? ErrorMessage = null);

public sealed class LocalLargeTranslationModelManager : IDisposable
{
    private const string ModelFileName =
        "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf";
    private const string RuntimeArchiveName =
        "llama-b10276-bin-win-cpu-x64.zip";
    private static readonly DownloadableModelFile[] DownloadFiles =
    [
        new(
            "Qwen2.5 1.5B 翻译模型",
            ModelFileName,
            986_048_768,
            "1adf0b11065d8ad2e8123ea110d1ec956dab4ab038eab665614adba04b6c3370",
            [
                "https://www.modelscope.cn/models/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/master/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
                "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
            ]),
        new(
            "llama.cpp CPU 推理程序",
            RuntimeArchiveName,
            18_347_005,
            "b1db7fc5b3d2728dcead5b792b0565da045dec688df81c9272ce5aef5f55a3e8",
            ["https://github.com/ggml-org/llama.cpp/releases/download/b10276/llama-b10276-bin-win-cpu-x64.zip"]),
    ];

    private static readonly Lazy<LocalLargeTranslationModelManager> SharedManager =
        new(() => new LocalLargeTranslationModelManager());
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _installationLock = new(1, 1);
    private bool _disposed;

    public LocalLargeTranslationModelManager(
        string? installationDirectory = null,
        HttpClient? httpClient = null)
    {
        InstallationDirectory = Path.GetFullPath(
            installationDirectory ?? Path.Combine(
                AppMetadata.TranslationModelsDirectoryPath,
                AppMetadata.LocalLargeTranslationModelDirectoryName));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromHours(12),
        };
        _ownsHttpClient = httpClient is null;
    }

    public static LocalLargeTranslationModelManager Shared => SharedManager.Value;

    public string InstallationDirectory { get; }

    public string ModelPath => Path.Combine(
        InstallationDirectory,
        ModelFileName);

    public string? ExecutablePath
    {
        get
        {
            var runtimeDirectory = Path.Combine(InstallationDirectory, "runtime");
            return Directory.Exists(runtimeDirectory)
                ? Directory.EnumerateFiles(
                        runtimeDirectory,
                        "llama-cli.exe",
                        SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;
        }
    }

    public LocalLargeTranslationModelStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var modelInstalled = File.Exists(ModelPath) &&
                             new FileInfo(ModelPath).Length == DownloadFiles[0].Size;
        var runtimeInstalled = ExecutablePath is { } executable &&
                               File.Exists(executable);
        var downloadSize = (modelInstalled ? 0 : DownloadFiles[0].Size) +
                           (runtimeInstalled ? 0 : DownloadFiles[1].Size);
        return new LocalLargeTranslationModelStatus(
            modelInstalled && runtimeInstalled,
            InstallationDirectory,
            downloadSize,
            DownloadFiles.Sum(file => file.Size) + 35_000_000,
            GetAvailableSpace());
    }

    public async Task<(bool IsSuccess, string? ErrorMessage)> InstallAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken);
        try
        {
            var downloader = new ResumableModelDownloader(_httpClient);
            await downloader.DownloadAsync(
                DownloadFiles,
                InstallationDirectory,
                progress,
                cancellationToken);
            if (ExecutablePath is null)
            {
                await Task.Run(
                    ExtractRuntime,
                    cancellationToken);
            }

            return ExecutablePath is not null
                ? (true, null)
                : (false, "CPU 推理程序解压后不完整，请重新下载。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "本机翻译大模型下载已暂停，下次会从断点继续。");
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or
                UnauthorizedAccessException or InvalidDataException)
        {
            return (false, exception.Message);
        }
        finally
        {
            _installationLock.Release();
        }
    }

    private void ExtractRuntime()
    {
        var archivePath = Path.Combine(
            InstallationDirectory,
            RuntimeArchiveName);
        var runtimeDirectory = Path.Combine(
            InstallationDirectory,
            "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var root = Path.GetFullPath(runtimeDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(
                runtimeDirectory,
                entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("推理程序压缩包包含不安全路径。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private long GetAvailableSpace()
    {
        try
        {
            var root = Path.GetPathRoot(InstallationDirectory);
            return string.IsNullOrWhiteSpace(root)
                ? 0
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _installationLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
