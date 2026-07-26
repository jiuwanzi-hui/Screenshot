using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
    public void PortableApplyReplacesProgramButPreservesScreenshotData()
    {
        var root = CreateTemporaryDirectory();
        var packagePath = Path.Combine(root, "update.zip");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(target, "ScreenshotData"));
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old");
        File.WriteAllText(
            Path.Combine(target, "ScreenshotData", "settings.json"),
            "personal");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "SnapCut.exe", "new");
            WriteEntry(archive, "ScreenshotData/settings.json", "must-not-replace");
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
}
