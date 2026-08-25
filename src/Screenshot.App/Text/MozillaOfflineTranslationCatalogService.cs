using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Screenshot.App.Text;

internal sealed record OfflineTranslationModelPlan(
    string SourceCode,
    string TargetCode,
    string DisplayName,
    string BaseUrl,
    IReadOnlyList<OfflineTranslationDirection> Directions,
    IReadOnlyList<string>? DownloadBaseUrls = null)
{
    public long DownloadSize => Directions.Sum(direction =>
        direction.Files.Sum(file => file.DownloadSize));

    public long InstalledSize => Directions.Sum(direction =>
        direction.Files.Sum(file => file.InstalledSize));
}

internal sealed record OfflineTranslationModelPlanResult(
    OfflineTranslationModelPlan? Plan,
    string? ErrorMessage)
{
    public bool IsSuccess => Plan is not null;

    public static OfflineTranslationModelPlanResult Failure(string message) =>
        new(null, message);
}

internal sealed class MozillaOfflineTranslationCatalogService : IDisposable
{
    private const int MaximumRegistryAttempts = 3;
    private const string RegistryCacheFileName = "mozilla-model-registry-cache.json";
    private const string StorageBucketName =
        "moz-fx-translations-data--303e-prod-translations-data";
    internal static readonly Uri RegistryUri = new(
        "https://storage.googleapis.com/" +
        StorageBucketName + "/db/models.json");
    private static readonly Uri VirtualHostedRegistryUri = new(
        "https://" + StorageBucketName +
        ".storage.googleapis.com/db/models.json");
    private static readonly Uri GoogleApiRegistryUri = new(
        "https://www.googleapis.com/download/storage/v1/b/" +
        StorageBucketName + "/o/db%2Fmodels.json?alt=media");
    private static readonly string[] DownloadBaseUrls =
    [
        "https://storage.googleapis.com/" + StorageBucketName + "/",
        "https://" + StorageBucketName + ".storage.googleapis.com/",
    ];

    private readonly HttpClient _httpClient;
    private readonly string? _registryCachePath;
    private readonly object _snapshotLock = new();
    private readonly object _targetPlanLock = new();
    private readonly Dictionary<string, Task<OfflineTranslationModelPlanResult>>
        _targetPlanTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _directionMetadataGate = new(8, 8);
    private Task<RegistrySnapshot>? _snapshotTask;

    public MozillaOfflineTranslationCatalogService(
        HttpClient httpClient,
        string? registryCacheDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _registryCachePath = string.IsNullOrWhiteSpace(registryCacheDirectory)
            ? null
            : Path.Combine(registryCacheDirectory, RegistryCacheFileName);
    }

    public async Task<OfflineTranslationModelPlanResult> CreatePlanAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var sourceCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            sourceLanguage);
        var targetCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        if (sourceCode is null)
        {
            return OfflineTranslationModelPlanResult.Failure(
                "无法确定源语言，请先在“文字识别”设置中选择语言。");
        }

        if (targetCode is null)
        {
            return OfflineTranslationModelPlanResult.Failure("请选择目标语言。");
        }

        var displayName =
            $"{TranslationLanguageCatalog.GetDisplayName(sourceLanguage)} → " +
            TranslationLanguageCatalog.GetDisplayName(targetLanguage);
        if (string.Equals(sourceCode, targetCode, StringComparison.OrdinalIgnoreCase))
        {
            return new OfflineTranslationModelPlanResult(
                new OfflineTranslationModelPlan(
                    sourceCode,
                    targetCode,
                    displayName,
                    string.Empty,
                    [],
                    []),
                null);
        }

        try
        {
            var snapshot = await GetSnapshotAsync(cancellationToken);
            var directionIds = TranslationLanguageCatalog.BuildRoute(
                sourceCode,
                targetCode);
            var directions = new List<OfflineTranslationDirection>(
                directionIds.Count);
            foreach (var directionId in directionIds)
            {
                if (!snapshot.Models.TryGetProperty(
                        directionId,
                        out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array)
                {
                    return OfflineTranslationModelPlanResult.Failure(
                        $"Mozilla 暂未提供 {displayName} 所需的离线模型（缺少 {directionId}）。");
                }

                var candidate = SelectBestCandidate(candidates);
                if (candidate is null)
                {
                    return OfflineTranslationModelPlanResult.Failure(
                        $"Mozilla 暂未提供可用的 {directionId} 离线模型。");
                }

                directions.Add(await CreateDirectionAsync(
                    snapshot.BaseUrl,
                    directionId,
                    candidate.Value,
                    cancellationToken));
            }

            return new OfflineTranslationModelPlanResult(
                new OfflineTranslationModelPlan(
                    sourceCode,
                    targetCode,
                    displayName,
                    snapshot.BaseUrl,
                    directions,
                    snapshot.DownloadBaseUrls),
                null);
        }
        catch (OperationCanceledException)
        {
            return OfflineTranslationModelPlanResult.Failure(
                "已取消离线模型信息查询。");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or
                InvalidDataException)
        {
            return OfflineTranslationModelPlanResult.Failure(
                $"无法读取 Mozilla 离线模型清单：{DescribeCatalogReadFailure(exception)}");
        }
    }

    public async Task<OfflineTranslationModelPlanResult> CreateTargetPlanAsync(
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var targetCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        if (targetCode is null)
        {
            return OfflineTranslationModelPlanResult.Failure("请选择目标语言。");
        }

        Task<OfflineTranslationModelPlanResult> task;
        lock (_targetPlanLock)
        {
            if (!_targetPlanTasks.TryGetValue(targetCode, out task!))
            {
                task = CreateTargetPlanCoreAsync(targetCode);
                _targetPlanTasks[targetCode] = task;
            }
        }

        try
        {
            var result = await task.WaitAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                RemoveTargetPlanTask(targetCode, task);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return OfflineTranslationModelPlanResult.Failure(
                "已取消离线模型信息查询。");
        }
        catch
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                RemoveTargetPlanTask(targetCode, task);
            }

            throw;
        }
    }

    private async Task<OfflineTranslationModelPlanResult> CreateTargetPlanCoreAsync(
        string targetCode)
    {
        try
        {
            var snapshot = await GetSnapshotAsync(CancellationToken.None);
            var directionIds = TranslationLanguageCatalog
                .BuildAutoDetectPackDirections(targetCode);
            var directionTasks = directionIds.Select(directionId =>
                CreateDirectionFromSnapshotAsync(
                    snapshot,
                    directionId,
                    CancellationToken.None));
            var directions = await Task.WhenAll(directionTasks);
            var displayName =
                $"自动检测（{TranslationLanguageCatalog.OfflineSourceCodes.Count} 种源语言） → " +
                TranslationLanguageCatalog.GetDisplayName(targetCode);
            return new OfflineTranslationModelPlanResult(
                new OfflineTranslationModelPlan(
                    "auto",
                    targetCode,
                    displayName,
                    snapshot.BaseUrl,
                    directions,
                    snapshot.DownloadBaseUrls),
                null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or
                InvalidDataException)
        {
            return OfflineTranslationModelPlanResult.Failure(
                $"无法读取 Mozilla 离线模型清单：{DescribeCatalogReadFailure(exception)}");
        }
    }

    private async Task<OfflineTranslationDirection> CreateDirectionFromSnapshotAsync(
        RegistrySnapshot snapshot,
        string directionId,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Models.TryGetProperty(directionId, out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Mozilla 暂未提供 {directionId} 离线模型。");
        }

        var candidate = SelectBestCandidate(candidates) ??
            throw new InvalidDataException(
                $"Mozilla 暂未提供可用的 {directionId} 离线模型。");
        await _directionMetadataGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateDirectionAsync(
                snapshot.BaseUrl,
                directionId,
                candidate,
                cancellationToken);
        }
        finally
        {
            _directionMetadataGate.Release();
        }
    }

    private void RemoveTargetPlanTask(
        string targetCode,
        Task<OfflineTranslationModelPlanResult> task)
    {
        lock (_targetPlanLock)
        {
            if (_targetPlanTasks.TryGetValue(targetCode, out var current) &&
                ReferenceEquals(current, task))
            {
                _targetPlanTasks.Remove(targetCode);
            }
        }
    }

    private async Task<RegistrySnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        Task<RegistrySnapshot> task;
        lock (_snapshotLock)
        {
            task = _snapshotTask ??= LoadSnapshotAsync();
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                lock (_snapshotLock)
                {
                    if (ReferenceEquals(_snapshotTask, task))
                    {
                        _snapshotTask = null;
                    }
                }
            }

            throw;
        }
    }

    private async Task<RegistrySnapshot> LoadSnapshotAsync()
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumRegistryAttempts; attempt++)
        {
            foreach (var registryUri in GetRegistryUrisForAttempt(attempt))
            {
                try
                {
                    var snapshot = await LoadSnapshotOnceAsync(registryUri);
                    await TryWriteSnapshotCacheAsync(snapshot);
                    return snapshot;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or JsonException or
                        InvalidDataException)
                {
                    lastError = exception;
                }
            }

            if (attempt < MaximumRegistryAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }

        var cachedSnapshot = await TryReadSnapshotCacheAsync();
        if (cachedSnapshot is not null)
        {
            return cachedSnapshot;
        }

        throw lastError ?? new HttpRequestException("无法连接 Mozilla 模型清单服务器。");
    }

    private static IEnumerable<Uri> GetRegistryUrisForAttempt(int attempt)
    {
        // Rotate the preferred endpoint so a temporary failure on one official
        // Google endpoint does not make every retry repeat the same TLS path.
        var registryUris = new[]
        {
            RegistryUri,
            VirtualHostedRegistryUri,
            GoogleApiRegistryUri,
        };
        var offset = (attempt - 1) % registryUris.Length;
        for (var index = 0; index < registryUris.Length; index++)
        {
            yield return registryUris[(offset + index) % registryUris.Length];
        }
    }

    private async Task<RegistrySnapshot> LoadSnapshotOnceAsync(Uri registryUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, registryUri)
        {
            // Some enterprise proxies fail during HTTP/2 TLS negotiation with
            // Google Cloud Storage. The registry is a small static document,
            // so HTTP/1.1 is the most compatible transport here.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        return ParseSnapshot(payload, registryUri);
    }

    private static RegistrySnapshot ParseSnapshot(string payload, Uri registryUri)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var baseUrl = root.GetProperty("baseUrl").GetString();
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !root.TryGetProperty("models", out var models))
        {
            throw new InvalidDataException("Mozilla 模型清单缺少必要字段。");
        }

        var preferredBaseUrl = GetPreferredDownloadBaseUrl(registryUri, baseUrl);
        var downloadBaseUrls = new[] { preferredBaseUrl }
            .Concat(DownloadBaseUrls.Where(baseUrl => !string.Equals(
                baseUrl,
                preferredBaseUrl,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new RegistrySnapshot(
            preferredBaseUrl,
            downloadBaseUrls,
            payload,
            models.Clone());
    }

    private static string GetPreferredDownloadBaseUrl(
        Uri registryUri,
        string manifestBaseUrl)
    {
        if (string.Equals(
                registryUri.Host,
                VirtualHostedRegistryUri.Host,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                registryUri.Host,
                GoogleApiRegistryUri.Host,
                StringComparison.OrdinalIgnoreCase))
        {
            return DownloadBaseUrls[1];
        }

        return NormalizeBaseUrl(manifestBaseUrl);
    }

    private static string NormalizeBaseUrl(string baseUrl) =>
        baseUrl.TrimEnd('/') + "/";

    private async Task TryWriteSnapshotCacheAsync(RegistrySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_registryCachePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_registryCachePath)!);
            var cache = new CachedRegistrySnapshot(
                snapshot.BaseUrl,
                snapshot.DownloadBaseUrls,
                snapshot.Payload);
            await File.WriteAllTextAsync(
                _registryCachePath,
                JsonSerializer.Serialize(cache));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A cache miss only affects resilience. The current trusted
            // network response remains usable when its directory is read-only.
        }
    }

    private async Task<RegistrySnapshot?> TryReadSnapshotCacheAsync()
    {
        if (string.IsNullOrWhiteSpace(_registryCachePath) ||
            !File.Exists(_registryCachePath))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(_registryCachePath);
            var cache = JsonSerializer.Deserialize<CachedRegistrySnapshot>(content);
            if (cache is null || string.IsNullOrWhiteSpace(cache.Payload) ||
                string.IsNullOrWhiteSpace(cache.BaseUrl) ||
                cache.DownloadBaseUrls is not { Count: > 0 })
            {
                return null;
            }

            var snapshot = ParseSnapshot(cache.Payload, RegistryUri);
            return snapshot with
            {
                BaseUrl = NormalizeBaseUrl(cache.BaseUrl),
                DownloadBaseUrls = cache.DownloadBaseUrls
                    .Where(baseUrl => Uri.TryCreate(
                        baseUrl,
                        UriKind.Absolute,
                        out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    .Select(NormalizeBaseUrl)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string DescribeCatalogReadFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return "无法建立安全连接。请检查 Windows 日期时间、根证书和公司代理/安全软件；" +
                       "该网络未能安全访问 Mozilla 模型服务器。";
            }
        }

        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return string.IsNullOrWhiteSpace(root.Message)
            ? exception.Message
            : root.Message;
    }

    private static JsonElement? SelectBestCandidate(JsonElement candidates)
    {
        return candidates.EnumerateArray()
            .OrderBy(GetReleasePriority)
            .ThenBy(GetArchitecturePriority)
            .Select(candidate => (JsonElement?)candidate.Clone())
            .FirstOrDefault();
    }

    private static int GetReleasePriority(JsonElement candidate)
    {
        var releaseStatus = candidate.TryGetProperty(
            "releaseStatus",
            out var statusElement)
            ? statusElement.GetString()
            : null;
        return releaseStatus switch
        {
            "Release Desktop" => 0,
            "Release" => 1,
            "Release Android" => 2,
            not null when releaseStatus.StartsWith(
                "Release",
                StringComparison.OrdinalIgnoreCase) => 3,
            "Nightly" => 4,
            _ => 5,
        };
    }

    private static int GetArchitecturePriority(JsonElement candidate)
    {
        var architecture = candidate.TryGetProperty(
            "architecture",
            out var architectureElement)
            ? architectureElement.GetString()
            : null;
        return architecture switch
        {
            "base-memory" => 0,
            "base" => 1,
            "tiny" => 2,
            _ => 3,
        };
    }

    private async Task<OfflineTranslationDirection> CreateDirectionAsync(
        string baseUrl,
        string directionId,
        JsonElement candidate,
        CancellationToken cancellationToken)
    {
        if (!candidate.TryGetProperty("files", out var files))
        {
            throw new InvalidDataException($"{directionId} 模型没有文件清单。");
        }

        var modelNode = files.GetProperty("model");
        var shortlistNode = files.TryGetProperty(
            "lexicalShortlist",
            out var shortlist)
            ? shortlist
            : (JsonElement?)null;
        JsonElement sourceVocabNode;
        JsonElement targetVocabNode;
        if (files.TryGetProperty("vocab", out var sharedVocab))
        {
            sourceVocabNode = sharedVocab;
            targetVocabNode = sharedVocab;
        }
        else
        {
            sourceVocabNode = files.GetProperty("srcVocab");
            targetVocabNode = files.GetProperty("trgVocab");
        }

        var nodes = new List<JsonElement> { modelNode, sourceVocabNode };
        if (!string.Equals(
                GetPath(sourceVocabNode),
                GetPath(targetVocabNode),
                StringComparison.Ordinal))
        {
            nodes.Add(targetVocabNode);
        }

        if (shortlistNode is JsonElement shortlistValue)
        {
            nodes.Add(shortlistValue);
        }

        var metadataTasks = nodes.Select(node => CreateFileAsync(
            baseUrl,
            node,
            cancellationToken));
        var modelFiles = await Task.WhenAll(metadataTasks);
        var modelFileName = GetInstalledFileName(modelNode);
        var sourceVocabFileName = GetInstalledFileName(sourceVocabNode);
        var targetVocabFileName = GetInstalledFileName(targetVocabNode);
        var shortlistFileName = shortlistNode is JsonElement shortlistFile
            ? GetInstalledFileName(shortlistFile)
            : null;
        var configuration = OfflineTranslationModelCatalog.CreateConfiguration(
            modelFileName,
            sourceVocabFileName,
            targetVocabFileName,
            shortlistFileName);
        var versionSource = directionId + "\n" +
                            string.Join(
                                "\n",
                                modelFiles.Select(file =>
                                    $"{file.DownloadPath}|{file.DownloadSize}|" +
                                    $"{file.InstalledSize}|{file.DownloadMd5}|" +
                                    file.InstalledSha256)) +
                            "\n" + configuration;
        var version = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(versionSource)));
        var parts = directionId.Split('-', 2);
        var displayName = parts.Length == 2
            ? $"{TranslationLanguageCatalog.GetDisplayName(parts[0])} → " +
              TranslationLanguageCatalog.GetDisplayName(parts[1])
            : directionId;
        return new OfflineTranslationDirection(
            directionId,
            displayName,
            modelFiles,
            configuration,
            version);
    }

    private async Task<OfflineTranslationModelFile> CreateFileAsync(
        string baseUrl,
        JsonElement fileNode,
        CancellationToken cancellationToken)
    {
        var path = GetPath(fileNode);
        var uri = new Uri(new Uri(baseUrl), path);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
        using var headResponse = await _httpClient.SendAsync(
            headRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        headResponse.EnsureSuccessStatusCode();
        var downloadSize = headResponse.Content.Headers.ContentLength ??
            throw new InvalidDataException($"{path} 没有下载大小。");
        var downloadMd5 = headResponse.Headers.ETag?.Tag.Trim('"');
        if (downloadMd5 is not { Length: 32 })
        {
            downloadMd5 = null;
        }

        var installedSize = fileNode.TryGetProperty(
            "uncompressedSize",
            out var installedSizeElement)
            ? installedSizeElement.GetInt64()
            : await ReadGzipInstalledSizeAsync(uri, cancellationToken);
        var installedSha256 = fileNode.TryGetProperty(
            "uncompressedHash",
            out var hashElement)
            ? hashElement.GetString()
            : null;
        return new OfflineTranslationModelFile(
            path,
            GetInstalledFileName(fileNode),
            downloadSize,
            installedSize,
            installedSha256,
            downloadMd5);
    }

    private async Task<long> ReadGzipInstalledSizeAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(null, 4);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length != 4 || response.Content.Headers.ContentRange is null)
        {
            throw new InvalidDataException($"无法读取 {uri.Segments[^1]} 的解压大小。");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static string GetPath(JsonElement fileNode)
    {
        var path = fileNode.GetProperty("path").GetString();
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidDataException("模型文件路径为空。")
            : path;
    }

    private static string GetInstalledFileName(JsonElement fileNode)
    {
        var fileName = Path.GetFileName(GetPath(fileNode));
        return fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^3]
            : fileName;
    }

    private sealed record RegistrySnapshot(
        string BaseUrl,
        IReadOnlyList<string> DownloadBaseUrls,
        string Payload,
        JsonElement Models);

    private sealed record CachedRegistrySnapshot(
        string BaseUrl,
        IReadOnlyList<string> DownloadBaseUrls,
        string Payload);

    public void Dispose()
    {
        _directionMetadataGate.Dispose();
    }
}
