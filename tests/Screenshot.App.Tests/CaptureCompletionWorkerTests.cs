using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class CaptureCompletionWorkerTests
{
    [DesktopTheory]
    [InlineData("checkmark")]
    [InlineData("shortcut")]
    [InlineData("doubleclick")]
    public async Task RealFeatureWorkerCompletionShowsCenteredToast(string entry)
    {
        var pipeName = "SnapCut.Test.Completion." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var exe = Path.Combine(AppContext.BaseDirectory, "SnapCut.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--feature-worker"); start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--background"); start.ArgumentList.Add("--capture-startup");
        using var process = Process.Start(start)!;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var connection = new FeatureProcessConnection(pipe);
            async Task ReadUntil(string kind)
            {
                while (true)
                {
                    var message = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.NotEqual("error", message.Kind);
                    if (message.Kind == kind) return;
                }
            }
            var settings = AppSettings.CreateDefault() with { RequestAdministratorPrivileges = false,
                OpenSettingsOnStartup = false, AutoSaveOnComplete = false, Theme = AppTheme.GlassInk };
            await connection.SendAsync(new("bootstrap") { Settings = settings, Flag = true, HotKey = HotKeyAction.RegionCapture });
            await ReadUntil("ready");
            await connection.SendAsync(new("activate") { Flag = true });
            await ReadUntil("started");
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var snapshot = ScreenCaptureService.Capture(VirtualScreen.GetBounds());
            await connection.SendAsync(new("command") { Sequence = 1, HotKey = HotKeyAction.RegionCapture,
                NativeSelectionId = 5, InitialSelection = new(screen.Left + 100, screen.Top + 100, 280, 150), Image = snapshot });
            await ReadUntil("native-presented");
            var overlay = FeatureToolbarPreparationTests.FindToolbar(process.Id, "SnapCut Capture");
            if (entry == "shortcut")
                await connection.SendAsync(new("command") { Sequence = 2, HotKey = HotKeyAction.CompleteCapture });
            else if (entry == "doubleclick")
            {
                _ = SetForegroundWindow(overlay);
                await Task.Delay(150);
                Assert.Equal(overlay, GetForegroundWindow());
                Assert.True(GetCursorPos(out var oldPosition));
                try
                {
                    Assert.True(SetCursorPos(screen.Left + 180, screen.Top + 180));
                    await Task.Delay(50);
                    MouseEvent(2, 0, 0, 0, 0); MouseEvent(4, 0, 0, 0, 0);
                    await Task.Delay(80);
                    MouseEvent(2, 0, 0, 0, 0); MouseEvent(4, 0, 0, 0, 0);
                    await Task.Delay(50);
                }
                finally { SetCursorPos(oldPosition.X, oldPosition.Y); }
            }
            else await Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(overlay);
                var confirm = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ConfirmButton"));
                Assert.NotNull(confirm);
                ((InvokePattern)confirm.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            }).WaitAsync(TimeSpan.FromSeconds(8));
            nint toast = 0;
            for (var i = 0; i < 80 && toast == 0; i++)
            {
                toast = FindToast(process.Id);
                if (toast == 0) await Task.Delay(25);
            }
            Assert.NotEqual(nint.Zero, toast);
            Assert.True(IsWindowVisible(toast));
            Assert.True(GetWindowDisplayAffinity(toast, out var affinity));
            Assert.Equal(0u, affinity);
            Assert.True(GetWindowRect(toast, out var bounds));
            Assert.InRange(Math.Abs((bounds.Left + bounds.Right) / 2d - (screen.Left + screen.Width / 2d)), 0, 2);
            Assert.InRange(Math.Abs((bounds.Top + bounds.Bottom) / 2d - (screen.Top + screen.Height / 2d)), 0, 2);
            AssertNativePixels(toast, bounds);
            if (entry == "checkmark") ResidentStartupFeedback.DismissBeforeCapture();
            else await Task.Delay(1000);
            Assert.False(IsWindowVisible(toast));
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static void AssertNativePixels(nint toast, NativeRect bounds)
    {
        using var pixels = new NativeDesktopFrame(new(0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        Assert.True(PrintWindow(toast, pixels.DeviceContext, 1));
        using var bitmap = new System.Drawing.Bitmap(pixels.Region.Width, pixels.Region.Height, pixels.Stride,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb, pixels.Pixels);
        var colors = new HashSet<int>();
        for (var y = 5; y < bitmap.Height - 5; y++)
            for (var x = 5; x < bitmap.Width - 5; x++) colors.Add(bitmap.GetPixel(x, y).ToArgb());
        Assert.True(colors.Count > 8, "Native visible client must contain the message, not an empty HWND.");
        if (Environment.GetEnvironmentVariable("SNAPCUT_FEEDBACK_ARTIFACT") is { Length: > 0 } path)
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    internal static nint FindToast(int processId, string message = "截图成功")
    {
        nint result = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != processId) return true;
            var title = new char[256]; var length = GetWindowText(handle, title, title.Length);
            if (new string(title, 0, length) == "SnapCut " + message && IsWindowVisible(handle)) result = handle;
            return true;
        }, 0);
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate bool EnumCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint owner);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, [Out] char[] text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint dc, uint flags);
}
