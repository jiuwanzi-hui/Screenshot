using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed partial class ImmediateSettingsTests
{
    [Fact]
    public async Task UpdateButtonIsReadyWhileReleaseHistoryIsStillPending()
    {
        var handler = new PendingHistoryHandler();
        using var client = new HttpClient(handler);
        using var updates = new ApplicationUpdateService(client, new Uri("https://github.com/update.json"),
            _testDirectory, staticReleaseHistoryUri: new Uri("https://github.com/history.json"))
            { MetadataRequestTimeout = TimeSpan.FromSeconds(10) };
        MainWindow? window = null;
        GlobalHotKeyManager? hotkeys = null;
        Task? check = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                hotkeys = new GlobalHotKeyManager();
                window = new MainWindow(CreateSettings(), new SettingsStore(Path.Combine(_testDirectory, "update-ui.json")),
                    new FakeStartupRegistrationService(), hotkeys, new FakeTranslationCredentialStore(),
                    applicationUpdateService: updates);
                check = (Task)typeof(MainWindow).GetMethod("CheckForUpdatesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [true])!;
            });
            await handler.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var visible = false;
            for (var attempt = 0; attempt < 100 && !visible; attempt++)
            {
                WpfTestHost.Invoke(() => visible = ((Button)window!.FindName("InstallUpdateButton")).Visibility == Visibility.Visible);
                if (!visible) await Task.Delay(20);
            }
            Assert.True(visible, "A stalled history request must not hide the verified update action.");
            Assert.False(check!.IsCompleted);
            WpfTestHost.Invoke(() => Assert.True(((Button)window!.FindName("InstallUpdateButton")).IsEnabled));
            handler.ReleaseHistory.TrySetResult();
            await check.WaitAsync(TimeSpan.FromSeconds(4));
        }
        finally
        {
            handler.ReleaseHistory.TrySetResult();
            WpfTestHost.Invoke(() => { window?.RequestExit(); hotkeys?.Dispose(); });
        }
    }

    private sealed class PendingHistoryHandler : HttpMessageHandler
    {
        public TaskCompletionSource HistoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHistory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/update.json")
            {
                HistoryStarted.TrySetResult();
                await ReleaseHistory.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }
            object Asset(string name) => new { fileName = name, size = 1,
                sha256 = Convert.ToHexString(SHA256.HashData([1])),
                githubUrl = "https://github.com/" + name, giteeUrl = "https://gitee.com/" + name };
            var manifest = new { version = "99.0.0", releasePage = "https://github.com/releases",
                installer = Asset("SnapCut-Setup-99.0.0-win-x64.exe"), portable = Asset("SnapCut-Portable-99.0.0-win-x64.zip") };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(manifest)) };
        }
    }
}
