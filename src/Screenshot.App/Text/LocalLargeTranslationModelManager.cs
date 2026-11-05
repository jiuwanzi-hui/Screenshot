using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
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
        "translategemma-4b-it-Q4_K_M.gguf";
    private const string RuntimeArchiveName =
        "llama-b10276-bin-win-vulkan-x64.zip";
    private static readonly DownloadableModelFile[] DefaultDownloadFiles =
    [
        new(
            "TranslateGemma 4B 多语言翻译模型",
            ModelFileName,
            2_489_909_312,
            "7f7357c14abd9da4eb200b38b05da502cd6e10d7e1d403fbc9f78c19f3209b72",
            [
                "https://modelscope.cn/models/bullerwins/translategemma-4b-it-GGUF/resolve/5f0545305035039384fde236d8be8237b1c46102/translategemma-4b-it-Q4_K_M.gguf",
                "https://modelscope.cn/models/42ailab/TranslateGemma-4B-GGUF/resolve/fd2e388a08a8f11159dcdaf9e637975517b288c3/translategemma-4b-it-Q4_K_M.gguf",
                "https://hf-mirror.com/bullerwins/translategemma-4b-it-GGUF/resolve/7c938465a870d8624bcfa98a8e4a3510053c19a8/translategemma-4b-it-Q4_K_M.gguf",
                "https://huggingface.co/bullerwins/translategemma-4b-it-GGUF/resolve/7c938465a870d8624bcfa98a8e4a3510053c19a8/translategemma-4b-it-Q4_K_M.gguf",
            ]),
        new(
            "llama.cpp 显卡 / CPU 推理程序",
            RuntimeArchiveName,
            34_096_096,
            "e9f0f8cd772258a1bbc3c1f39bedbb63b2ce176ddfd1d2eecdd2be65406eba70",
            ["https://github.com/ggml-org/llama.cpp/releases/download/b10276/llama-b10276-bin-win-vulkan-x64.zip"]),
    ];

    private static readonly Lazy<LocalLargeTranslationModelManager> SharedManager =
        new(() => new LocalLargeTranslationModelManager());
    private static readonly Lazy<LocalLargeTranslationModelManager> LightweightManager =
        new(() => new LocalLargeTranslationModelManager(lightweight: true));
    private readonly DownloadableModelFile[] DownloadFiles;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _installationLock = new(1, 1);
    private bool _disposed;
    private readonly Lazy<LocalTranslationInferenceSession> _inferenceSession;
    private readonly string _bundledPackagesDirectory;
    private readonly string?[] _preparedRuntimes = new string?[2];

    private string BundledRuntimeArchivePath => Path.Combine(_bundledPackagesDirectory, RuntimeArchiveName);

    public LocalLargeTranslationModelManager(
        string? installationDirectory = null,
        HttpClient? httpClient = null,
        string? bundledPackagesDirectory = null,
        bool lightweight = false)
    {
        IsLightweight = lightweight;
        DownloadFiles = lightweight
            ? [new DownloadableModelFile("Hy-MT2 1.8B 轻量翻译模型", "Hy-MT2-1.8B-Q4_K_M.gguf",
                1_133_080_448, "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699",
                ["https://modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-GGUF/resolve/ef1d40b8b315575d30d1eb6579996130b8fc0bb2/Hy-MT2-1.8B-Q4_K_M.gguf",
                 "https://hf-mirror.com/tencent/Hy-MT2-1.8B-GGUF/resolve/1cd5208700acedef4ef93019b6cfc148b8522d45/Hy-MT2-1.8B-Q4_K_M.gguf",
                 "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/1cd5208700acedef4ef93019b6cfc148b8522d45/Hy-MT2-1.8B-Q4_K_M.gguf"]), DefaultDownloadFiles[1]]
            : DefaultDownloadFiles;
        InstallationDirectory = Path.GetFullPath(
            installationDirectory ?? Path.Combine(
                AppMetadata.TranslationModelsDirectoryPath,
                lightweight ? "Hy-MT2-1.8B" : AppMetadata.LocalLargeTranslationModelDirectoryName));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromHours(12),
        };
        _ownsHttpClient = httpClient is null;
        _bundledPackagesDirectory = bundledPackagesDirectory ?? Path.Combine(AppContext.BaseDirectory, "OfflinePackages");
        _inferenceSession = new(() => new LocalTranslationInferenceSession(this));
    }

    public static LocalLargeTranslationModelManager Shared => SharedManager.Value;
    public static LocalLargeTranslationModelManager LightweightShared => LightweightManager.Value;
    public bool IsLightweight { get; }
    public string DisplayName => IsLightweight ? "Hy-MT2 1.8B" : "TranslateGemma 4B";
    public long ModelDownloadSize => DownloadFiles[0].Size;

    public string InstallationDirectory { get; }

    internal LocalTranslationInferenceSession InferenceSession => _inferenceSession.Value;

    internal async Task<string> PrepareInferenceRuntimeAsync(bool cpuOnly, CancellationToken token)
    {
        await _installationLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = cpuOnly ? 0 : 1;
            if (_preparedRuntimes[index] is { } prepared) return prepared;
            var archive = File.Exists(BundledRuntimeArchivePath) ? BundledRuntimeArchivePath :
                Path.Combine(InstallationDirectory, RuntimeArchiveName);
            if (!File.Exists(archive))
                throw new InvalidOperationException("缺少离线推理程序压缩包，请在设置 → 翻译下载或导入；无需重新下载已有模型。");
            var directory = Path.Combine(InstallationDirectory, "inference-b10276", cpuOnly ? "cpu" : "vulkan");
            return _preparedRuntimes[index] = await LocalInferenceRuntime.PrepareAsync(
                archive, DownloadFiles[1].Sha256, directory, cpuOnly, token).ConfigureAwait(false);
        }
        finally { _installationLock.Release(); }
    }

    public string ModelPath => Path.Combine(
        InstallationDirectory,
        DownloadFiles[0].FileName);

    public string? ExecutablePath
    {
        get
        {
            var runtimeDirectory = Path.Combine(InstallationDirectory, "runtime");
            return Directory.Exists(runtimeDirectory)
                ? Directory.EnumerateFiles(
                        runtimeDirectory,
                        "llama-server.exe",
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
                           (runtimeInstalled || HasBundledRuntime ? 0 : DownloadFiles[1].Size);
        return new LocalLargeTranslationModelStatus(
            modelInstalled && runtimeInstalled,
            InstallationDirectory,
            downloadSize,
            DownloadFiles.Sum(file => file.Size) + 99_285_856,
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
            await ImportBundledRuntimeAsync(progress, cancellationToken).ConfigureAwait(false);
            await downloader.DownloadAsync(
                DownloadFiles,
                InstallationDirectory,
                progress,
                cancellationToken).ConfigureAwait(false);
            // Also repairs an existing extraction with a missing/corrupt DLL.
            await Task.Run(ExtractRuntime, cancellationToken).ConfigureAwait(false);

            if (ExecutablePath is null)
            {
                return (false, "翻译推理程序解压后不完整，请重新下载。");
            }

            await Task.Run(() => CleanLegacyModelCoreAsync(cancellationToken), cancellationToken);
            return (true, null);
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

    private bool HasBundledRuntime => File.Exists(BundledRuntimeArchivePath) &&
        new FileInfo(BundledRuntimeArchivePath).Length == DownloadFiles[1].Size;

    private async Task ImportBundledRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!HasBundledRuntime) return;
        if (ResumableModelDownloader.IsFileComplete(InstallationDirectory, DownloadFiles[1]) &&
            await ResumableModelDownloader.HasExpectedHashAsync(Path.Combine(InstallationDirectory, RuntimeArchiveName),
                DownloadFiles[1].Sha256, cancellationToken).ConfigureAwait(false)) return;
        await ResumableModelDownloader.ImportAsync(DownloadFiles, InstallationDirectory, [BundledRuntimeArchivePath],
            progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? ErrorMessage)> ImportAsync(
        IReadOnlyList<string> filePaths, IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResumableModelDownloader.ImportAsync(DownloadFiles, InstallationDirectory, filePaths,
                progress, cancellationToken).ConfigureAwait(false);
            await ImportBundledRuntimeAsync(progress, cancellationToken).ConfigureAwait(false);
            if (ResumableModelDownloader.IsFileComplete(InstallationDirectory, DownloadFiles[1]) &&
                await ResumableModelDownloader.HasExpectedHashAsync(Path.Combine(InstallationDirectory, RuntimeArchiveName),
                    DownloadFiles[1].Sha256, cancellationToken).ConfigureAwait(false))
                await Task.Run(ExtractRuntime, cancellationToken).ConfigureAwait(false);
            if (!GetStatus().IsInstalled)
                return (true, "本地文件已导入；还缺少模型或推理程序。可以继续导入，或点击下载，仅补齐缺失文件。");
            if (!await ValidateReplacementAsync(cancellationToken).ConfigureAwait(false))
                return (false, "现有模型或推理程序校验失败，请重新导入完整文件或下载修复。");
            await CleanLegacyModelCoreAsync(cancellationToken).ConfigureAwait(false);
            return (true, $"{DisplayName} 本地导入完成，可完全离线翻译。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "已取消本地模型导入。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (false, exception.Message);
        }
        finally { _installationLock.Release(); }
    }

    internal async Task<long> CleanupLegacyModelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CleanLegacyModelCoreAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _installationLock.Release();
        }
    }

    private Task<long> CleanLegacyModelCoreAsync(CancellationToken cancellationToken) =>
        LegacyTranslationModelCleanup.TryCleanAsync(
            InstallationDirectory, ValidateReplacementAsync, cancellationToken);

    private async Task<bool> ValidateReplacementAsync(CancellationToken cancellationToken)
    {
        if (!GetStatus().IsInstalled) return false;
        // Size alone (also used by the download-resume fast path) does not prove
        // that an existing model is intact. Hash before removing the fallback.
        foreach (var file in DownloadFiles)
        {
            var path = Path.Combine(InstallationDirectory, file.FileName);
            if (!ResumableModelDownloader.IsFileComplete(InstallationDirectory, file) ||
                !await ResumableModelDownloader.HasExpectedHashAsync(
                    path, file.Sha256, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        // Verify the extracted runtime against the authenticated archive too:
        // a lone llama-server.exe is insufficient when a DLL is missing/corrupt.
        var runtimeRoot = Path.GetFullPath(Path.Combine(InstallationDirectory, "runtime")) +
                          Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(Path.Combine(InstallationDirectory, RuntimeArchiveName));
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = Path.GetFullPath(Path.Combine(runtimeRoot, entry.FullName));
            if (!path.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) || new FileInfo(path).Length != entry.Length)
            {
                return false;
            }

            await using var source = entry.Open();
            var expectedHash = Convert.ToHexString(
                await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            if (!await ResumableModelDownloader.HasExpectedHashAsync(
                    path, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
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
        if (_inferenceSession.IsValueCreated)
        {
            _inferenceSession.Value.Dispose();
        }
        _installationLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
