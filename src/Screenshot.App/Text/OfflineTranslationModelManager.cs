using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed record OfflineTranslationModelStatus(
    bool IsInstalled,
    string InstallationDirectory,
    long DownloadSize,
    long InstalledSize,
    long AvailableSpace,
    string? ErrorMessage = null);

public sealed record OfflineTranslationDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    string CurrentFileName);

public sealed record OfflineTranslationInstallationResult(
    bool IsSuccess,
    string? ErrorMessage)
{
    public static OfflineTranslationInstallationResult Failure(string message) =>
        new(false, message);
}

public sealed class OfflineTranslationModelManager : IDisposable
{
    private const string CompletionMarkerFileName = "pack.json";
    private const string ConfigurationFileName = "config.yml";
    private const string MultilingualDirectoryName = "Bergamot-Multilingual";
    private static readonly Lazy<OfflineTranslationModelManager> SharedManager =
        new(() => new OfflineTranslationModelManager());
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly MozillaOfflineTranslationCatalogService _catalogService;
    private readonly SemaphoreSlim _installationLock = new(1, 1);
    private bool _disposed;

    public OfflineTranslationModelManager(
        string? installationDirectory = null,
        HttpClient? httpClient = null)
    {
        InstallationDirectory = Path.GetFullPath(
            installationDirectory ?? AppMetadata.TranslationModelsDirectoryPath);
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
        _ownsHttpClient = httpClient is null;
        _catalogService = new MozillaOfflineTranslationCatalogService(_httpClient);
    }

    public static OfflineTranslationModelManager Shared => SharedManager.Value;

    public string InstallationDirectory { get; }

    internal string ModelsDirectory => Path.Combine(
        InstallationDirectory,
        MultilingualDirectoryName);

    internal Task<OfflineTranslationModelPlanResult> PreparePlanAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _catalogService.CreatePlanAsync(
            sourceLanguage,
            targetLanguage,
            cancellationToken);
    }

    internal Task<OfflineTranslationModelPlanResult> PrepareTargetPlanAsync(
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _catalogService.CreateTargetPlanAsync(
            targetLanguage,
            cancellationToken);
    }

    internal OfflineTranslationModelStatus GetStatus(
        OfflineTranslationModelPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var missingDirections = plan.Directions
            .Where(direction => !IsDirectionInstalled(direction))
            .ToArray();
        return new OfflineTranslationModelStatus(
            missingDirections.Length == 0,
            InstallationDirectory,
            missingDirections.Sum(direction =>
                direction.Files.Sum(file => file.DownloadSize)),
            missingDirections.Sum(direction =>
                direction.Files.Sum(file => file.InstalledSize)),
            GetAvailableSpace());
    }

    internal IReadOnlyList<string>? GetInstalledRoute(
        string sourceLanguage,
        string targetLanguage)
    {
        var sourceCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            sourceLanguage);
        var targetCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        if (sourceCode is null || targetCode is null)
        {
            return null;
        }

        if (string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var directionId in TranslationLanguageCatalog.BuildRoute(
                     sourceCode,
                     targetCode))
        {
            var path = GetConfigurationPath(directionId);
            if (path is null)
            {
                return null;
            }

            paths.Add(path);
        }

        return paths;
    }

    internal string? GetConfigurationPath(string directionId)
    {
        if (!IsSafeDirectionId(directionId))
        {
            return null;
        }

        var directionDirectory = Path.Combine(ModelsDirectory, directionId);
        var markerPath = Path.Combine(directionDirectory, CompletionMarkerFileName);
        var configurationPath = Path.Combine(
            directionDirectory,
            ConfigurationFileName);
        return File.Exists(markerPath) && File.Exists(configurationPath)
            ? configurationPath
            : null;
    }

    internal async Task<OfflineTranslationInstallationResult> InstallAsync(
        OfflineTranslationModelPlan plan,
        IProgress<OfflineTranslationDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installationLock.WaitAsync(cancellationToken);
        try
        {
            var missingDirections = plan.Directions
                .Where(direction => !IsDirectionInstalled(direction))
                .ToArray();
            if (missingDirections.Length == 0)
            {
                return new OfflineTranslationInstallationResult(true, null);
            }

            var requiredDownloadSize = missingDirections.Sum(direction =>
                direction.Files.Sum(file => file.DownloadSize));
            var requiredInstalledSize = missingDirections.Sum(direction =>
                direction.Files.Sum(file => file.InstalledSize));
            if (GetAvailableSpace() < requiredInstalledSize + (64L * 1024 * 1024))
            {
                return OfflineTranslationInstallationResult.Failure(
                    "安装目录所在磁盘空间不足，请释放空间后重试。");
            }

            if (string.IsNullOrWhiteSpace(plan.BaseUrl))
            {
                return OfflineTranslationInstallationResult.Failure(
                    "离线模型下载地址无效，请重新读取模型信息。");
            }

            Directory.CreateDirectory(ModelsDirectory);
            long downloadedBytes = 0;
            foreach (var direction in missingDirections)
            {
                await InstallDirectionAsync(
                    plan.BaseUrl,
                    direction,
                    downloadedBytes,
                    requiredDownloadSize,
                    progress,
                    cancellationToken);
                downloadedBytes += direction.Files.Sum(file => file.DownloadSize);
            }

            progress?.Report(new OfflineTranslationDownloadProgress(
                requiredDownloadSize,
                requiredDownloadSize,
                "安装完成"));
            return new OfflineTranslationInstallationResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return OfflineTranslationInstallationResult.Failure(
                "已取消离线模型下载。");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                HttpRequestException or InvalidDataException or
                CryptographicException)
        {
            return OfflineTranslationInstallationResult.Failure(
                $"离线模型安装失败：{exception.Message}");
        }
        finally
        {
            _installationLock.Release();
        }
    }

    private async Task InstallDirectionAsync(
        string baseUrl,
        OfflineTranslationDirection direction,
        long previouslyDownloaded,
        long totalDownloadSize,
        IProgress<OfflineTranslationDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSafeDirectionId(direction.Id))
        {
            throw new InvalidDataException("离线模型方向标识无效。");
        }

        var stagingDirectory = Path.Combine(
            ModelsDirectory,
            $".{direction.Id}.{Guid.NewGuid():N}.download");
        EnsurePathIsInsideInstallationDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var directionDownloaded = 0L;
            foreach (var file in direction.Files)
            {
                await DownloadAndExtractAsync(
                    baseUrl,
                    file,
                    stagingDirectory,
                    previouslyDownloaded + directionDownloaded,
                    totalDownloadSize,
                    progress,
                    cancellationToken);
                directionDownloaded += file.DownloadSize;
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, ConfigurationFileName),
                direction.Configuration,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, CompletionMarkerFileName),
                JsonSerializer.Serialize(new
                {
                    version = direction.Version,
                    direction = direction.Id,
                    installedAt = DateTimeOffset.UtcNow,
                    source = "Mozilla Firefox Translations model registry",
                    files = direction.Files.Select(file => new
                    {
                        name = file.InstalledFileName,
                        size = file.InstalledSize,
                    }),
                }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            ReplaceDirectionDirectory(direction.Id, stagingDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                EnsurePathIsInsideInstallationDirectory(stagingDirectory);
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private bool IsDirectionInstalled(OfflineTranslationDirection direction)
    {
        if (!IsSafeDirectionId(direction.Id))
        {
            return false;
        }

        var directionDirectory = Path.Combine(ModelsDirectory, direction.Id);
        var markerPath = Path.Combine(directionDirectory, CompletionMarkerFileName);
        var configurationPath = Path.Combine(
            directionDirectory,
            ConfigurationFileName);
        if (!File.Exists(markerPath) || !File.Exists(configurationPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            if (!document.RootElement.TryGetProperty("version", out var version) ||
                !string.Equals(
                    version.GetString(),
                    direction.Version,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    File.ReadAllText(configurationPath),
                    direction.Configuration,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return direction.Files.All(file =>
            {
                var path = Path.Combine(
                    directionDirectory,
                    file.InstalledFileName);
                return File.Exists(path) &&
                       new FileInfo(path).Length == file.InstalledSize;
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task DownloadAndExtractAsync(
        string baseUrl,
        OfflineTranslationModelFile file,
        string directionDirectory,
        long previouslyDownloaded,
        long totalDownloadSize,
        IProgress<OfflineTranslationDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(new Uri(baseUrl), file.DownloadPath);
        using var response = await _httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != file.DownloadSize)
        {
            throw new InvalidDataException(
                $"{file.InstalledFileName} 的下载大小与清单不一致。");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var compressedHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var progressStream = new DownloadProgressStream(
            responseStream,
            bytesRead => progress?.Report(new OfflineTranslationDownloadProgress(
                previouslyDownloaded + bytesRead,
                totalDownloadSize,
                file.InstalledFileName)),
            compressedHash);
        await using var gzipStream = new GZipStream(
            progressStream,
            CompressionMode.Decompress);
        var destinationPath = Path.Combine(
            directionDirectory,
            file.InstalledFileName);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        using var installedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long installedBytes = 0;
        while (true)
        {
            var bytesRead = await gzipStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
            installedHash.AppendData(buffer, 0, bytesRead);
            installedBytes += bytesRead;
        }

        await destination.FlushAsync(cancellationToken);
        var compressedMd5 = Convert.ToHexString(compressedHash.GetHashAndReset());
        var installedSha256 = Convert.ToHexString(installedHash.GetHashAndReset());
        if (progressStream.BytesRead != file.DownloadSize ||
            installedBytes != file.InstalledSize ||
            (file.DownloadMd5 is not null &&
             !string.Equals(
                 compressedMd5,
                 file.DownloadMd5,
                 StringComparison.OrdinalIgnoreCase)) ||
            (file.InstalledSha256 is not null &&
             !string.Equals(
                 installedSha256,
                 file.InstalledSha256,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"{file.InstalledFileName} 未通过完整性校验。");
        }
    }

    private void ReplaceDirectionDirectory(
        string directionId,
        string stagingDirectory)
    {
        var finalDirectory = Path.Combine(ModelsDirectory, directionId);
        EnsurePathIsInsideInstallationDirectory(finalDirectory);
        if (!Directory.Exists(finalDirectory))
        {
            Directory.Move(stagingDirectory, finalDirectory);
            return;
        }

        var backupDirectory = Path.Combine(
            ModelsDirectory,
            $".{directionId}.{Guid.NewGuid():N}.old");
        EnsurePathIsInsideInstallationDirectory(backupDirectory);
        Directory.Move(finalDirectory, backupDirectory);
        try
        {
            Directory.Move(stagingDirectory, finalDirectory);
            Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(finalDirectory) &&
                Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, finalDirectory);
            }

            throw;
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
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private void EnsurePathIsInsideInstallationDirectory(string path)
    {
        var root = InstallationDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("模型路径超出了安装目录。");
        }
    }

    private static bool IsSafeDirectionId(string directionId)
    {
        return !string.IsNullOrWhiteSpace(directionId) &&
               directionId.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '_');
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _installationLock.Dispose();
        _catalogService.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

file sealed class DownloadProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _progress;
    private readonly IncrementalHash? _hash;

    public DownloadProgressStream(
        Stream inner,
        Action<long> progress,
        IncrementalHash? hash = null)
    {
        _inner = inner;
        _progress = progress;
        _hash = hash;
    }

    public long BytesRead { get; private set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Report(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Report(buffer.Span[..read]);
        return read;
    }

    private void Report(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        _hash?.AppendData(bytes);
        BytesRead += bytes.Length;
        _progress(BytesRead);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
