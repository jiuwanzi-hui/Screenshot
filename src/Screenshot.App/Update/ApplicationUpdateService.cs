using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Screenshot.App.Core;

namespace Screenshot.App.Update;

public enum ApplicationUpdateMirror
{
    Gitee,
    GitHub,
}

public sealed record ApplicationUpdateAsset(
    string FileName,
    Uri GitHubDownloadUri,
    Uri GiteeDownloadUri,
    long Size,
    string Sha256,
    ApplicationUpdateMirror PreferredMirror)
{
    public ApplicationUpdateAsset(
        string fileName,
        Uri downloadUri,
        long size,
        string sha256)
        : this(
            fileName,
            downloadUri,
            downloadUri,
            size,
            sha256,
            ApplicationUpdateMirror.GitHub)
    {
    }

    public IEnumerable<Uri> GetDownloadUris()
    {
        if (PreferredMirror == ApplicationUpdateMirror.Gitee)
        {
            yield return GiteeDownloadUri;
            yield return GitHubDownloadUri;
        }
        else
        {
            yield return GitHubDownloadUri;
            yield return GiteeDownloadUri;
        }
    }
}

public sealed record ApplicationUpdateInfo(
    Version Version,
    Uri ReleasePage,
    ApplicationUpdateAsset Installer,
    ApplicationUpdateAsset Portable,
    ApplicationUpdateMirror PreferredMirror);

public sealed record ApplicationUpdateCheckResult(
    bool IsSuccess,
    ApplicationUpdateInfo? AvailableUpdate,
    string Message);

public sealed record ApplicationReleaseInfo(
    Version Version,
    string Title,
    DateTimeOffset PublishedAt,
    string ReleaseNotes,
    Uri ReleasePage,
    ApplicationUpdateInfo? InstallableUpdate,
    string? PackageWarning);

public sealed record ApplicationReleaseHistoryResult(
    bool IsSuccess,
    IReadOnlyList<ApplicationReleaseInfo> Releases,
    string Message);

public sealed class ApplicationUpdateService : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static readonly Uri DefaultGitHubManifestUri = new(
        "https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/SnapCut-Update.json");
    internal static readonly Uri LegacyGitHubManifestUri = new(
        "https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Update.json");
    internal static readonly Uri DefaultGitHubLatestReleaseUri = new(
        "https://github.com/jiuwanzi-hui/Screenshot/releases/latest");
    internal static readonly Uri DefaultGiteeManifestUri = new(
        "https://gitee.com/wwangyunhui/screenshot/raw/main/updates/SnapCut-Update.json");
    internal static readonly Uri LegacyGiteeManifestUri = new(
        "https://gitee.com/wwangyunhui/screenshot/raw/main/updates/Screenshot-Update.json");
    internal static readonly Uri DefaultGiteeLatestReleaseUri = new(
        "https://gitee.com/wwangyunhui/screenshot/releases/latest");
    internal static readonly Uri DefaultGitHubReleaseHistoryUri = new(
        "https://api.github.com/repos/jiuwanzi-hui/Screenshot/releases?per_page=20");
    internal static readonly Uri DefaultGiteeReleaseHistoryUri = new(
        "https://gitee.com/api/v5/repos/wwangyunhui/screenshot/releases?per_page=20");
    internal static readonly Uri DefaultGitHubStaticReleaseHistoryUri = new(
        "https://raw.githubusercontent.com/jiuwanzi-hui/Screenshot/main/updates/SnapCut-Releases.json");
    internal static readonly Uri DefaultGiteeStaticReleaseHistoryUri = new(
        "https://gitee.com/wwangyunhui/screenshot/raw/main/updates/SnapCut-Releases.json");

    private const long MaximumPackageSize = 500L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<UpdateSource> _sources;
    private readonly string _updatesDirectory;
    private readonly string _releaseHistoryCachePath;
    private readonly string? _bundledReleaseHistoryPath;

    public ApplicationUpdateService(
        HttpClient? httpClient = null,
        Uri? manifestUri = null,
        string? updatesDirectory = null,
        Uri? releaseHistoryUri = null,
        Uri? legacyManifestUri = null,
        Uri? staticReleaseHistoryUri = null,
        string? releaseHistoryCachePath = null,
        string? bundledReleaseHistoryPath = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _ownsHttpClient = httpClient is null;
        _sources = manifestUri is null
            ?
            [
                new UpdateSource(
                    ApplicationUpdateMirror.Gitee,
                    DefaultGiteeManifestUri,
                    LegacyGiteeManifestUri,
                    DefaultGiteeLatestReleaseUri,
                    DefaultGiteeReleaseHistoryUri,
                    DefaultGiteeStaticReleaseHistoryUri),
                new UpdateSource(
                    ApplicationUpdateMirror.GitHub,
                    DefaultGitHubManifestUri,
                    LegacyGitHubManifestUri,
                    DefaultGitHubLatestReleaseUri,
                    DefaultGitHubReleaseHistoryUri,
                    DefaultGitHubStaticReleaseHistoryUri),
            ]
            :
            [
                new UpdateSource(
                    ApplicationUpdateMirror.GitHub,
                    manifestUri,
                    legacyManifestUri,
                    DefaultGitHubLatestReleaseUri,
                    releaseHistoryUri ?? DefaultGitHubReleaseHistoryUri,
                    staticReleaseHistoryUri),
            ];
        _updatesDirectory = updatesDirectory ?? AppMetadata.UpdatesDirectoryPath;
        _releaseHistoryCachePath = releaseHistoryCachePath ?? Path.Combine(
            _updatesDirectory,
            AppMetadata.ReleaseHistoryCacheFileName);
        _bundledReleaseHistoryPath = bundledReleaseHistoryPath ??
            (manifestUri is null
                ? Path.Combine(AppContext.BaseDirectory, "SnapCut-Releases.json")
                : null);
    }

    public async Task<ApplicationUpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        var pending = _sources
            .Select(source => CheckSourceSafelyAsync(
                source,
                currentVersion,
                cancellationToken))
            .ToList();
        var failures = new List<string>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var result = await completed;
            if (result.IsSuccess)
            {
                return result;
            }

            failures.Add(result.Message);
        }

        return new ApplicationUpdateCheckResult(
            false,
            null,
            "Gitee 和 GitHub 更新源均不可用：" +
            string.Join("；", failures.Distinct(StringComparer.Ordinal)));
    }

    public async Task<string> DownloadAsync(
        ApplicationUpdateAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAsset(asset, expectedFileName: asset.FileName);
        Directory.CreateDirectory(_updatesDirectory);
        var destinationPath = Path.Combine(
            _updatesDirectory,
            asset.FileName);
        var failures = new List<string>();
        Exception? lastFailure = null;
        foreach (var downloadUri in asset.GetDownloadUris().Distinct())
        {
            var partialPath = destinationPath + $".{Guid.NewGuid():N}.part";
            try
            {
                await DownloadFromUriAsync(
                    asset,
                    downloadUri,
                    partialPath,
                    progress,
                    cancellationToken);
                File.Move(partialPath, destinationPath, overwrite: true);
                progress?.Report(1);
                return destinationPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(partialPath);
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                TryDeleteFile(partialPath);
                lastFailure = exception;
                failures.Add($"{GetMirrorName(downloadUri)}：{exception.Message}");
            }
        }

        var message = "Gitee 和 GitHub 更新包均下载失败：" + string.Join("；", failures);
        if (lastFailure is InvalidDataException)
        {
            throw new InvalidDataException(message, lastFailure);
        }

        throw new HttpRequestException(message, lastFailure);
    }

    public async Task<ApplicationReleaseHistoryResult> GetReleaseHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var source in _sources.Where(source =>
                     source.StaticReleaseHistoryUri is not null))
        {
            try
            {
                var releaseItems = await ReadReleaseHistoryItemsAsync(
                    source.StaticReleaseHistoryUri!,
                    cancellationToken);
                var releases = await CreateReleaseInfosAsync(
                    source,
                    releaseItems,
                    cancellationToken);
                if (releases.Count > 0)
                {
                    TrySaveReleaseHistoryCache(source.Mirror, releaseItems);
                    return new ApplicationReleaseHistoryResult(
                        true,
                        releases,
                        $"已从 {GetMirrorName(source.Mirror)}加载 {releases.Count} 个正式版本。");
                }

                failures.Add($"{GetMirrorName(source.Mirror)}静态清单：没有可显示的正式版本");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                failures.Add($"{GetMirrorName(source.Mirror)}静态清单：{exception.Message}");
            }
        }

        var localResult = await TryReadBestLocalReleaseHistoryAsync(cancellationToken);
        if (localResult is not null)
        {
            return localResult;
        }

        foreach (var source in _sources)
        {
            try
            {
                var releaseItems = await ReadReleaseHistoryItemsAsync(
                    source.ReleaseHistoryUri,
                    cancellationToken);
                var releases = await CreateReleaseInfosAsync(
                    source,
                    releaseItems,
                    cancellationToken);
                if (releases.Count > 0)
                {
                    TrySaveReleaseHistoryCache(source.Mirror, releaseItems);
                    return new ApplicationReleaseHistoryResult(
                        true,
                        releases,
                        $"已从 {GetMirrorName(source.Mirror)}加载 {releases.Count} 个正式版本。");
                }

                failures.Add($"{GetMirrorName(source.Mirror)}：没有可显示的正式版本");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                failures.Add($"{GetMirrorName(source.Mirror)}：{exception.Message}");
            }
        }

        return new ApplicationReleaseHistoryResult(
            false,
            [],
            "暂时无法读取版本历史：" +
            string.Join("；", failures.Distinct(StringComparer.Ordinal)));
    }

    private async Task DownloadFromUriAsync(
        ApplicationUpdateAsset asset,
        Uri downloadUri,
        string partialPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.UserAgent.ParseAdd($"SnapCut/{AppMetadata.DisplayVersion}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > asset.Size || total > MaximumPackageSize)
            {
                throw new InvalidDataException("更新包大小超过清单声明。");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(Math.Clamp((double)total / asset.Size, 0, 1));
        }

        await destination.FlushAsync(cancellationToken);
        if (total != asset.Size)
        {
            throw new InvalidDataException("更新包下载不完整。");
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新包 SHA-256 校验失败，已停止更新。");
        }
    }

    private async Task<IReadOnlyList<ReleaseApiItem>> ReadReleaseHistoryItemsAsync(
        Uri historyUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, historyUri);
        request.Headers.UserAgent.ParseAdd($"SnapCut/{AppMetadata.DisplayVersion}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<ReleaseApiItem>>(
            stream,
            ManifestJsonOptions,
            cancellationToken) ?? [];
    }

    private async Task<IReadOnlyList<ApplicationReleaseInfo>> CreateReleaseInfosAsync(
        UpdateSource source,
        IReadOnlyList<ReleaseApiItem> releaseItems,
        CancellationToken cancellationToken)
    {
        var candidates = releaseItems
            .Where(item => !item.IsDraft && !item.IsPrerelease)
            .Select(item => new
            {
                Item = item,
                Version = ParseReleaseVersion(item.TagName),
            })
            .Where(candidate => candidate.Version is not null)
            .GroupBy(candidate => ComparableVersion(candidate.Version!))
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.Version)
            .Take(20)
            .ToArray();
        var releases = await Task.WhenAll(candidates.Select(candidate =>
            CreateReleaseInfoAsync(
                source,
                candidate.Item,
                candidate.Version!,
                cancellationToken)));
        return releases
            .OrderByDescending(release => ComparableVersion(release.Version))
            .ToArray();
    }

    private async Task<ApplicationReleaseHistoryResult?> TryReadBestLocalReleaseHistoryAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Name, ReleaseHistoryCacheFile Cache)>();
        var cached = TryReadReleaseHistoryCache();
        if (cached is not null)
        {
            candidates.Add(("本地缓存", cached));
        }

        if (!string.IsNullOrWhiteSpace(_bundledReleaseHistoryPath) &&
            File.Exists(_bundledReleaseHistoryPath))
        {
            try
            {
                await using var stream = File.OpenRead(_bundledReleaseHistoryPath);
                var items = await JsonSerializer.DeserializeAsync<List<ReleaseApiItem>>(
                    stream,
                    ManifestJsonOptions,
                    cancellationToken) ?? [];
                candidates.Add((
                    "程序内置清单",
                    new ReleaseHistoryCacheFile
                    {
                        Mirror = ApplicationUpdateMirror.GitHub,
                        SavedAt = DateTimeOffset.MinValue,
                        Releases = items,
                    }));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        foreach (var candidate in candidates
                     .Where(candidate => candidate.Cache.Releases.Count > 0)
                     .OrderByDescending(candidate => candidate.Cache.Releases
                         .Select(item => ParseReleaseVersion(item.TagName))
                         .Where(version => version is not null)
                         .Select(version => ComparableVersion(version!))
                         .DefaultIfEmpty(new Version(0, 0, 0))
                         .Max()))
        {
            var source = _sources.FirstOrDefault(source =>
                    source.Mirror == candidate.Cache.Mirror) ??
                _sources[0];
            var releases = await CreateReleaseInfosAsync(
                source,
                candidate.Cache.Releases,
                cancellationToken);
            if (releases.Count > 0)
            {
                return new ApplicationReleaseHistoryResult(
                    true,
                    releases,
                    $"已从{candidate.Name}加载 {releases.Count} 个正式版本。在线清单暂不可用。");
            }
        }

        return null;
    }

    private ReleaseHistoryCacheFile? TryReadReleaseHistoryCache()
    {
        try
        {
            if (!File.Exists(_releaseHistoryCachePath))
            {
                return null;
            }

            using var stream = File.OpenRead(_releaseHistoryCachePath);
            return JsonSerializer.Deserialize<ReleaseHistoryCacheFile>(
                stream,
                ManifestJsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void TrySaveReleaseHistoryCache(
        ApplicationUpdateMirror mirror,
        IReadOnlyList<ReleaseApiItem> releaseItems)
    {
        if (releaseItems.Count == 0)
        {
            return;
        }

        var temporaryPath = _releaseHistoryCachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(_releaseHistoryCachePath) ?? _updatesDirectory);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    new ReleaseHistoryCacheFile
                    {
                        Mirror = mirror,
                        SavedAt = DateTimeOffset.UtcNow,
                        Releases = releaseItems.ToList(),
                    },
                    ManifestJsonOptions);
            }

            File.Move(temporaryPath, _releaseHistoryCachePath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<ApplicationReleaseInfo> CreateReleaseInfoAsync(
        UpdateSource source,
        ReleaseApiItem release,
        Version version,
        CancellationToken cancellationToken)
    {
        var tagName = release.TagName!.Trim();
        var releasePage = CreateReleasePageUri(
            source.Mirror,
            tagName,
            release.HtmlUrl);
        var publishedAt = release.PublishedAt ?? release.CreatedAt ?? DateTimeOffset.MinValue;
        var title = string.IsNullOrWhiteSpace(release.Name)
            ? $"SnapCut {NormalizeVersion(version)}"
            : release.Name.Trim();
        var releaseNotes = FormatReleaseNotes(release.Body);
        ApplicationUpdateInfo? installableUpdate = null;
        string? packageWarning = null;
        var manifestAssets = release.Assets.Where(asset =>
                string.Equals(
                    asset.Name,
                    "SnapCut-Update.json",
                    StringComparison.OrdinalIgnoreCase))
            .Concat(release.Assets.Where(asset =>
                string.Equals(
                    asset.Name,
                    "Screenshot-Update.json",
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (manifestAssets.Length == 0)
        {
            packageWarning = "此版本缺少可验证的在线更新清单，只能查看更新说明。";
        }
        else
        {
            var manifestFailures = new List<string>();
            foreach (var manifestAsset in manifestAssets)
            {
                var manifestUri = CreateReleaseDownloadUri(
                    source.Mirror,
                    tagName,
                    manifestAsset.Name!);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
                    request.Headers.UserAgent.ParseAdd($"SnapCut/{AppMetadata.DisplayVersion}");
                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
                        stream,
                        ManifestJsonOptions,
                        cancellationToken);
                    var validated = ValidateManifest(manifest, source.Mirror);
                    if (ComparableVersion(validated.Version) != ComparableVersion(version))
                    {
                        throw new InvalidDataException("Release 标签与更新清单版本不一致。");
                    }

                    EnsureReleaseContainsAsset(release, validated.Installer.FileName);
                    EnsureReleaseContainsAsset(release, validated.Portable.FileName);
                    installableUpdate = new ApplicationUpdateInfo(
                        version,
                        releasePage,
                        CreateHistoricalAsset(
                            validated.Installer,
                            tagName,
                            source.Mirror),
                        CreateHistoricalAsset(
                            validated.Portable,
                            tagName,
                            source.Mirror),
                        source.Mirror);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
                {
                    manifestFailures.Add(exception.Message);
                }
            }

            if (installableUpdate is null)
            {
                packageWarning = "此版本暂不能一键安装：" +
                    string.Join("；", manifestFailures.Distinct(StringComparer.Ordinal));
            }
        }

        return new ApplicationReleaseInfo(
            version,
            title,
            publishedAt,
            releaseNotes,
            releasePage,
            installableUpdate,
            packageWarning);
    }

    private static ApplicationUpdateAsset CreateHistoricalAsset(
        ApplicationUpdateAsset validatedAsset,
        string tagName,
        ApplicationUpdateMirror preferredMirror)
    {
        var asset = new ApplicationUpdateAsset(
            validatedAsset.FileName,
            CreateReleaseDownloadUri(
                ApplicationUpdateMirror.GitHub,
                tagName,
                validatedAsset.FileName),
            CreateReleaseDownloadUri(
                ApplicationUpdateMirror.Gitee,
                tagName,
                validatedAsset.FileName),
            validatedAsset.Size,
            validatedAsset.Sha256,
            preferredMirror);
        ValidateAsset(asset, validatedAsset.FileName);
        return asset;
    }

    private static void EnsureReleaseContainsAsset(
        ReleaseApiItem release,
        string fileName)
    {
        if (!release.Assets.Any(asset => string.Equals(
                asset.Name,
                fileName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Release 中缺少 {fileName}。");
        }
    }

    private static Version? ParseReleaseVersion(string? tagName)
    {
        return Version.TryParse(tagName?.Trim().TrimStart('v', 'V'), out var version) &&
               version.Major >= 1
            ? version
            : null;
    }

    private static Uri CreateReleasePageUri(
        ApplicationUpdateMirror mirror,
        string tagName,
        string? apiReleasePage)
    {
        if (TryCreateTrustedUri(apiReleasePage, out var releasePage))
        {
            return releasePage;
        }

        var escapedTag = Uri.EscapeDataString(tagName);
        return mirror == ApplicationUpdateMirror.Gitee
            ? new Uri($"https://gitee.com/wwangyunhui/screenshot/releases/tag/{escapedTag}")
            : new Uri($"https://github.com/jiuwanzi-hui/Screenshot/releases/tag/{escapedTag}");
    }

    private static Uri CreateReleaseDownloadUri(
        ApplicationUpdateMirror mirror,
        string tagName,
        string fileName)
    {
        var escapedTag = Uri.EscapeDataString(tagName);
        var escapedFileName = Uri.EscapeDataString(fileName);
        return mirror == ApplicationUpdateMirror.Gitee
            ? new Uri(
                $"https://gitee.com/wwangyunhui/screenshot/releases/download/{escapedTag}/{escapedFileName}")
            : new Uri(
                $"https://github.com/jiuwanzi-hui/Screenshot/releases/download/{escapedTag}/{escapedFileName}");
    }

    internal static string FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "暂无更新说明。";
        }

        var output = new List<string>();
        foreach (var sourceLine in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = sourceLine.Trim();
            if (Regex.IsMatch(line, "^#{1,6}\\s*English\\s*$", RegexOptions.IgnoreCase))
            {
                break;
            }

            if (Regex.IsMatch(
                    line,
                    "^#{1,6}\\s*(下载|校验|SHA-?256)\\s*$",
                    RegexOptions.IgnoreCase))
            {
                break;
            }

            if (Regex.IsMatch(
                    line,
                    "^#{1,6}\\s*(SnapCut|Screenshot)\\s+v?\\d+(?:\\.\\d+){1,3}\\s*$",
                    RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) ||
                Regex.IsMatch(line, "^\\|?\\s*[-:]+(?:\\s*\\|\\s*[-:]+)+\\s*\\|?$") ||
                line.StartsWith("安装版 SHA-256", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("免安装版 SHA-256", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = Regex.Replace(line, "^#{1,6}\\s*", string.Empty);
            line = Regex.Replace(line, "^[-*+]\\s+", "• ");
            line = Regex.Replace(line, "\\[([^]]+)]\\([^)]+\\)", "$1");
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);
            if (line.StartsWith('|') && line.EndsWith('|'))
            {
                line = string.Join(" · ", line.Trim('|').Split('|').Select(cell => cell.Trim()));
            }

            output.Add(line);
            if (output.Sum(value => value.Length + 1) >= 6000)
            {
                output.Add("…");
                break;
            }
        }

        return output.Count == 0 ? "暂无更新说明。" : string.Join(Environment.NewLine, output);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static string NormalizeVersion(Version version)
    {
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static Version ComparableVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(0, version.Build));
    }

    private async Task<ApplicationUpdateCheckResult> CheckSourceSafelyAsync(
        UpdateSource source,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CheckSourceAsync(source, currentVersion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        {
            return new ApplicationUpdateCheckResult(
                false,
                null,
                $"{GetMirrorName(source.Mirror)}：{exception.Message}");
        }
    }

    private async Task<ApplicationUpdateCheckResult> CheckSourceAsync(
        UpdateSource source,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(
            source.ManifestUri,
            currentVersion,
            cancellationToken);
        if (manifest is null && source.LegacyManifestUri is not null)
        {
            manifest = await ReadManifestAsync(
                source.LegacyManifestUri,
                currentVersion,
                cancellationToken);
        }

        if (manifest is null)
        {
            return await CheckLatestReleaseRedirectAsync(
                source,
                currentVersion,
                cancellationToken);
        }

        var update = ValidateManifest(manifest, source.Mirror);
        if (ComparableVersion(update.Version).CompareTo(ComparableVersion(currentVersion)) <= 0)
        {
            return new ApplicationUpdateCheckResult(
                true,
                null,
                $"当前已是最新版本 {NormalizeVersion(currentVersion)}（{GetMirrorName(source.Mirror)}）。");
        }

        return new ApplicationUpdateCheckResult(
            true,
            update,
            $"发现新版本 {NormalizeVersion(update.Version)}（{GetMirrorName(source.Mirror)}）。");
    }

    private async Task<UpdateManifest?> ReadManifestAsync(
        Uri manifestUri,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.UserAgent.ParseAdd($"SnapCut/{NormalizeVersion(currentVersion)}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(
            stream,
            ManifestJsonOptions,
            cancellationToken);
    }

    private static ApplicationUpdateInfo ValidateManifest(
        UpdateManifest? manifest,
        ApplicationUpdateMirror preferredMirror)
    {
        if (manifest is null ||
            !Version.TryParse(manifest.Version?.TrimStart('v', 'V'), out var version) ||
            version.Major < 1)
        {
            throw new InvalidDataException("更新清单中的版本号无效。");
        }

        if (!TryCreateTrustedUri(manifest.ReleasePage, out var releasePage))
        {
            throw new InvalidDataException("更新清单中的发布页面无效。");
        }

        var displayVersion = NormalizeVersion(version);
        // The application was renamed from Screenshot to SnapCut in 2.0.0.
        // Versions 2.1.0 and later accept both asset brands. The old manifest
        // endpoint remains available while new releases use SnapCut package names.
        var installerNames = new[]
        {
            $"SnapCut-Setup-{displayVersion}-win-x64.exe",
            $"Screenshot-Setup-{displayVersion}-win-x64.exe",
        };
        var portableNames = new[]
        {
            $"SnapCut-Portable-{displayVersion}-win-x64.zip",
            $"Screenshot-Portable-{displayVersion}-win-x64.zip",
        };
        return new ApplicationUpdateInfo(
            version,
            releasePage,
            CreateAsset(manifest.Installer, installerNames, preferredMirror),
            CreateAsset(manifest.Portable, portableNames, preferredMirror),
            preferredMirror);
    }

    private async Task<ApplicationUpdateCheckResult> CheckLatestReleaseRedirectAsync(
        UpdateSource source,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            source.LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd(
            $"SnapCut/{NormalizeVersion(currentVersion)}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        var versionText = finalUri?.Segments.LastOrDefault()?.Trim('/');
        if (!Version.TryParse(versionText?.TrimStart('v', 'V'), out var latestVersion))
        {
            throw new InvalidDataException(
                $"无法从 {GetMirrorName(source.Mirror)} latest 地址识别最新版本。");
        }

        if (ComparableVersion(latestVersion).CompareTo(
                ComparableVersion(currentVersion)) <= 0)
        {
            return new ApplicationUpdateCheckResult(
                true,
                null,
                $"当前已是最新版本 {NormalizeVersion(currentVersion)}（{GetMirrorName(source.Mirror)}）。");
        }

        return new ApplicationUpdateCheckResult(
            false,
            null,
            $"发现新版本 {NormalizeVersion(latestVersion)}，但该版本缺少在线更新清单，请稍后重试。");
    }

    private static ApplicationUpdateAsset CreateAsset(
        UpdateAssetManifest? manifest,
        IReadOnlyList<string> acceptedFileNames,
        ApplicationUpdateMirror preferredMirror)
    {
        var expectedFileName = manifest is null
            ? null
            : acceptedFileNames.FirstOrDefault(name => string.Equals(
                manifest.FileName,
                name,
                StringComparison.OrdinalIgnoreCase));

        if (manifest is null ||
            expectedFileName is null ||
            !TryCreateTrustedUri(manifest.GitHubUrl, out var githubUri) ||
            !TryCreateTrustedUri(manifest.GiteeUrl, out var giteeUri) ||
            !githubUri.AbsolutePath.EndsWith(
                "/" + expectedFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !giteeUri.AbsolutePath.EndsWith(
                "/" + expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新清单缺少 {acceptedFileNames[0]}。");
        }

        var asset = new ApplicationUpdateAsset(
            expectedFileName,
            githubUri,
            giteeUri,
            manifest.Size,
            manifest.Sha256?.Trim() ?? string.Empty,
            preferredMirror);
        ValidateAsset(asset, expectedFileName);
        return asset;
    }

    private static void ValidateAsset(
        ApplicationUpdateAsset asset,
        string expectedFileName)
    {
        if (!string.Equals(asset.FileName, expectedFileName, StringComparison.Ordinal) ||
            Path.GetFileName(asset.FileName) != asset.FileName ||
            asset.Size <= 0 ||
            asset.Size > MaximumPackageSize ||
            !Regex.IsMatch(asset.Sha256, "^[A-Fa-f0-9]{64}$") ||
            !IsTrustedDownloadUri(asset.GitHubDownloadUri) ||
            !IsTrustedDownloadUri(asset.GiteeDownloadUri))
        {
            throw new InvalidDataException("更新包信息未通过安全检查。");
        }
    }

    private static bool TryCreateTrustedUri(string? value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
               IsTrustedDownloadUri(uri);
    }

    private static bool IsTrustedDownloadUri(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttps &&
               (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "gitee.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMirrorName(ApplicationUpdateMirror mirror)
    {
        return mirror == ApplicationUpdateMirror.Gitee ? "Gitee 国内源" : "GitHub 国际源";
    }

    private static string GetMirrorName(Uri uri)
    {
        return string.Equals(uri.Host, "gitee.com", StringComparison.OrdinalIgnoreCase)
            ? "Gitee 国内源"
            : "GitHub 国际源";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class UpdateManifest
    {
        public string? Version { get; init; }

        public string? ReleasePage { get; init; }

        public UpdateAssetManifest? Installer { get; init; }

        public UpdateAssetManifest? Portable { get; init; }
    }

    private sealed class UpdateAssetManifest
    {
        public string? FileName { get; init; }

        public string? GitHubUrl { get; init; }

        public string? GiteeUrl { get; init; }

        public long Size { get; init; }

        public string? Sha256 { get; init; }
    }

    private sealed class ReleaseApiItem
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("draft")]
        public bool IsDraft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool IsPrerelease { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<ReleaseApiAsset> Assets { get; init; } = [];
    }

    private sealed class ReleaseApiAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }

    private sealed class ReleaseHistoryCacheFile
    {
        public ApplicationUpdateMirror Mirror { get; init; }

        public DateTimeOffset SavedAt { get; init; }

        public List<ReleaseApiItem> Releases { get; init; } = [];
    }

    private sealed record UpdateSource(
        ApplicationUpdateMirror Mirror,
        Uri ManifestUri,
        Uri? LegacyManifestUri,
        Uri LatestReleaseUri,
        Uri ReleaseHistoryUri,
        Uri? StaticReleaseHistoryUri);
}
