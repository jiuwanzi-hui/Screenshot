using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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

public sealed class ApplicationUpdateService : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static readonly Uri DefaultGitHubManifestUri = new(
        "https://github.com/jiuwanzi-hui/Screenshot/releases/latest/download/Screenshot-Update.json");
    internal static readonly Uri DefaultGitHubLatestReleaseUri = new(
        "https://github.com/jiuwanzi-hui/Screenshot/releases/latest");
    internal static readonly Uri DefaultGiteeManifestUri = new(
        "https://gitee.com/wwangyunhui/screenshot/raw/main/updates/Screenshot-Update.json");
    internal static readonly Uri DefaultGiteeLatestReleaseUri = new(
        "https://gitee.com/wwangyunhui/screenshot/releases/latest");

    private const long MaximumPackageSize = 500L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<UpdateSource> _sources;
    private readonly string _updatesDirectory;

    public ApplicationUpdateService(
        HttpClient? httpClient = null,
        Uri? manifestUri = null,
        string? updatesDirectory = null)
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
                    DefaultGiteeLatestReleaseUri),
                new UpdateSource(
                    ApplicationUpdateMirror.GitHub,
                    DefaultGitHubManifestUri,
                    DefaultGitHubLatestReleaseUri),
            ]
            :
            [
                new UpdateSource(
                    ApplicationUpdateMirror.GitHub,
                    manifestUri,
                    DefaultGitHubLatestReleaseUri),
            ];
        _updatesDirectory = updatesDirectory ?? AppMetadata.UpdatesDirectoryPath;
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

    private async Task DownloadFromUriAsync(
        ApplicationUpdateAsset asset,
        Uri downloadUri,
        string partialPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        request.Headers.UserAgent.ParseAdd($"Screenshot/{AppMetadata.DisplayVersion}");
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
        using var request = new HttpRequestMessage(HttpMethod.Get, source.ManifestUri);
        request.Headers.UserAgent.ParseAdd($"Screenshot/{NormalizeVersion(currentVersion)}");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return await CheckLatestReleaseRedirectAsync(
                source,
                currentVersion,
                cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
            stream,
            ManifestJsonOptions,
            cancellationToken);
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
        var installerName = $"Screenshot-Setup-{displayVersion}-win-x64.exe";
        var portableName = $"Screenshot-Portable-{displayVersion}-win-x64.zip";
        return new ApplicationUpdateInfo(
            version,
            releasePage,
            CreateAsset(manifest.Installer, installerName, preferredMirror),
            CreateAsset(manifest.Portable, portableName, preferredMirror),
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
            $"Screenshot/{NormalizeVersion(currentVersion)}");
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
        string expectedFileName,
        ApplicationUpdateMirror preferredMirror)
    {
        if (manifest is null ||
            !string.Equals(
                manifest.FileName,
                expectedFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !TryCreateTrustedUri(manifest.GitHubUrl, out var githubUri) ||
            !TryCreateTrustedUri(manifest.GiteeUrl, out var giteeUri) ||
            !githubUri.AbsolutePath.EndsWith(
                "/" + expectedFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !giteeUri.AbsolutePath.EndsWith(
                "/" + expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"更新清单缺少 {expectedFileName}。");
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

    private sealed record UpdateSource(
        ApplicationUpdateMirror Mirror,
        Uri ManifestUri,
        Uri LatestReleaseUri);
}
