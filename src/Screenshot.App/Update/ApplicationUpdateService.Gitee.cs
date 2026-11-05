using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Screenshot.App.Update;

public sealed partial class ApplicationUpdateService
{
    private async Task<(Version Version, string Tag)> ResolveLatestReleaseAsync(
        UpdateSource source, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MetadataRequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, source.LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd("SnapCut/Update");
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        var prefix = source.LatestReleaseUri.AbsolutePath[..^"latest".Length] + "tag/";
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, source.LatestReleaseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !finalUri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("未能解析最新发行版本。");
        var tag = Uri.UnescapeDataString(finalUri.AbsolutePath[prefix.Length..].TrimEnd('/'));
        var version = ParseReleaseVersion(tag) ?? throw new InvalidDataException("发行标签中的版本号无效。");
        return (version, tag);
    }

    private async Task<ApplicationUpdateCheckResult> CheckGiteeReleaseAsync(
        UpdateSource source, Version currentVersion, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        try
        {
            // Release attachments are authoritative: publishing binaries does not
            // update the old raw/main manifest (source commits are local only).
            return await CheckLatestReleaseRedirectAsync(source, currentVersion, cancellationToken);
        }
        catch (Exception e) when (IsMetadataFailure(e, cancellationToken))
        {
            failures.Add(e.Message);
        }

        try
        {
            var releases = await ReadReleaseHistoryItemsAsync(source.ReleaseHistoryUri, cancellationToken);
            var latest = releases.Where(item => !item.IsDraft && !item.IsPrerelease)
                .Select(item => (Item: item, Version: ParseReleaseVersion(item.TagName)))
                .Where(item => item.Version is not null)
                .OrderByDescending(item => ComparableVersion(item.Version!)).FirstOrDefault();
            if (latest.Version is not null)
            {
                if (ComparableVersion(latest.Version) <= ComparableVersion(currentVersion))
                    return new(true, null, $"当前已是最新版本 {NormalizeVersion(currentVersion)}（Gitee）。");
                var release = await CreateReleaseInfoAsync(source, latest.Item, latest.Version, cancellationToken);
                return release.InstallableUpdate is { } update
                    ? new(true, update, $"发现新版本 {NormalizeVersion(update.Version)}（Gitee）。")
                    : new(false, null, $"发现新版本 {NormalizeVersion(latest.Version)}，{release.PackageWarning}");
            }
            failures.Add("发行列表中没有正式版本。");
        }
        catch (Exception e) when (IsMetadataFailure(e, cancellationToken))
        {
            failures.Add(e.Message);
        }

        try
        {
            var legacy = await CheckManifestSourceAsync(source, currentVersion, cancellationToken);
            if (legacy.AvailableUpdate is not null) return legacy;
        }
        catch (Exception e) when (IsMetadataFailure(e, cancellationToken))
        {
            failures.Add(e.Message);
        }
        // An old repository file cannot prove the current release is up to date.
        return new(false, null, "Gitee：无法确认最新发行版，请稍后重试。" + string.Join("；", failures));
    }

    private async Task<IReadOnlyList<ReleaseApiItem>> ReadStaticReleaseHistoryAsync(
        UpdateSource source, CancellationToken cancellationToken)
    {
        if (_probeMirrorAssets && source.Mirror == ApplicationUpdateMirror.Gitee)
        {
            try
            {
                var (_, tag) = await ResolveLatestReleaseAsync(source, cancellationToken);
                return await ReadReleaseHistoryItemsAsync(
                    CreateReleaseDownloadUri(source.Mirror, tag, "SnapCut-Releases.json"), cancellationToken);
            }
            catch (Exception e) when (IsMetadataFailure(e, cancellationToken)) { }
        }
        return await ReadReleaseHistoryItemsAsync(source.StaticReleaseHistoryUri!, cancellationToken);
    }

    private static bool IsMetadataFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException;
}
