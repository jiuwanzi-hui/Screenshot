using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text.Realtime;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class RealtimeFeatureLifetimeTests(ITestOutputHelper output)
{
    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingRealtimeReleasesWorkerAfterTheUserClosesVisibleSettings(bool settingsFirst)
    {
        var pipeName = "SnapCut.Test.Realtime." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SnapCut.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--feature-worker"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add("--background");
        Window? sample = null;
        ScreenRegion region = default;
        WpfTestHost.Invoke(() =>
        {
            sample = new Window { Width = 360, Height = 140, ShowActivated = true, ShowInTaskbar = false,
                Title = "SnapCut translation lifecycle test", Background = System.Windows.Media.Brushes.White,
                Content = new System.Windows.Controls.TextBlock { Text = "Mana", FontSize = 28,
                    Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(24) } };
            sample.Show();
            region = RealtimeWindowTarget.Bounds(new WindowInteropHelper(sample).Handle);
        });
        using var process = Process.Start(start)!;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var connection = new FeatureProcessConnection(pipe);
            var settings = AppSettings.CreateDefault() with
            {
                RequestAdministratorPrivileges = false, OcrEngine = OcrEngineMode.Windows,
                RealtimeTranslation = new() { Glossary = "Mana=法力值" },
                RegionCaptureHotKey = "Ctrl+Alt+F11", OpenSettingsHotKey = "Ctrl+Alt+F12",
                OcrHotKey = "", PinHotKey = "", OpenSettingsOnStartup = false,
            };
            async Task<FeatureMessage> ReadUntil(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var message = await connection.ReadAsync().WaitAsync(timeout.Token);
                    if (message.Kind == "error") throw new InvalidOperationException(message.Text);
                    if (message.Kind == kind) return message;
                }
            }
            await connection.SendAsync(new("bootstrap") { Settings = settings });
            await ReadUntil("ready");
            await connection.SendAsync(new("activate") { Settings = settings });
            await ReadUntil("started");
            long sequence = 0;
            if (settingsFirst)
            {
                await connection.SendAsync(new("command") { Sequence = ++sequence, Command = "settings" });
                await Task.Delay(900);
            }
            nint settingsWindow = 0;
            if (settingsFirst)
            {
                _ = EnumWindows((handle, data) =>
                {
                    _ = GetWindowThreadProcessId(handle, out var pid);
                    if (pid == process.Id && IsWindowVisible(handle)) settingsWindow = handle;
                    return true;
                }, 0);
                Assert.NotEqual(0, settingsWindow);
            }
            const long nativeSelectionId = 731;
            await connection.SendAsync(new("command") { Sequence = ++sequence, HotKey = HotKeyAction.RealtimeTranslation,
                NativeSelectionId = nativeSelectionId, InitialSelection = region });
            var preparation = await ReadUntil("prepare-capture");
            Assert.Equal(nativeSelectionId, preparation.NativeSelectionId);
            await connection.SendAsync(new("capture-hidden") { Sequence = preparation.Sequence, Flag = true });
            await Task.Delay(2500);
            Assert.False(process.HasExited);
            nint overlay = 0;
            _ = EnumWindows((handle, data) =>
            {
                _ = GetWindowThreadProcessId(handle, out var pid);
                var buffer = new char[256];
                var length = GetWindowText(handle, buffer, buffer.Length);
                var title = new string(buffer, 0, length);
                if (pid == process.Id && IsWindowVisible(handle))
                    output.WriteLine($"HWND {handle}, visible={IsWindowVisible(handle)}, bounds={RealtimeWindowTarget.Bounds(handle)}, title={title}");
                if (pid == process.Id && title == "SnapCut 实时译文") overlay = handle;
                return true;
            }, 0);
            Assert.NotEqual(0, overlay);
            Assert.True(IsWindowVisible(overlay));
            Assert.Equal(region, RealtimeWindowTarget.Bounds(overlay));
            var root = System.Windows.Automation.AutomationElement.FromHandle(overlay);
            var text = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Text));
            var labels = text.Cast<System.Windows.Automation.AutomationElement>().Select(item => item.Current.Name).ToArray();
            output.WriteLine("Actual worker overlay text: " + string.Join(" | ", labels));
            Assert.Contains(labels, label => label.Contains("法力值", StringComparison.Ordinal));
            if (settingsFirst) Assert.True(IsWindowVisible(settingsWindow));
            process.Refresh();
            output.WriteLine($"Active worker: {process.Id}, working set {process.WorkingSet64 / 1048576d:F1} MiB, private commit {process.PrivateMemorySize64 / 1048576d:F1} MiB");
            var watch = Stopwatch.StartNew();
            await connection.SendAsync(new("command") { Sequence = ++sequence, Command = "realtime" });
            if (settingsFirst)
            {
                await Task.Delay(250);
                Assert.True(IsWindowVisible(settingsWindow));
                // Open settings is intentional user UI, not an idle resource.
                // The worker retires only after the user closes that UI too.
                _ = PostMessage(settingsWindow, 0x0010, 0, 0);
            }
            var idle = await ReadUntil("idle");
            Assert.Equal(sequence, idle.Sequence);
            await connection.SendAsync(new("retire") { Sequence = idle.Sequence });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine($"Settings first: {settingsFirst}; worker exited {watch.ElapsedMilliseconds} ms after stop.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            WpfTestHost.Invoke(() => sample?.Close());
        }
    }
    private delegate bool EnumCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int count);
}
