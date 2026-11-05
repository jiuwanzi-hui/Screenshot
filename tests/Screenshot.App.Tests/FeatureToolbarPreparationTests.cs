using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class FeatureToolbarPreparationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [DesktopTheory]
    [InlineData(HotKeyAction.RegionCapture, 2)]
    [InlineData(HotKeyAction.RegionCapture, 8)]
    [InlineData(HotKeyAction.VideoRecording, 2)]
    [InlineData(HotKeyAction.VideoRecording, 8)]
    public async Task PreparingThenCancellingSelectionReleasesTheRealWorkerWithoutShowingAnEditor(HotKeyAction action, int processors)
    {
        var pipeName = "SnapCut.Test.Toolbar." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var workerExe = Environment.GetEnvironmentVariable("SNAPCUT_TEST_WORKER_EXE") ??
            Path.Combine(AppContext.BaseDirectory, "SnapCut.exe");
        var start = new ProcessStartInfo(workerExe)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(workerExe)! };
        start.ArgumentList.Add("--feature-worker");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--capture-startup");
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        try
        {
            process.ProcessorAffinity = new IntPtr((1L << Math.Min(processors, Environment.ProcessorCount)) - 1);
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var connection = new FeatureProcessConnection(pipe);
            async Task<FeatureMessage> ReadUntil(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    var message = await connection.ReadAsync().WaitAsync(timeout.Token);
                    Assert.NotEqual("error", message.Kind);
                    if (message.Kind == kind) return message;
                }
            }
            var settings = AppSettings.CreateDefault() with { RequestAdministratorPrivileges = false,
                RegionCaptureHotKey = "Ctrl+Alt+F11", OpenSettingsHotKey = "Ctrl+Alt+F12",
                OcrHotKey = "", PinHotKey = "", OpenSettingsOnStartup = false };
            await connection.SendAsync(new("bootstrap") { Settings = settings, Flag = true, HotKey = action });
            await ReadUntil("ready");
            output.WriteLine($"{action}, budget={processors}: startup plus hidden toolbar {timer.Elapsed.TotalMilliseconds:F1} ms");
            process.Refresh();
            Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
            var expectedTitle = action == HotKeyAction.RegionCapture ? "SnapCut Capture" : "SnapCut Recording Controls";
            var preparedHandle = FindToolbar(process.Id, expectedTitle);
            Assert.False(IsWindowVisible(preparedHandle));
            await connection.SendAsync(new("activate") { Flag = true });
            await ReadUntil("started");
            await connection.SendAsync(new("prepare-capture-toolbar") { HotKey = action });
            await connection.SendAsync(new("prepare-capture-toolbar") { HotKey = action });
            if (action == HotKeyAction.RegionCapture)
            {
                using var image = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(500, 300));
                await connection.SendAsync(new("prepare-capture-frame") { NativeSelectionId = 7, Image = image });
            }
            await connection.SendAsync(new("command") { Sequence = 1, Command = "cancel" });
            var idle = await ReadUntil("idle");
            Assert.Equal(1, idle.Sequence);
            process.Refresh();
            Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
            await connection.SendAsync(new("retire") { Sequence = idle.Sequence });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    internal static IntPtr FindToolbar(int processId, string expectedTitle)
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((handle, parameter) =>
        {
            _ = GetWindowThreadProcessId(handle, out var owner);
            if (owner != (uint)processId) return true;
            var title = new char[256];
            var length = GetWindowText(handle, title, title.Length);
            if (new string(title, 0, length) == expectedTitle) windows.Add(handle);
            return true;
        }, IntPtr.Zero);
        return Assert.Single(windows);
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
