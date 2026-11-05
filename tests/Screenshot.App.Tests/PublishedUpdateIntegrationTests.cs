using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Screenshot.App.Update;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

// Explicit post-publication checks; real downloads, isolated replacement only.
public sealed class PublishedUpdateIntegrationTests(ITestOutputHelper output)
{
    [LocalPackageFact]
    public async Task PreparedLocalPackagesApplyAndPreserveUserData()
    {
        var packages = Path.GetFullPath(Environment.GetEnvironmentVariable("SNAPCUT_VERIFY_LOCAL_RELEASE_DIR")!);
        using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packages, "SnapCut-Update.json")));
        var version = manifest.RootElement.GetProperty("version").GetString();
        foreach (var kind in new[] { "portable", "portable7z" })
        {
            var asset = manifest.RootElement.GetProperty(kind);
            var name = asset.GetProperty("fileName").GetString()!;
            Assert.Equal(name, Path.GetFileName(name));
            var package = Path.Combine(packages, name);
            Assert.Equal(asset.GetProperty("size").GetInt64(), new FileInfo(package).Length);
            await using (var input = File.OpenRead(package))
                Assert.Equal(asset.GetProperty("sha256").GetString(), Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input)));
            var target = Path.Combine(Path.GetTempPath(), "SnapCut-local-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                var preserved = new[] { "ScreenshotData/settings.json", "ScreenshotData/translation-credentials.dat",
                    "ScreenshotData/History/retained.txt", "TranslationModels/retained-model.txt" };
                foreach (var relative in preserved)
                {
                    var file = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, "retained user content");
                }
                PortableUpdateRunner.ApplyPackage(package, target);
                foreach (var relative in preserved)
                    Assert.Equal("retained user content", await File.ReadAllTextAsync(Path.Combine(target, relative)));
                var executable = Path.Combine(target, "SnapCut.exe");
                Assert.Equal(version, FileVersionInfo.GetVersionInfo(executable).ProductVersion);
                Assert.True(File.Exists(Path.Combine(target, "ScreenRecorderLib.dll")));
                Assert.Empty(Directory.GetFiles(target, "*ToolbarDiagnosis*", SearchOption.AllDirectories));
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = target };
                start.ArgumentList.Add(PortableUpdateRunner.UpdateArgument);
                using var process = Process.Start(start)!;
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.Equal(2, process.ExitCode);
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                output.WriteLine($"{name}: real package hash, isolated replacement, data preservation and executable startup passed.");
            }
            finally { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
        }
    }

    private sealed class LocalPackageFactAttribute : FactAttribute
    {
        public LocalPackageFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_VERIFY_LOCAL_RELEASE_DIR")))
                Skip = "Set SNAPCUT_VERIFY_LOCAL_RELEASE_DIR to verify prepared packages without network or installation.";
        }
    }

    [PublishedUpdateFact]
    public Task GitHubPublishedVersionDownloadsAndApplies() => VerifyAsync(ApplicationUpdateMirror.GitHub);

    [PublishedUpdateFact]
    public Task GiteePublishedVersionDownloadsAndAppliesWithoutGitHub() => VerifyAsync(ApplicationUpdateMirror.Gitee);

    private async Task VerifyAsync(ApplicationUpdateMirror mirror)
    {
        var expected = Version.Parse(Environment.GetEnvironmentVariable("SNAPCUT_VERIFY_PUBLISHED_VERSION")!);
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-published-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new HttpClient(new OnlyMirrorHandler(mirror)) { Timeout = TimeSpan.FromMinutes(10) };
            using var service = new ApplicationUpdateService(client, updatesDirectory: Path.Combine(directory, "downloads"));
            var result = await service.CheckAsync(new Version(4, 1, 3));
            Assert.True(result.IsSuccess, result.Message);
            var update = Assert.IsType<ApplicationUpdateInfo>(result.AvailableUpdate);
            Assert.Equal(expected, update.Version);
            Assert.Equal(mirror, update.PreferredMirror);
            output.WriteLine($"Detected {update.Version} from {update.PreferredMirror}: {update.ReleasePage}");
            var installer = await service.DownloadAsync(update.Installer);
            Assert.Equal(update.Installer.Size, new FileInfo(installer).Length);
            var package = await service.DownloadAsync(update.Portable);
            Assert.Equal(update.Portable.Size, new FileInfo(package).Length);
            output.WriteLine("Installer and portable downloads passed the production size/SHA-256 checks.");

            var target = Path.Combine(directory, "isolated-application");
            var preserved = new[] { "ScreenshotData/settings.json", "ScreenshotData/translation-credentials.dat",
                "ScreenshotData/History/retained.txt", "TranslationModels/retained-model.txt" };
            foreach (var relative in preserved)
            {
                var path = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "retained user content");
            }
            PortableUpdateRunner.ApplyPackage(package, target);
            foreach (var relative in preserved)
                Assert.Equal("retained user content", await File.ReadAllTextAsync(Path.Combine(target, relative)));
            var executable = Path.Combine(target, "SnapCut.exe");
            Assert.Equal(expected.ToString(), FileVersionInfo.GetVersionInfo(executable).ProductVersion);
            Assert.True(File.Exists(Path.Combine(target, "ScreenRecorderLib.dll")));
            Assert.Empty(Directory.GetFiles(target, "*ToolbarDiagnosis*", SearchOption.TopDirectoryOnly));
            // Exercise the downloaded executable's updater entry point without
            // installing into a user directory or opening another UI instance.
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = target };
            start.ArgumentList.Add(PortableUpdateRunner.UpdateArgument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, process.ExitCode); // Deliberately incomplete update arguments.
            output.WriteLine($"Applied {expected} in isolation; settings, credentials, history and model sentinel preserved; downloaded updater started.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class OnlyMirrorHandler(ApplicationUpdateMirror mirror) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if ((mirror == ApplicationUpdateMirror.Gitee && host.Contains("github", StringComparison.OrdinalIgnoreCase)) ||
                (mirror == ApplicationUpdateMirror.GitHub && host.Contains("gitee", StringComparison.OrdinalIgnoreCase)))
                throw new HttpRequestException("Other mirror intentionally blocked for release verification.");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PublishedUpdateFactAttribute : FactAttribute
    {
        public PublishedUpdateFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAPCUT_VERIFY_PUBLISHED_VERSION")))
                Skip = "Set SNAPCUT_VERIFY_PUBLISHED_VERSION to explicitly verify a published release using real downloads.";
        }
    }
}
