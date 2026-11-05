using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed partial class ApplicationUpdateServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GiteeReleaseUpdatesWithoutGitHubOrAnUpdatedRawManifest(bool latestUnavailable)
    {
        var handler = new GiteeReleaseHandler { LatestUnavailable = latestUnavailable };
        using var client = new HttpClient(handler);
        var directory = CreateTemporaryPath();
        try
        {
            using var service = new ApplicationUpdateService(client, updatesDirectory: directory)
            { MetadataRequestTimeout = TimeSpan.FromMilliseconds(150), DownloadRetryDelay = TimeSpan.Zero };
            var result = await service.CheckAsync(new Version(3, 8, 1)).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(result.IsSuccess, result.Message);
            var update = Assert.IsType<ApplicationUpdateInfo>(result.AvailableUpdate);
            Assert.Equal(new Version(4, 0, 2), update.Version);
            Assert.Equal(ApplicationUpdateMirror.Gitee, update.PreferredMirror);
            Assert.Equal("gitee.com", update.ReleasePage.Host);
            Assert.EndsWith(".7z", update.Portable.FileName);
            Assert.Contains("/v4.0.2/", update.Portable.GiteeDownloadUri.AbsolutePath);
            var downloaded = await service.DownloadAsync(update.Portable);
            Assert.Equal(GiteeReleaseHandler.Package, await File.ReadAllBytesAsync(downloaded));
            Assert.Equal(0, handler.RawRequests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task KnownNewGiteeReleaseWithWrongManifestNeverReportsUpToDate()
    {
        using var client = new HttpClient(new GiteeReleaseHandler { WrongVersion = true });
        using var service = new ApplicationUpdateService(client, updatesDirectory: CreateTemporaryPath())
        { MetadataRequestTimeout = TimeSpan.FromMilliseconds(150) };
        var result = await service.CheckAsync(new Version(4, 0, 1));
        Assert.False(result.IsSuccess);
        Assert.Null(result.AvailableUpdate);
        Assert.Contains("版本不一致", result.Message);
    }

    [Fact]
    public async Task StaleRawManifestCannotConfirmLatestWhenReleaseEndpointsFail()
    {
        using var client = new HttpClient(new GiteeReleaseHandler { LatestUnavailable = true, ApiUnavailable = true });
        using var service = new ApplicationUpdateService(client, updatesDirectory: CreateTemporaryPath())
        { MetadataRequestTimeout = TimeSpan.FromMilliseconds(150) };
        var result = await service.CheckAsync(new Version(4, 0, 1));
        Assert.False(result.IsSuccess);
        Assert.Contains("无法确认最新发行版", result.Message);
    }

    [Fact]
    public async Task MetadataCancellationStopsBothMirrors()
    {
        using var client = new HttpClient(new GiteeReleaseHandler { StallAll = true });
        using var service = new ApplicationUpdateService(client, updatesDirectory: CreateTemporaryPath());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(new Version(3, 8, 1), cancellation.Token));
    }

    [GiteeNetworkFact]
    public async Task LiveGiteeOnlyDownloadAndPortableApplyPreserveUserData()
    {
        using var client = new HttpClient(new BlockGitHubHandler()) { Timeout = TimeSpan.FromMinutes(3) };
        var directory = CreateTemporaryPath();
        try
        {
            using var service = new ApplicationUpdateService(client, updatesDirectory: directory);
            var result = await service.CheckAsync(new Version(3, 8, 1));
            Assert.True(result.IsSuccess, result.Message);
            var update = Assert.IsType<ApplicationUpdateInfo>(result.AvailableUpdate);
            Assert.Equal(ApplicationUpdateMirror.Gitee, update.PreferredMirror);
            Assert.EndsWith(".7z", update.Portable.FileName);
            var installerPath = await service.DownloadAsync(update.Installer);
            Assert.Equal(update.Installer.Size, new FileInfo(installerPath).Length);
            var path = await service.DownloadAsync(update.Portable);
            Assert.Equal(update.Portable.Size, new FileInfo(path).Length);
            var target = Path.Combine(directory, "isolated-application");
            Directory.CreateDirectory(Path.Combine(target, "ScreenshotData"));
            File.WriteAllText(Path.Combine(target, "ScreenshotData/settings.json"), "personal settings");
            PortableUpdateRunner.ApplyPackage(path, target);
            Assert.True(new FileInfo(Path.Combine(target, "SnapCut.exe")).Length > 0);
            Assert.True(File.Exists(Path.Combine(target, "ScreenRecorderLib.dll")));
            Assert.Equal("personal settings", File.ReadAllText(Path.Combine(target, "ScreenshotData/settings.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class GiteeNetworkFactAttribute : FactAttribute
    {
        public GiteeNetworkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SNAPCUT_VERIFY_GITEE_UPDATE") != "1")
                Skip = "Explicit live-network verification downloads the Gitee portable package; no desktop input or install is performed.";
        }
    }

    private sealed class BlockGitHubHandler : DelegatingHandler
    {
        public BlockGitHubHandler() : base(new HttpClientHandler()) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("GitHub blocked for mainland-only verification");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class GiteeReleaseHandler : HttpMessageHandler
    {
        public static readonly byte[] Package = "verified gitee package"u8.ToArray();
        public bool LatestUnavailable { get; init; }
        public bool ApiUnavailable { get; init; }
        public bool WrongVersion { get; init; }
        public bool StallAll { get; init; }
        public int RawRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (StallAll || uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (request.Method == HttpMethod.Head) return Response(request, "");
            if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                if (LatestUnavailable) return new(HttpStatusCode.ServiceUnavailable);
                return Response(new HttpRequestMessage(HttpMethod.Get, "https://gitee.com/wwangyunhui/screenshot/releases/tag/v4.0.2"), "");
            }
            if (uri.AbsolutePath.Contains("/api/v5/", StringComparison.Ordinal))
            {
                if (ApiUnavailable) return new(HttpStatusCode.ServiceUnavailable);
                // Gitee's API need not return semantic versions in descending order.
                var array = JsonNode.Parse(CreateSingleReleaseHistoryJson("4.0.1", "SnapCut"))!.AsArray();
                var latest = JsonNode.Parse(CreateSingleReleaseHistoryJson("4.0.2", "SnapCut"))![0]!.DeepClone();
                latest["assets"]!.AsArray().Add(new JsonObject { ["name"] = "SnapCut-Portable-4.0.2-win-x64.7z" });
                array.Add(latest);
                return Response(request, array.ToJsonString());
            }
            if (uri.AbsolutePath.Contains("/raw/", StringComparison.Ordinal))
            {
                RawRequests++;
                return Response(request, CreateManifest("3.8.1", Package));
            }
            if (uri.AbsolutePath.EndsWith("-Update.json", StringComparison.Ordinal))
            {
                var manifest = JsonNode.Parse(CreateManifest(WrongVersion ? "4.0.1" : "4.0.2", Package)
                    .Replace("Screenshot-", "SnapCut-"))!;
                manifest["portable7z"] = manifest["portable"]!.DeepClone();
                foreach (var name in new[] { "fileName", "githubUrl", "giteeUrl" })
                    manifest["portable7z"]![name] = manifest["portable7z"]![name]!.GetValue<string>().Replace(".zip", ".7z");
                return Response(request, manifest.ToJsonString());
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Package) };
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, string content) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(content) };
    }
}
