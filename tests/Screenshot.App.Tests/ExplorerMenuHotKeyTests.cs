using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class ExplorerMenuHotKeyTests(ITestOutputHelper output)
{
    [DesktopFact]
    public async Task HoldingCaptureChordDeliversOneSystemHotKeyUntilKeyUp()
    {
        GlobalHotKeyManager? manager = null;
        var count = 0;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                manager = new GlobalHotKeyManager { ForegroundMenuActiveOverride = () => false };
                Assert.True(manager.Apply([new HotKeyBinding(HotKeyAction.RegionCapture,
                    new HotKeyGesture(HotKeyModifiers.Alt, 'A'))]).IsSuccess);
                manager.HotKeyPressed += (_, _) => Interlocked.Increment(ref count);
            });
            Key(0x12, false); Key('A', false);
            await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
            Key('A', false); Key('A', false); // Hardware auto-repeat, still held.
            await Task.Delay(120);
            Assert.Equal(1, Volatile.Read(ref count));
            Key('A', true); Key('A', false); // A new press still works with Alt held.
            await WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        }
        finally
        {
            Key('A', true); Key(0x12, true);
            WpfTestHost.Invoke(() => manager?.Dispose());
        }
    }

    [DesktopFact]
    public async Task AltACapturesNonActivatingGlassTrayMenuAndTransfersInputToSelection()
    {
        GlobalHotKeyManager? manager = null;
        GlassTrayMenuWindow? menu = null;
        CaptureOverlayWindow? overlay = null;
        PinnedImageManager? pins = null;
        var opened = new TaskCompletionSource<CaptureOverlayWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                manager = new GlobalHotKeyManager();
                Assert.True(manager.Apply([new HotKeyBinding(HotKeyAction.RegionCapture,
                    new HotKeyGesture(HotKeyModifiers.Alt, 'A'))]).IsSuccess);
                pins = new PinnedImageManager();
                manager.HotKeyPressed += async (_, args) =>
                {
                    try
                    {
                        var frame = args.DetachPreCapturedScreen();
                        manager.SetCaptureOverlayActive(true);
                        manager.SetMouseShortcutsSuspended(true);
                        overlay = await CaptureOverlayWindow.BeginInteractive(new CaptureOverlayOptions
                        {
                            SaveDirectory = Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                            HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                            StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                            DeferInitialScreenCapture = true, DeferInitialColorPickerActivation = true,
                        }, frame);
                        opened.TrySetResult(overlay!);
                    }
                    catch (Exception exception) { opened.TrySetException(exception); }
                };
                menu = new GlassTrayMenuWindow([new("菜单截图检查", false, null), new("区域截图", true, () => { })]);
                var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                menu.ShowAt(new Point(area.Left + 80, area.Top + 80));
                Assert.False(menu.IsActive);
            });
            await Task.Delay(350);
            Key(0x12, false);
            await Task.Delay(35);
            Key('A', false); Key('A', true); Key(0x12, true);
            overlay = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
            {
                var ready = false;
                WpfTestHost.Invoke(() => ready = overlay.IsActive && overlay.Opacity == 1 && Snapshot(overlay) is not null && !menu!.IsVisible);
                return ready;
            });
        }
        finally
        {
            Key('A', true); Key(0x12, true);
            WpfTestHost.Invoke(() => { menu?.Close(); overlay?.Close(); overlay?.Dispose(); pins?.Dispose(); manager?.Dispose(); });
        }
    }

    [DesktopTheory]
    [InlineData(0, false, false)]
    [InlineData(5000, false, false)]
    [InlineData(0, true, false)]
    [InlineData(700, true, false)]
    [InlineData(0, false, true)]
    [InlineData(700, true, true)]
    public async Task ShortcutCapturesAnActualExplorerMenu(int menuAgeMilliseconds, bool recentContextGesture, bool classicMenuWithF1)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-menu-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Menu capture fixture.txt");
        await File.WriteAllTextAsync(file, "SnapCut context menu regression fixture.");
        dynamic? shell = null;
        dynamic? explorer = null;
        GlobalHotKeyManager? manager = null;
        PinnedImageManager? pins = null;
        CaptureOverlayWindow? overlay = null;
        IntPtr explorerHandle = IntPtr.Zero;
        var originalPointer = System.Windows.Forms.Cursor.Position;
        var gesture = classicMenuWithF1
            ? new HotKeyGesture(HotKeyModifiers.None, 0x70)
            : new HotKeyGesture(HotKeyModifiers.Alt, 'A');
        var opened = new TaskCompletionSource<CaptureOverlayWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                shell!.Open(directory);
                manager = new GlobalHotKeyManager();
                Assert.True(manager.Apply([new HotKeyBinding(HotKeyAction.RegionCapture,
                    gesture)]).IsSuccess);
                pins = new PinnedImageManager();
                manager.HotKeyPressed += async (_, args) =>
                {
                    try
                    {
                        var frame = args.DetachPreCapturedScreen();
                        manager.SetCaptureOverlayActive(true);
                        overlay = await CaptureOverlayWindow.BeginInteractive(new CaptureOverlayOptions
                        {
                            SaveDirectory = directory, KeepHistory = false, HistoryLimit = 0,
                            HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                            StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                            DeferInitialScreenCapture = true,
                            DeferInitialColorPickerActivation = true,
                        }, frame);
                        opened.TrySetResult(overlay!);
                    }
                    catch (Exception exception) { opened.TrySetException(exception); }
                };
            });
            await WaitUntilAsync(() =>
            {
                var found = false;
                WpfTestHost.Invoke(() =>
                {
                    foreach (dynamic window in shell!.Windows())
                    {
                        if (!Uri.TryCreate((string)window.LocationURL, UriKind.Absolute, out var url) ||
                            !string.Equals(url.LocalPath.TrimEnd('\\'), directory, StringComparison.OrdinalIgnoreCase)) continue;
                        explorer = window;
                        explorerHandle = new IntPtr((long)window.HWND);
                        found = true;
                        break;
                    }
                });
                return found;
            });
            WpfTestHost.Invoke(() =>
            {
                explorer!.Document.SelectItem(explorer.Document.Folder.ParseName(Path.GetFileName(file)), 0x1D);
                Assert.True(SetForegroundWindow(explorerHandle), "Explorer could not take foreground focus.");
            });
            await Task.Delay(250);
            if (recentContextGesture)
                WpfTestHost.Invoke(() => typeof(GlobalHotKeyManager).GetMethod("MarkRecentContextMenuGesture",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null));
            if (classicMenuWithF1)
            {
                Assert.True(GetWindowRect(explorerHandle, out var explorerBounds));
                Assert.True(SetCursorPos(explorerBounds.Right - 100, explorerBounds.Top + 250));
                Key(0x10, false);
                Pointer(0x0008); Pointer(0x0010); // Shift + right click, matching the reported repro.
                Key(0x10, true);
            }
            else { Key(0x5D, false); Key(0x5D, true); }
            Rectangle menu = default;
            await WaitUntilAsync(() => (menu = FindMenu(explorerHandle)).Width > 0);
            await Task.Delay(250 + menuAgeMilliseconds);
            output.WriteLine($"Explorer popup: {menu}, age={menuAgeMilliseconds} ms");
            using var expected = ScreenCaptureService.CaptureIncludingLayeredWindows(
                new ScreenRegion(menu.X, menu.Y, menu.Width, menu.Height));
            if (classicMenuWithF1) { Key(0x70, false); Key(0x70, true); }
            else
            {
                Key(0x12, false);
                await Task.Delay(35);
                Key('A', false); Key('A', true); Key(0x12, true);
            }
            overlay = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
            {
                var ready = false;
                WpfTestHost.Invoke(() => ready = overlay.IsActive && overlay.Opacity == 1 && Snapshot(overlay) is not null);
                return ready;
            });
            double mismatch = 1;
            WpfTestHost.Invoke(() =>
            {
                var actual = Snapshot(overlay)!;
                var origin = actual.SourceRegion!.Value;
                var changed = 0;
                var count = 0;
                for (var y = 12; y < menu.Height - 12; y += 4)
                for (var x = 12; x < menu.Width - 12; x += 4)
                {
                    var a = expected.Bitmap.GetPixel(x, y);
                    var b = actual.Bitmap.GetPixel(menu.X - origin.X + x, menu.Y - origin.Y + y);
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 45) changed++;
                    count++;
                }
                mismatch = changed / (double)count;
                if (Environment.GetEnvironmentVariable("SNAPCUT_MENU_PREVIEW_DIR") is { Length: > 0 } previews)
                {
                    Directory.CreateDirectory(previews);
                    expected.Bitmap.Save(Path.Combine(previews, $"explorer-{menuAgeMilliseconds}-before.png"));
                    using var crop = actual.Bitmap.Clone(new Rectangle(menu.X - origin.X, menu.Y - origin.Y,
                        menu.Width, menu.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    crop.Save(Path.Combine(previews, $"explorer-{menuAgeMilliseconds}-capture.png"));
                }
            });
            output.WriteLine($"Changed popup pixels: {mismatch:P1}");
            Assert.True(mismatch < 0.08, $"The captured Explorer menu lost {mismatch:P1} of its pixels.");
        }
        finally
        {
            Key('A', true); Key(0x12, true); Key(0x1B, false); Key(0x1B, true);
            Key(0x70, true); Key(0x10, true);
            if (classicMenuWithF1) _ = SetCursorPos(originalPointer.X, originalPointer.Y);
            WpfTestHost.Invoke(() =>
            {
                overlay?.Close(); overlay?.Dispose(); pins?.Dispose(); manager?.Dispose();
                if (explorer is not null) explorer.Quit();
                if (shell is not null) Marshal.FinalReleaseComObject(shell);
            });
            File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static CapturedImage? Snapshot(CaptureOverlayWindow overlay) =>
        (CapturedImage?)typeof(CaptureOverlayWindow).GetField("_screenSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 8;
        while (!predicate())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "The interactive Explorer desktop was not ready.");
            await Task.Delay(40);
        }
    }

    private static Rectangle FindMenu(IntPtr owner)
    {
        if (GetWindowThreadProcessId(owner, out var process) == 0) return default;
        Rectangle result = default;
        bool Inspect(IntPtr handle, IntPtr _)
        {
            if (GetWindowThreadProcessId(handle, out var candidateProcess) == 0) return true;
            if (candidateProcess != process || !IsWindowVisible(handle)) return true;
            var name = new char[256];
            var length = GetClassName(handle, name, name.Length);
            if (length == 0) return true;
            var className = new string(name, 0, length);
            if ((className.Contains("Popup", StringComparison.OrdinalIgnoreCase) || className == "#32768") &&
                GetWindowRect(handle, out var rectangle) && rectangle.Right - rectangle.Left > 100 &&
                rectangle.Bottom - rectangle.Top > 100)
            {
                result = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
                return false;
            }
            return true;
        }
        EnumWindows(Inspect, IntPtr.Zero);
        if (result.IsEmpty) EnumChildWindows(owner, Inspect, IntPtr.Zero);
        return result;
    }

    private static void Key(int key, bool up)
    {
        var input = new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = up ? 2u : 0 } };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<Input>()));
    }
    private static void Pointer(uint flags) => Assert.Equal(1u,
        SendInput(1, [new Input { Type = 0, Mouse = new MouseInput { Flags = flags } }], Marshal.SizeOf<Input>()));
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; [FieldOffset(8)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, [Out] char[] name, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr parameter);
}
