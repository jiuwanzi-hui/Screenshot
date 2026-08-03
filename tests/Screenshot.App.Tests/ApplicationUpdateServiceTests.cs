using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task FindsNewerReleaseFromTheFixedLatestManifest()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var manifest = CreateManifest("2.0.0", package);
        using var client = new HttpClient(new StaticResponseHandler(
            Encoding.UTF8.GetBytes(manifest),
            "application/json"));
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/update.json"),
            CreateTemporaryPath());

        var result = await service.CheckAsync(new Version(1, 2, 0, 0));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.AvailableUpdate);
        var update = result.AvailableUpdate;
        Assert.Equal(new Version(2, 0, 0), update.Version);
        Assert.Equal("Screenshot-Setup-2.0.0-win-x64.exe", update.Installer.FileName);
        Assert.Equal("Screenshot-Portable-2.0.0-win-x64.zip", update.Portable.FileName);
    }

    [Fact]
    public async Task AcceptsSnapCutNamedAssetsAfterTheRename()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var manifest = CreateManifest("2.1.0", package)
            .Replace("Screenshot-Setup-", "SnapCut-Setup-")
            .Replace("Screenshot-Portable-", "SnapCut-Portable-");
        using var client = new HttpClient(new StaticResponseHandler(
            Encoding.UTF8.GetBytes(manifest),
            "application/json"));
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/update.json"),
            CreateTemporaryPath());

        var result = await service.CheckAsync(new Version(2, 0, 0, 0));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.AvailableUpdate);
        Assert.Equal(
            "SnapCut-Setup-2.1.0-win-x64.exe",
            result.AvailableUpdate.Installer.FileName);
        Assert.Equal(
            "SnapCut-Portable-2.1.0-win-x64.zip",
            result.AvailableUpdate.Portable.FileName);
    }

    [Fact]
    public async Task FallsBackToLegacyManifestNameWithoutChangingPackageNames()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var manifest = CreateManifest("2.3.0", package)
            .Replace("Screenshot-Setup-", "SnapCut-Setup-")
            .Replace("Screenshot-Portable-", "SnapCut-Portable-");
        var handler = new ManifestNameFallbackHandler(manifest);
        using var client = new HttpClient(handler);
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/SnapCut-Update.json"),
            CreateTemporaryPath(),
            legacyManifestUri: new Uri(
                "https://github.com/Screenshot-Update.json"));

        var result = await service.CheckAsync(new Version(2, 2, 2));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(
            ["SnapCut-Update.json", "Screenshot-Update.json"],
            handler.RequestedFileNames);
        Assert.Equal(
            "SnapCut-Setup-2.3.0-win-x64.exe",
            result.AvailableUpdate!.Installer.FileName);
    }

    [Fact]
    public async Task ReportsCurrentVersionAsUpToDate()
    {
        var package = Encoding.UTF8.GetBytes("package");
        using var client = new HttpClient(new StaticResponseHandler(
            Encoding.UTF8.GetBytes(CreateManifest("1.2.0", package)),
            "application/json"));
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/update.json"),
            CreateTemporaryPath());

        var result = await service.CheckAsync(new Version(1, 2, 0, 0));

        Assert.True(result.IsSuccess);
        Assert.Null(result.AvailableUpdate);
        Assert.Contains("最新版本", result.Message);
    }

    [Fact]
    public async Task LoadsFormalReleaseHistoryWithDatesNotesAndVerifiedPackages()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var historyJson = """
        [
          {
            "tag_name": "v2.2.1",
            "name": "SnapCut 2.2.1",
            "body": "## SnapCut 2.2.1\n\n- 修复长截图接缝。\n\n## English\n\n- English notes.",
            "html_url": "https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v2.2.1",
            "published_at": "2026-07-29T12:39:05Z",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "Screenshot-Update.json", "browser_download_url": "https://github.com/releases/v2.2.1/Screenshot-Update.json" },
              { "name": "Screenshot-Setup-2.2.1-win-x64.exe", "browser_download_url": "https://github.com/releases/v2.2.1/Screenshot-Setup-2.2.1-win-x64.exe" },
              { "name": "Screenshot-Portable-2.2.1-win-x64.zip", "browser_download_url": "https://github.com/releases/v2.2.1/Screenshot-Portable-2.2.1-win-x64.zip" }
            ]
          },
          {
            "tag_name": "v2.2.2",
            "name": "SnapCut 2.2.2",
            "body": "- 修复版本提示。",
            "html_url": "https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v2.2.2",
            "published_at": "2026-07-29T13:11:46Z",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "SnapCut-Update.json", "browser_download_url": "https://github.com/releases/v2.2.2/SnapCut-Update.json" },
              { "name": "SnapCut-Setup-2.2.2-win-x64.exe", "browser_download_url": "https://github.com/releases/v2.2.2/SnapCut-Setup-2.2.2-win-x64.exe" },
              { "name": "SnapCut-Portable-2.2.2-win-x64.zip", "browser_download_url": "https://github.com/releases/v2.2.2/SnapCut-Portable-2.2.2-win-x64.zip" }
            ]
          },
          {
            "tag_name": "v3.0.0-preview",
            "name": "Preview",
            "draft": false,
            "prerelease": true,
            "assets": []
          }
        ]
        """;
        using var client = new HttpClient(new ReleaseHistoryResponseHandler(
            historyJson,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["v2.2.1"] = CreateManifest("2.2.1", package),
                ["v2.2.2"] = CreateManifest("2.2.2", package)
                    .Replace("Screenshot-Setup-", "SnapCut-Setup-")
                    .Replace("Screenshot-Portable-", "SnapCut-Portable-"),
            }));
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/latest.json"),
            CreateTemporaryPath(),
            new Uri("https://github.com/releases.json"));

        var result = await service.GetReleaseHistoryAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Releases.Count);
        Assert.Equal(new Version(2, 2, 2), result.Releases[0].Version);
        Assert.Equal(
            "SnapCut-Setup-2.2.2-win-x64.exe",
            result.Releases[0].InstallableUpdate!.Installer.FileName);
        var rollback = result.Releases[1];
        Assert.Equal(new Version(2, 2, 1), rollback.Version);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 12, 39, 5, TimeSpan.Zero), rollback.PublishedAt);
        Assert.Contains("• 修复长截图接缝。", rollback.ReleaseNotes);
        Assert.DoesNotContain("SnapCut 2.2.1", rollback.ReleaseNotes);
        Assert.DoesNotContain("English notes", rollback.ReleaseNotes);
        Assert.NotNull(rollback.InstallableUpdate);
        Assert.Contains(
            "/releases/download/v2.2.1/",
            rollback.InstallableUpdate.Installer.GitHubDownloadUri.AbsolutePath);
        Assert.Null(rollback.PackageWarning);
    }

    [Fact]
    public async Task UsesBundledReleaseHistoryWhenBothPublicApisAreRateLimited()
    {
        var directory = CreateTemporaryDirectory();
        var bundledPath = Path.Combine(directory, "SnapCut-Releases.json");
        File.WriteAllText(
            bundledPath,
            CreateSingleReleaseHistoryJson("2.6.0", "SnapCut"));
        var handler = new NetworkUnavailableHandler(HttpStatusCode.Forbidden);
        try
        {
            using var client = new HttpClient(handler);
            using var service = new ApplicationUpdateService(
                client,
                new Uri("https://github.com/latest.json"),
                directory,
                new Uri("https://api.github.com/releases.json"),
                staticReleaseHistoryUri: new Uri(
                    "https://raw.githubusercontent.com/static-releases.json"),
                bundledReleaseHistoryPath: bundledPath);

            var result = await service.GetReleaseHistoryAsync();

            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Releases);
            Assert.Equal(new Version(2, 6, 0), result.Releases[0].Version);
            Assert.Contains("程序内置清单", result.Message);
            Assert.DoesNotContain(
                handler.RequestedPaths,
                path => path.EndsWith("/releases.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UsesCachedHistoryWithoutRequestingTheRateLimitedListAgain()
    {
        var directory = CreateTemporaryDirectory();
        var cachePath = Path.Combine(directory, "release-history-cache.json");
        var package = Encoding.UTF8.GetBytes("package");
        var historyJson = CreateSingleReleaseHistoryJson("2.6.0", "SnapCut");
        try
        {
            using (var client = new HttpClient(new ReleaseHistoryResponseHandler(
                       historyJson,
                       new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                       {
                           ["v2.6.0"] = CreateManifest("2.6.0", package)
                               .Replace("Screenshot-Setup-", "SnapCut-Setup-")
                               .Replace("Screenshot-Portable-", "SnapCut-Portable-"),
                       })))
            using (var service = new ApplicationUpdateService(
                       client,
                       new Uri("https://github.com/latest.json"),
                       directory,
                       new Uri("https://api.github.com/releases.json"),
                       releaseHistoryCachePath: cachePath,
                       bundledReleaseHistoryPath: Path.Combine(directory, "missing.json")))
            {
                var onlineResult = await service.GetReleaseHistoryAsync();
                Assert.True(onlineResult.IsSuccess, onlineResult.Message);
            }

            Assert.True(File.Exists(cachePath));
            var unavailableHandler = new NetworkUnavailableHandler(HttpStatusCode.Forbidden);
            using var unavailableClient = new HttpClient(unavailableHandler);
            using var cachedService = new ApplicationUpdateService(
                unavailableClient,
                new Uri("https://github.com/latest.json"),
                directory,
                new Uri("https://api.github.com/releases.json"),
                releaseHistoryCachePath: cachePath,
                bundledReleaseHistoryPath: Path.Combine(directory, "missing.json"));

            var cachedResult = await cachedService.GetReleaseHistoryAsync();

            Assert.True(cachedResult.IsSuccess, cachedResult.Message);
            Assert.Single(cachedResult.Releases);
            Assert.Contains("本地缓存", cachedResult.Message);
            Assert.DoesNotContain(
                unavailableHandler.RequestedPaths,
                path => path.EndsWith("/releases.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingManifestFallsBackToLatestReleaseRedirect()
    {
        using var client = new HttpClient(new LatestRedirectResponseHandler(
            new Uri("https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v1.2.0")));
        using var service = new ApplicationUpdateService(
            client,
            new Uri("https://github.com/update.json"),
            CreateTemporaryPath());

        var result = await service.CheckAsync(new Version(1, 2, 0, 0));

        Assert.True(result.IsSuccess);
        Assert.Null(result.AvailableUpdate);
        Assert.Contains("最新版本 1.2.0", result.Message);
    }

    [Fact]
    public async Task DownloadRequiresExactSizeAndSha256()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var package = Encoding.UTF8.GetBytes("verified update package");
            using var client = new HttpClient(new StaticResponseHandler(
                package,
                "application/octet-stream"));
            using var service = new ApplicationUpdateService(
                client,
                new Uri("https://github.com/update.json"),
                directory);
            var asset = new ApplicationUpdateAsset(
                "Screenshot-Setup-2.0.0-win-x64.exe",
                new Uri("https://github.com/update.exe"),
                package.Length,
                Convert.ToHexString(SHA256.HashData(package)));

            var path = await service.DownloadAsync(asset);

            Assert.Equal(package, File.ReadAllBytes(path));

            var invalidAsset = asset with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(invalidAsset));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.part"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFallsBackFromGiteeToGitHub()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var package = Encoding.UTF8.GetBytes("fallback update package");
            var handler = new MirrorFallbackResponseHandler(package);
            using var client = new HttpClient(handler);
            using var service = new ApplicationUpdateService(
                client,
                new Uri("https://github.com/update.json"),
                directory);
            var asset = new ApplicationUpdateAsset(
                "Screenshot-Setup-2.0.0-win-x64.exe",
                new Uri("https://github.com/update.exe"),
                new Uri("https://gitee.com/update.exe"),
                package.Length,
                Convert.ToHexString(SHA256.HashData(package)),
                ApplicationUpdateMirror.Gitee);

            var path = await service.DownloadAsync(asset);

            Assert.Equal(package, File.ReadAllBytes(path));
            Assert.Equal(["gitee.com", "github.com"], handler.RequestedHosts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PortableApplyPreservesUserDataAndDownloadedTranslationModels()
    {
        var root = CreateTemporaryDirectory();
        var packagePath = Path.Combine(root, "update.zip");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(target, "ScreenshotData"));
        Directory.CreateDirectory(Path.Combine(
            target,
            AppMetadata.TranslationModelsDirectoryName,
            "en-zh"));
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old");
        File.WriteAllText(
            Path.Combine(target, "ScreenshotData", "settings.json"),
            "personal");
        var modelPath = Path.Combine(
            target,
            AppMetadata.TranslationModelsDirectoryName,
            "en-zh",
            "model.bin");
        File.WriteAllText(modelPath, "downloaded-model");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "SnapCut.exe", "new");
            WriteEntry(archive, "ScreenshotData/settings.json", "must-not-replace");
            WriteEntry(
                archive,
                "TranslationModels/en-zh/model.bin",
                "must-not-replace");
        }

        try
        {
            PortableUpdateRunner.ApplyPackage(packagePath, target);

            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
            Assert.Equal(
                "personal",
                File.ReadAllText(Path.Combine(
                    target,
                    "ScreenshotData",
                    "settings.json")));
            Assert.Equal("downloaded-model", File.ReadAllText(modelPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableApplyRejectsArchivePathTraversal()
    {
        var root = CreateTemporaryDirectory();
        var packagePath = Path.Combine(root, "update.zip");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../outside.exe", "unsafe");
        }

        try
        {
            Assert.Throws<InvalidDataException>(() =>
            {
                PortableUpdateRunner.ApplyPackage(packagePath, target);
            });
            Assert.False(File.Exists(Path.Combine(root, "outside.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstallerUpdateUsesSilentOverwriteArguments()
    {
        var startInfo = ApplicationUpdateLauncher.CreateInstallerStartInfo(
            "Screenshot-Setup.exe");

        Assert.True(startInfo.UseShellExecute);
        Assert.Contains("/SILENT", startInfo.ArgumentList);
        Assert.Contains("/CLOSEAPPLICATIONS", startInfo.ArgumentList);
        Assert.Contains("/UPDATE=1", startInfo.ArgumentList);
        Assert.Contains(
            startInfo.ArgumentList,
            argument => argument.StartsWith(
                "/UPDATEPACKAGE=",
                StringComparison.Ordinal));
    }

    private static string CreateManifest(string version, byte[] package)
    {
        var hash = Convert.ToHexString(SHA256.HashData(package));
        return $$"""
        {
          "version": "{{version}}",
          "releasePage": "https://github.com/jiuwanzi-hui/Screenshot/releases/latest",
          "installer": {
            "fileName": "Screenshot-Setup-{{version}}-win-x64.exe",
            "githubUrl": "https://github.com/Screenshot-Setup-{{version}}-win-x64.exe",
            "giteeUrl": "https://gitee.com/Screenshot-Setup-{{version}}-win-x64.exe",
            "size": {{package.Length}},
            "sha256": "{{hash}}"
          },
          "portable": {
            "fileName": "Screenshot-Portable-{{version}}-win-x64.zip",
            "githubUrl": "https://github.com/Screenshot-Portable-{{version}}-win-x64.zip",
            "giteeUrl": "https://gitee.com/Screenshot-Portable-{{version}}-win-x64.zip",
            "size": {{package.Length}},
            "sha256": "{{hash}}"
          }
        }
        """;
    }

    private static string CreateSingleReleaseHistoryJson(
        string version,
        string assetPrefix)
    {
        return $$"""
        [
          {
            "tag_name": "v{{version}}",
            "name": "SnapCut {{version}}",
            "body": "- 修复版本历史读取。",
            "html_url": "https://github.com/jiuwanzi-hui/Screenshot/releases/tag/v{{version}}",
            "published_at": "2026-08-02T03:28:47Z",
            "draft": false,
            "prerelease": false,
            "assets": [
              { "name": "{{assetPrefix}}-Update.json", "browser_download_url": "https://github.com/releases/v{{version}}/{{assetPrefix}}-Update.json" },
              { "name": "{{assetPrefix}}-Setup-{{version}}-win-x64.exe", "browser_download_url": "https://github.com/releases/v{{version}}/{{assetPrefix}}-Setup-{{version}}-win-x64.exe" },
              { "name": "{{assetPrefix}}-Portable-{{version}}-win-x64.zip", "browser_download_url": "https://github.com/releases/v{{version}}/{{assetPrefix}}-Portable-{{version}}-win-x64.zip" }
            ]
          }
        ]
        """;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = CreateTemporaryPath();
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTemporaryPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            Guid.NewGuid().ToString("N"));
    }

    private sealed class StaticResponseHandler(
        byte[] content,
        string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class LatestRedirectResponseHandler(Uri finalUri) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            if (_requestCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri),
            });
        }
    }

    private sealed class ReleaseHistoryResponseHandler(
        string historyJson,
        IReadOnlyDictionary<string, string> manifests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.EndsWith("/releases.json", StringComparison.OrdinalIgnoreCase))
            {
                content = historyJson;
            }
            else
            {
                var tag = manifests.Keys.FirstOrDefault(key =>
                    path.Contains($"/{key}/", StringComparison.OrdinalIgnoreCase));
                if (tag is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                    });
                }

                content = manifests[tag];
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class MirrorFallbackResponseHandler(byte[] package) : HttpMessageHandler
    {
        public List<string> RequestedHosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            RequestedHosts.Add(host);
            return Task.FromResult(host == "gitee.com"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(package),
                    RequestMessage = request,
                });
        }
    }

    private sealed class NetworkUnavailableHandler(HttpStatusCode statusCode)
        : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
            });
        }
    }

    private sealed class ManifestNameFallbackHandler(string manifest)
        : HttpMessageHandler
    {
        public List<string> RequestedFileNames { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileName(request.RequestUri!.AbsolutePath);
            RequestedFileNames.Add(fileName);
            return Task.FromResult(fileName.StartsWith(
                "SnapCut-",
                StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        manifest,
                        Encoding.UTF8,
                        "application/json"),
                    RequestMessage = request,
                });
        }
    }
}
