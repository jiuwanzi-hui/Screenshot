using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class ResidentEditorPresentationTests(ITestOutputHelper output)
{
    [DesktopTheory]
    [InlineData(false, HotKeyAction.RegionCapture)]
    [InlineData(true, HotKeyAction.RegionCapture)]
    [InlineData(false, HotKeyAction.VideoRecording)]
    [InlineData(true, HotKeyAction.VideoRecording)]
    [InlineData(true, HotKeyAction.RegionCapture, 2, true)]
    [InlineData(true, HotKeyAction.RegionCapture, 8, true)]
    [InlineData(true, HotKeyAction.VideoRecording, 2, true)]
    [InlineData(true, HotKeyAction.VideoRecording, 8, true)]
    public async Task RealWorkerAcceptsCoverThenClosesAndReleasesItsProcess(bool prepareEditor, HotKeyAction action,
        int processors = 0, bool rapidSelection = false)
    {
        var pipeName = "SnapCut.Test.Selection." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var workerExe = Environment.GetEnvironmentVariable("SNAPCUT_TEST_WORKER_EXE") ??
            System.IO.Path.Combine(AppContext.BaseDirectory, "SnapCut.exe");
        var start = new ProcessStartInfo(workerExe)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(workerExe)! };
        start.ArgumentList.Add("--feature-worker"); start.ArgumentList.Add(pipeName); start.ArgumentList.Add("--background");
        start.ArgumentList.Add(FeatureStartupCompilation.Argument);
        var renderMode = Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_MODE");
        if (renderMode is "software" or "hardware")
            start.ArgumentList.Add("--" + renderMode + "-rendering");
        else if (!string.IsNullOrEmpty(renderMode))
            throw new InvalidOperationException("SNAPCUT_TEST_RENDER_MODE must be software or hardware.");
        // Optional external startup profiler; only the child is suspended.
        if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_STARTUP_TRACE_PORT") is { Length: > 0 } tracePort)
            start.Environment["DOTNET_DiagnosticPorts"] = tracePort + ",suspend,connect";
        var total = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        if (processors > 0)
            process.ProcessorAffinity = new IntPtr((1L << Math.Min(processors, Environment.ProcessorCount)) - 1);
        using var frame = await NativeDesktopFrame.CaptureAsync(VirtualScreen.GetBounds(), CancellationToken.None);
        ResidentSelectionWindow? resident = null;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var connection = new FeatureProcessConnection(pipe);
            async Task<FeatureMessage> ReadUntil(string kind)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    var message = await connection.ReadAsync().WaitAsync(timeout.Token);
                    if (message.Kind == "error") throw new InvalidOperationException(message.Text);
                    if (message.Kind == "prepare-capture" && message.NativeSelectionId == 0)
                    {
                        await connection.SendAsync(new("capture-hidden") { Sequence = message.Sequence, Flag = true });
                        continue;
                    }
                    if (message.Kind == kind) return message;
                }
            }
            var settings = AppSettings.CreateDefault() with { RequestAdministratorPrivileges = false,
                RegionCaptureHotKey = "Ctrl+Alt+F11", OpenSettingsHotKey = "Ctrl+Alt+F12",
                OcrHotKey = "", PinHotKey = "", OpenSettingsOnStartup = false };
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var selection = new ScreenRegion(bounds.X + bounds.Width / 3, bounds.Y + bounds.Height / 3, 360, 200);
            WpfTestHost.Invoke(() =>
            {
                resident = new(frame, settings, () => { });
                typeof(ResidentSelectionWindow).GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resident, selection);
                typeof(ResidentSelectionWindow).GetField("_finished", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resident, true);
                ShowResidentForTest(resident);
            });
            // The selection deadline runs while the real child starts. Never
            // wait for its ready message before starting the mouse-up clock.
            var selectionDeadline = total.Elapsed.TotalMilliseconds + 150;
            var selectionComplete = Task.Delay(150);
            await connection.SendAsync(new("bootstrap") { Settings = settings,
                Flag = prepareEditor, HotKey = action });
            await ReadUntil("ready");
            var title = action == HotKeyAction.RegionCapture ? "SnapCut Capture" : "SnapCut Recording Controls";
            var preparedHandle = prepareEditor ? FeatureToolbarPreparationTests.FindToolbar(process.Id, title) : IntPtr.Zero;
            await connection.SendAsync(new("activate") { Flag = prepareEditor });
            await ReadUntil("started");
            var preparedFrameSent = false;
            if (prepareEditor && (!rapidSelection || !selectionComplete.IsCompleted))
            {
                if (action == HotKeyAction.RegionCapture)
                {
                    await connection.SendAsync(new("prepare-capture-frame") { NativeSelectionId = 93, NativeFrame = frame });
                    preparedFrameSent = true;
                }
                if (!rapidSelection) await Task.Delay(350);
            }
            if (rapidSelection) await selectionComplete;
            _ = AllowSetForegroundWindow((uint)process.Id);
            var handoff = Stopwatch.StartNew();
            await connection.SendAsync(new("command") { Sequence = 1, NativeSelectionId = 93,
                NativeSelectionWindow = resident!.Handle.ToInt64(), NativeFrame = preparedFrameSent || action == HotKeyAction.VideoRecording ? null : frame,
                InitialSelection = selection, HotKey = action });
            var ready = await ReadUntil(action == HotKeyAction.RegionCapture ? "native-presented" : "prepare-capture");
            output.WriteLine($"{action}; Prepared={prepareEditor}; RenderMode={renderMode ?? "automatic"}; command-to-presented: {handoff.Elapsed.TotalMilliseconds:F1} ms.");
            if (rapidSelection) output.WriteLine($"COLD budget={processors}; process-start-to-presented={total.Elapsed.TotalMilliseconds:F1} ms; selection-deadline-to-presented={total.Elapsed.TotalMilliseconds - selectionDeadline:F1} ms.");
            process.Refresh();
            output.WriteLine($"Active worker total working set: {process.WorkingSet64 / 1048576d:F1} MiB; " +
                $"peak total working set: {process.PeakWorkingSet64 / 1048576d:F1} MiB; private commit: {process.PrivateMemorySize64 / 1048576d:F1} MiB.");
            Assert.Equal(93, ready.NativeSelectionId);
            var editor = FeatureToolbarPreparationTests.FindToolbar(process.Id, title);
            if (prepareEditor) Assert.Equal(preparedHandle, editor);
            Assert.NotEqual(IntPtr.Zero, editor);
            _ = GetWindowThreadProcessId(editor, out var ownerProcess);
            Assert.Equal((uint)process.Id, ownerProcess);
            WpfTestHost.Invoke(() => resident.Dispose());
            if (action == HotKeyAction.VideoRecording)
                await connection.SendAsync(new("capture-hidden") { Sequence = ready.Sequence, NativeSelectionId = 93, Flag = true });
            await Task.Delay(80);
            Assert.True(IsWindowVisible(editor));
            if (action == HotKeyAction.RegionCapture)
            {
                Assert.True(CaptureInputReadiness.OwnsForeground(editor));
                Assert.True(CaptureInputReadiness.ReceivesPointer(editor,
                    new Point(selection.X + selection.Width / 2, selection.Y + selection.Height / 2)));
            }
            var clock = Stopwatch.StartNew();
            await connection.SendAsync(action == HotKeyAction.RegionCapture
                ? new("command") { Sequence = 2, Command = "cancel-native", NativeSelectionId = 93 }
                : new("command") { Sequence = 2, HotKey = HotKeyAction.EndVideoRecording });
            await Task.Delay(300);
            var foregroundAfterClose = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foregroundAfterClose, out var foregroundProcess);
            var foregroundClass = new char[256];
            var classLength = GetClassName(foregroundAfterClose, foregroundClass, foregroundClass.Length);
            output.WriteLine($"After close, before retirement: foreground={foregroundAfterClose}, pid={foregroundProcess}, worker={process.Id}, visible={IsWindowVisible(foregroundAfterClose)}, class={new string(foregroundClass, 0, classLength)}; editor={editor}, editorVisible={IsWindowVisible(editor)}.");
            Assert.False(IsWindowVisible(editor));
            Assert.False(process.HasExited); // Focus must recover before memory/process retirement.
            Assert.NotEqual((uint)process.Id, foregroundProcess);
            var idle = await ReadUntil("idle");
            await connection.SendAsync(new("retire") { Sequence = idle.Sequence });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            output.WriteLine($"Cross-process handoff received input; worker exited {clock.ElapsedMilliseconds} ms after closing.");
        }
        finally
        {
            WpfTestHost.Invoke(() => resident?.Dispose());
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [DesktopFact]
    public async Task ClosingBeforeFirstFrameDoesNotDestroyTheResidentOrPublishAReadyMessage()
    {
        using var frame = await NativeDesktopFrame.CaptureAsync(VirtualScreen.GetBounds(), CancellationToken.None);
        WpfTestHost.Invoke(() =>
        {
            using var resident = new ResidentSelectionWindow(frame, AppSettings.CreateDefault(), () => { });
            using var pins = new PinnedImageManager();
            using var bitmap = new Bitmap(frame.Region.Width, frame.Region.Height, frame.Stride, PixelFormat.Format32bppRgb, frame.Pixels);
            var called = false;
            using var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                HistoryService = new(), PinnedImageManager = pins, StartOcrAsync = _ => Task.CompletedTask,
                InitialSelection = new(frame.Region.X + 120, frame.Region.Y + 160, 320, 180),
                ResidentSelectionWindow = resident.Handle, CapturePresented = () => called = true,
            }, new CapturedImage((Bitmap)bitmap.Clone(), frame.Region));
            overlay.Close();
            Assert.True(IsWindow(resident.Handle));
            Assert.Equal(IntPtr.Zero, GetWindow(resident.Handle, 4));
            Assert.False(called);
        });
    }

    // Uses the actual frozen desktop (no test patterns or global input).
    // Sample the selection and its surrounding mask across a cold and warm
    // native-to-WPF handoff, including the acknowledgement that retires it.
    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdAndRepeatedEditorHandoffKeepSelectedPixelsAndMaskStable(bool prepareEditor)
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var surface = VirtualScreen.GetBounds();
            var monitor = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var selection = new ScreenRegion(monitor.X + monitor.Width / 3,
                monitor.Y + monitor.Height / 3, monitor.Width / 3, monitor.Height / 3);
            using var frame = await NativeDesktopFrame.CaptureAsync(surface, CancellationToken.None);
            using var bitmap = new Bitmap(surface.Width, surface.Height, frame.Stride, PixelFormat.Format32bppRgb, frame.Pixels);
            using var snapshot = new CapturedImage(bitmap.Clone(new(0, 0, surface.Width, surface.Height), PixelFormat.Format32bppRgb), surface);
            var points = new[] {
                FindFlatSelectionPixel(frame, selection),
                new Point(monitor.X + 30, monitor.Y + monitor.Height / 2),
                new Point(monitor.Right - 30, monitor.Y + monitor.Height / 2),
                new Point(monitor.X + monitor.Width / 2, monitor.Y + 30),
                new Point(monitor.X + 30, monitor.Y + 30), // Unpositioned toolbar's initial corner.
                new Point(monitor.X + 30, monitor.Bottom - 30),
                new Point(monitor.Right - 30, monitor.Bottom - 30),
            };
            ResidentSelectionWindow? resident = null;
            CaptureOverlayWindow? overlay = null;
            CaptureOverlayWindow? prepared = null;
            PinnedImageManager? pins = null;
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var samples = new ConcurrentQueue<(long Time, uint[] Pixels)>();
            var anomalies = new ConcurrentQueue<string>();
            var phase = "before-preparation";
            var observedEditor = IntPtr.Zero;
            var observedMask = IntPtr.Zero;
            var clock = new Stopwatch();
            using var stop = new CancellationTokenSource();
            Task? sampling = null;
            EventHandler? observeToolbar = null;
            var visiblePositions = new List<System.Windows.Point>();
            try
            {
                WpfTestHost.Invoke(() =>
                {
                    resident = new(frame, AppSettings.CreateDefault(), () => { });
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(ResidentSelectionWindow).GetField("_region", flags)!.SetValue(resident, selection);
                    typeof(ResidentSelectionWindow).GetField("_finished", flags)!.SetValue(resident, true);
                    ShowResidentForTest(resident);
                });
                await Task.Run(() => DwmFlush());
                if (prepareEditor)
                {
                    WpfTestHost.Invoke(() =>
                    {
                        pins = new();
                        prepared = CaptureOverlayWindow.PrepareForResidentSelection(new CaptureOverlayOptions
                        {
                            SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                            HistoryService = new(), PinnedImageManager = pins,
                            StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                        });
                        observedEditor = new System.Windows.Interop.WindowInteropHelper(prepared).Handle;
                        observedMask = ((NativeSelectionMaskWindow)typeof(CaptureOverlayWindow)
                            .GetField("_nativeSelectionMask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prepared)!).Handle;
                    });
                }
                var expected = Read(points);
                clock.Start();
                sampling = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var stamp = clock.ElapsedMilliseconds;
                        var pixels = Read(points);
                        samples.Enqueue((stamp, pixels));
                        if (pixels.Select((pixel, index) => Difference(pixel, expected[index])).Max() > 4)
                        {
                            _ = DwmGetWindowAttribute(resident!.Handle, 14, out var coverCloak, sizeof(int));
                            _ = DwmGetWindowAttribute(observedEditor, 14, out var editorCloak, sizeof(int));
                            _ = DwmGetWindowAttribute(observedMask, 14, out var maskCloak, sizeof(int));
                            anomalies.Enqueue($"{stamp} ms {Volatile.Read(ref phase)}: cover visible={IsWindowVisible(resident.Handle)}, cloak={coverCloak}; editor visible={IsWindowVisible(observedEditor)}, cloak={editorCloak}; mask visible={IsWindowVisible(observedMask)}, cloak={maskCloak}.");
                        }
                        await Task.Delay(4);
                    }
                });
                Task<CaptureOverlayWindow?>? opening = null;
                WpfTestHost.Invoke(() =>
                {
                    pins ??= new();
                    observeToolbar = (_, _) =>
                    {
                        var window = overlay ?? prepared ?? System.Windows.Application.Current.Windows
                            .OfType<CaptureOverlayWindow>().FirstOrDefault(item => item.IsVisible);
                        if (window?.FindName("CaptureToolbar") is System.Windows.FrameworkElement toolbar &&
                            toolbar.IsVisible && toolbar.Opacity > 0)
                            visiblePositions.Add(toolbar.PointToScreen(new System.Windows.Point()));
                    };
                    System.Windows.Media.CompositionTarget.Rendering += observeToolbar;
                    Volatile.Write(ref phase, "begin-interactive");
                    opening = CaptureOverlayWindow.BeginInteractive(new CaptureOverlayOptions
                    {
                        SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
                        HistoryService = new(), PinnedImageManager = pins,
                        StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
                        InitialSelection = selection,
                        ResidentSelectionWindow = resident!.Handle,
                        CapturePresented = () => { resident!.Dispose(); presented.TrySetResult(); },
                    }, snapshot.Clone(), prepared);
                });
                overlay = await opening!;
                await presented.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Volatile.Write(ref phase, "presented");
                var handoffMilliseconds = clock.ElapsedMilliseconds;
                await Task.Delay(200);
                stop.Cancel();
                await sampling;
                var worst = samples.Select(sample => sample.Pixels.Select((pixel, index) => Difference(pixel, expected[index])).Max()).DefaultIfEmpty(0).Max();
                output.WriteLine($"Prepared={prepareEditor}; handoff {iteration}: {handoffMilliseconds} ms, {samples.Count} pixel samples, largest channel change {worst}.");
                if (worst > 4)
                {
                    foreach (var anomaly in anomalies) output.WriteLine(anomaly);
                    output.WriteLine("Baseline " + string.Join(",", expected.Select(value => value.ToString("X6", System.Globalization.CultureInfo.InvariantCulture))));
                    foreach (var sample in samples)
                        output.WriteLine($"{sample.Time} ms: " + string.Join(",", sample.Pixels.Select(value => value.ToString("X6", System.Globalization.CultureInfo.InvariantCulture))));
                }
                Assert.NotEmpty(samples);
                Assert.True(worst <= 4, $"Selected desktop/mask flashed: maximum RGB channel change {worst}.");
                WpfTestHost.Invoke(() =>
                {
                    Assert.NotEmpty(visiblePositions);
                    Assert.All(visiblePositions, position => Assert.True(position.Y >= selection.Y + selection.Height,
                        $"Toolbar rendered above its final below-selection location: {position}."));
                });
            }
            finally
            {
                stop.Cancel();
                if (sampling is not null) await sampling;
                WpfTestHost.Invoke(() =>
                {
                    if (observeToolbar is not null) System.Windows.Media.CompositionTarget.Rendering -= observeToolbar;
                    overlay?.Close(); overlay?.Dispose(); prepared?.Dispose(); resident?.Dispose(); pins?.Dispose();
                });
                await Task.Delay(100);
            }
        }
    }

    private static void ShowResidentForTest(ResidentSelectionWindow resident)
    {
        // These rendering tests have no physical hotkey event granting input.
        // Grant only the test resident the foreground; the Explorer tests
        // exercise production activation using a real menu and F1 instead.
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var thread = GetCurrentThreadId();
        var attached = foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            var show = resident.ShowAsync(CancellationToken.None);
            Assert.True(show.IsCompletedSuccessfully, $"Test resident activation failed: {show.Exception}");
        }
        finally { if (attached) _ = AttachThreadInput(thread, foregroundThread, false); }
        Assert.True(CaptureInputReadiness.OwnsForeground(resident.Handle),
            "Desktop environment did not grant the test resident foreground ownership.");
    }

    private static uint[] Read(Point[] points)
    {
        // Capture one composed frame. Separate GetPixel calls can span several
        // DWM frames and observe GDI z-order changes not yet presented together.
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        using var frame = new NativeDesktopFrame(new(left, top,
            points.Max(point => point.X) - left + 1, points.Max(point => point.Y) - top + 1));
        _ = DwmFlush();
        var dc = GetDC(IntPtr.Zero);
        try
        {
            Assert.True(NativeDesktopFrame.BitBlt(frame.DeviceContext, 0, 0, frame.Region.Width, frame.Region.Height,
                dc, left, top, 0x40CC0020));
            _ = GdiFlush();
            return points.Select(point => frame.PixelAt(point.X, point.Y)).ToArray();
        }
        finally { _ = ReleaseDC(IntPtr.Zero, dc); }
    }

    private static Point FindFlatSelectionPixel(NativeDesktopFrame frame, ScreenRegion selection)
    {
        // A text antialias edge can differ between the native DIB and WPF at
        // fractional DPI without any intervening flash. Sample a uniform 9x9
        // patch of the actual captured desktop, retaining the strict temporal
        // RGB tolerance rather than treating resampling as a presentation gap.
        for (var y = selection.Y + 16; y < selection.Y + selection.Height - 16; y += 8)
        for (var x = selection.X + 16; x < selection.X + selection.Width - 16; x += 8)
        {
            var color = frame.PixelAt(x, y);
            var uniform = true;
            for (var dy = -4; dy <= 4 && uniform; dy++)
            for (var dx = -4; dx <= 4; dx++)
                if (Difference(color, frame.PixelAt(x + dx, y + dy)) > 1) { uniform = false; break; }
            if (uniform) return new(x, y);
        }
        return new(selection.X + selection.Width / 2, selection.Y + selection.Height / 2);
    }

    private static int Difference(uint a, uint b)
    {
        var maximum = 0;
        for (var shift = 0; shift <= 16; shift += 8)
            maximum = Math.Max(maximum, Math.Abs((int)(a >> shift & 255) - (int)(b >> shift & 255)));
        return maximum;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, [Out] char[] name, int maximum);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint other, bool attach);
}
