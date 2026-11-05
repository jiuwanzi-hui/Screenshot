using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed class UpdateRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SnapCut.UpdateTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("verified package");
    private static ApplicationUpdateAsset Asset => new("SnapCut-Setup-4.0.0-win-x64.exe",
        new Uri("https://github.com/update.exe"), Package.Length, Convert.ToHexString(SHA256.HashData(Package)));

    [Fact]
    public async Task RetriesTransientFailuresAndReusesVerifiedPackageWithoutNetwork()
    {
        using var handler = new RetryHandler { FailuresRemaining = 2 };
        using var client = new HttpClient(handler);
        using var service = CreateService(client);
        var path = await service.DownloadAsync(Asset);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(Package, File.ReadAllBytes(path));
        Assert.Equal(path, await service.DownloadAsync(Asset));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ExhaustedAttemptsCanBeRetriedUsingTheSameService()
    {
        using var handler = new RetryHandler { FailuresRemaining = 3 };
        using var client = new HttpClient(handler);
        using var service = CreateService(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(Asset));
        Assert.Equal(3, handler.Calls);
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
        var path = await service.DownloadAsync(Asset);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(Package, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task StalledResponseBodyTimesOutAndTheNextAttemptRecovers()
    {
        using var handler = new RetryHandler { StallFirstBody = true };
        using var client = new HttpClient(handler);
        using var service = CreateService(client);
        var path = await service.DownloadAsync(Asset).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(Package, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task UserCancellationDoesNotStartAnotherAttempt()
    {
        using var handler = new RetryHandler { StallFirstBody = true };
        using var client = new HttpClient(handler);
        using var service = CreateService(client);
        service.DownloadReadTimeout = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(Asset, cancellationToken: cancellation.Token));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }

    [Fact]
    public void InterruptedReplacementRestoresAlreadyChangedFilesAndCanRetry()
    {
        var staging = Path.Combine(_root, "staging");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(staging, "a.dll"), "new a");
        File.WriteAllText(Path.Combine(staging, "b.dll"), "new b");
        File.WriteAllText(Path.Combine(target, "a.dll"), "old a");
        File.WriteAllText(Path.Combine(target, "b.dll"), "old b");
        using (var locked = new FileStream(Path.Combine(target, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => PortableUpdateRunner.CommitStagedFiles(staging, target));
        }
        Assert.Equal("old a", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(target, "b.dll")));
        File.WriteAllText(Path.Combine(staging, "a.dll"), "new a");
        PortableUpdateRunner.CommitStagedFiles(staging, target);
        Assert.Equal("new a", File.ReadAllText(Path.Combine(target, "a.dll")));
        Assert.Equal("new b", File.ReadAllText(Path.Combine(target, "b.dll")));
    }

    [Fact]
    public void SupervisedInstallerDoesNotRestartBeforeRetryControllerFinishes()
    {
        var info = ApplicationUpdateLauncher.CreateInstallerStartInfo("setup.exe", restartAfterInstallation: false);
        Assert.Contains("/UPDATE=0", info.ArgumentList);
        Assert.DoesNotContain("/UPDATE=1", info.ArgumentList);
        Assert.True(PortableUpdateRunner.IsUpdateRequest([PortableUpdateRunner.InstallerUpdateArgument]));
    }

    private ApplicationUpdateService CreateService(HttpClient client) => new(client,
        new Uri("https://github.com/update.json"), _root)
    { DownloadRetryDelay = TimeSpan.Zero, DownloadReadTimeout = TimeSpan.FromMilliseconds(80) };

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int FailuresRemaining { get; set; }
        public bool StallFirstBody { get; set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailuresRemaining-- > 0) throw new HttpRequestException("temporary outage");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = StallFirstBody && Calls == 1 ? new StreamContent(new StalledStream()) : new ByteArrayContent(Package),
            });
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
