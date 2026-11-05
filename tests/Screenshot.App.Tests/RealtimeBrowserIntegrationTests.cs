using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;
using Screenshot.App.Text.Realtime;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

// Opt in with a real, visible browser region and the source Release executable.
// No navigation, generated page or temporary replacement of user preferences.
public sealed class RealtimeBrowserIntegrationTests(ITestOutputHelper output)
{
    [BrowserFact]
    public async Task ActualBrowserRegionSurvivesColdHandoffAndShowsChineseInDesktopCapture()
    {
        var executable = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_BROWSER_EXE")!;
        var coordinates = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_BROWSER_REGION")!.Split(',').Select(int.Parse).ToArray();
        var region = new ScreenRegion(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
        var directory = Path.GetDirectoryName(executable)!;
        var settings = new SettingsStore(Path.Combine(AppMetadata.ResolveDataDirectoryPath(directory), "settings.json")).Load().Settings;
        Assert.Equal("zh-Hans", settings.TranslationTargetLanguage);
        var target = RealtimeWindowTarget.FromRegion(region);
        Assert.NotEqual(0, target);
        var pipeName = "SnapCut.Test.Browser." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--feature-worker"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add("--background");
        using var process = Process.Start(start)!;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var connection = new FeatureProcessConnection(pipe);
            async Task<FeatureMessage> ReadUntil(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    var message = await connection.ReadAsync().WaitAsync(timeout.Token);
                    if (message.Kind == "error") throw new InvalidOperationException(message.Text);
                    if (message.Kind == kind) return message;
                }
            }
            await connection.SendAsync(new("bootstrap") { Settings = settings });
            await ReadUntil("ready");
            await connection.SendAsync(new("activate") { Settings = settings, Flag = true });
            await ReadUntil("started");
            var watch = Stopwatch.StartNew();
            await connection.SendAsync(new("command") { Sequence = 1, HotKey = HotKeyAction.RealtimeTranslation,
                NativeSelectionId = 832, InitialSelection = region });
            var prepared = await ReadUntil("prepare-capture");
            Assert.Equal(832, prepared.NativeSelectionId);
            await connection.SendAsync(new("capture-hidden") { Sequence = prepared.Sequence, Flag = true });
            nint overlay = 0;
            string[] labels = [];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                _ = EnumWindows((handle, data) =>
                {
                    _ = GetWindowThreadProcessId(handle, out var pid);
                    if (pid != process.Id) return true;
                    var buffer = new char[128];
                    var length = GetWindowText(handle, buffer, buffer.Length);
                    if (new string(buffer, 0, length) == "SnapCut 实时译文") overlay = handle;
                    return true;
                }, 0);
                if (overlay != 0)
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(overlay);
                    labels = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty,
                            System.Windows.Automation.ControlType.Text)).Cast<System.Windows.Automation.AutomationElement>()
                        .Select(item => item.Current.Name).ToArray();
                    if (labels.Any(label => label.Contains("知识", StringComparison.Ordinal))) break;
                }
                await Task.Delay(200, timeout.Token);
            }
            output.WriteLine($"Actual browser HWND {target}, region {region}, first visible Chinese {watch.ElapsedMilliseconds} ms: {string.Join(" | ", labels)}");
            Assert.Equal(region, RealtimeWindowTarget.Bounds(overlay));
            Assert.True(GetWindowDisplayAffinity(overlay, out var affinity));
            Assert.Equal(0u, affinity);
            // Capture through a desktop source, like a remote viewer. Also read
            // the same region from the original HWND while its translation is up.
            using var desktop = new RealtimeCaptureSource(0, region);
            using var original = new RealtimeCaptureSource(target, region);
            async Task<RealtimeFrame> Frame(RealtimeCaptureSource source)
            {
                while (true)
                {
                    if (await source.CaptureAsync(timeout.Token) is { } frame) return frame;
                    await Task.Delay(50, timeout.Token);
                }
            }
            using (var visible = await Frame(desktop))
            using (var source = await Frame(original))
            {
                if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } renderDirectory)
                {
                    Directory.CreateDirectory(renderDirectory);
                    visible.Image.Bitmap.Save(Path.Combine(renderDirectory, "browser-translated.png"), System.Drawing.Imaging.ImageFormat.Png);
                    source.Image.Bitmap.Save(Path.Combine(renderDirectory, "browser-original.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                var translatedOcr = await OcrService.RecognizeAsync(visible.Image, "zh-Hans", timeout.Token);
                var sourceOcr = await OcrService.RecognizeAsync(source.Image, "zh-Hans", timeout.Token);
                output.WriteLine("Remote-visible OCR: " + translatedOcr.Text);
                output.WriteLine("Original browser OCR: " + sourceOcr.Text);
                Assert.True(sourceOcr.IsSuccess, sourceOcr.ErrorMessage);
                Assert.Contains("我们了解", System.Text.RegularExpressions.Regex.Replace(translatedOcr.Text, @"\s+", ""));
                Assert.Contains("knowledge", sourceOcr.Text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("知识", sourceOcr.Text);
            }
            watch.Restart();
            await connection.SendAsync(new("command") { Sequence = 2, HotKey = HotKeyAction.RealtimeTranslation });
            var idle = await ReadUntil("idle");
            await connection.SendAsync(new("retire") { Sequence = idle.Sequence });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            output.WriteLine($"Real model worker exited {watch.ElapsedMilliseconds} ms after stop.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed class BrowserFactAttribute : FactAttribute
    {
        public BrowserFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_DESKTOP_TESTS") != "1" ||
                !File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_BROWSER_EXE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_BROWSER_REGION")))
                Skip = "Opt in with SNAPCUT_REALTIME_BROWSER_EXE and a visible SNAPCUT_REALTIME_BROWSER_REGION (x,y,width,height).";
        }
    }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
