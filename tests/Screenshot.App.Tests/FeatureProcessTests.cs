using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class FeatureProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenStartupCannotTakeInputBeforeTheFirstFrameAndCancellationUnblocksIt(bool cancel)
    {
        var name = "SnapCut.Test.FirstFrame." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connecting = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connecting;
        using var connection = new FeatureProcessConnection(server);
        using var child = new FeatureProcessConnection(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activations = 0;
        FeatureProcessHost? host = null;
        WpfTestHost.Invoke(() => host = new(Dispatcher.CurrentDispatcher,
            () => { activations++; return new("activate"); }, () => { }, _ => { }));
        using (host)
        {
            var reading = host!.ReadAsync(connection, ready, frame.Task, timeout.Token);
            await child.SendAsync(new("ready"));
            await Task.Delay(60);
            Assert.Equal(0, activations);
            Assert.False(ready.Task.IsCompleted);
            if (cancel)
            {
                frame.TrySetCanceled();
                await reading.WaitAsync(timeout.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready.Task);
                Assert.Equal(0, activations);
            }
            else
            {
                frame.TrySetResult();
                Assert.Equal("activate", (await child.ReadAsync().WaitAsync(timeout.Token)).Kind);
                Assert.Equal(1, activations);
                await child.SendAsync(new("started"));
                await ready.Task.WaitAsync(timeout.Token);
                child.Dispose();
                await reading.WaitAsync(timeout.Token);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHandoffWaitsForMatchingHideAcknowledgementAndRejectsCancelledSelection(bool accepted)
    {
        var name = "SnapCut.Test.Selection." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        FeatureProcessWorker? worker = null;
        Task? connected = null;
        WpfTestHost.Invoke(() =>
        {
            worker = new(Dispatcher.CurrentDispatcher, () => true,
                _ => Task.CompletedTask, () => { }, () => { });
            connected = worker.ConnectAsync(name);
        });
        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var connection = new FeatureProcessConnection(server);
            await connection.SendAsync(new("bootstrap") { Settings = AppSettings.CreateDefault() });
            Assert.Equal("ready", (await connection.ReadAsync()).Kind);
            await connected!;
            var handoff = worker!.PrepareResidentCaptureAsync(42);
            var request = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("prepare-capture", request.Kind);
            Assert.Equal(42, request.NativeSelectionId);
            await connection.SendAsync(new("capture-hidden") { Sequence = request.Sequence - 1, Flag = true });
            Assert.False(handoff.IsCompleted);
            await connection.SendAsync(new("capture-hidden") { Sequence = request.Sequence, Flag = accepted });
            if (accepted) await handoff;
            else await Assert.ThrowsAsync<OperationCanceledException>(() => handoff);
            // Rejection must not poison a subsequent request.
            var retry = worker.PrepareResidentCaptureAsync(43);
            var next = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(next.Sequence > request.Sequence);
            await connection.SendAsync(new("capture-hidden") { Sequence = next.Sequence, Flag = true });
            await retry;
        }
        finally { WpfTestHost.Invoke(() => worker?.Dispose()); }
    }

    [Fact]
    public void CompiledSettingsSerializationPreservesTheCompleteExistingWireFormat()
    {
        var message = new FeatureMessage("bootstrap") { Settings = AppSettings.CreateDefault() with
        {
            Theme = AppTheme.GlassInk, AutoOcrAfterCapture = true,
            TranslationTargetLanguage = "ja", MouseLongPressMilliseconds = 420,
        }};
        var previous = JsonSerializer.SerializeToNode(message);
        var compiled = JsonSerializer.SerializeToNode(message, FeatureJsonContext.Default.FeatureMessage);
        Assert.True(JsonNode.DeepEquals(previous, compiled));
        var restored = JsonSerializer.Deserialize(compiled, FeatureJsonContext.Default.FeatureMessage)!;
        Assert.True(JsonNode.DeepEquals(previous, JsonSerializer.SerializeToNode(restored)));
    }

    [Fact]
    public async Task PipePreservesPixelsScreenOriginPointerAndSettingsWithoutCompression()
    {
        using var stream = new MemoryStream();
        using var connection = new FeatureProcessConnection(stream);
        using var image = new CapturedImage(new Bitmap(11, 7), new ScreenRegion(-1920, 140, 11, 7));
        image.Bitmap.SetPixel(3, 4, Color.FromArgb(128, 21, 44, 65));
        var settings = AppSettings.CreateDefault() with { AutoOcrAfterCapture = true };
        await connection.SendAsync(new("command")
        {
            Sequence = 7, HotKey = HotKeyAction.PinImage, Image = image, Settings = settings,
            Button = CapturePointerButton.Right, PointerX = -123, PointerY = 456,
            EnterPickerOnRelease = true,
            LastCaptureRegion = new ScreenRegion(-100, 25, 400, 300),
        });
        stream.Position = 0;
        var result = await connection.ReadAsync();
        using var received = result.Image;
        Assert.NotNull(received);
        Assert.Equal(image.SourceRegion, received.SourceRegion);
        Assert.Equal(image.Bitmap.GetPixel(3, 4), received.Bitmap.GetPixel(3, 4));
        Assert.Equal(7, result.Sequence);
        Assert.Equal(HotKeyAction.PinImage, result.HotKey);
        Assert.Equal(CapturePointerButton.Right, result.Button);
        Assert.Equal(-123, result.PointerX);
        Assert.Equal(456, result.PointerY);
        Assert.True(result.EnterPickerOnRelease);
        Assert.Equal(new ScreenRegion(-100, 25, 400, 300), result.LastCaptureRegion);
        Assert.True(result.Settings!.AutoOcrAfterCapture);
    }

    [Fact]
    public async Task TruncatedPixelTransferFailsInsteadOfReturningPartialScreenshot()
    {
        using var stream = new MemoryStream();
        using var connection = new FeatureProcessConnection(stream);
        using var image = new CapturedImage(new Bitmap(8, 8));
        await connection.SendAsync(new("command") { Image = image });
        stream.SetLength(stream.Length - 5);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => connection.ReadAsync());
    }

    [Fact]
    public void IndependentPlacementStoresMergeChangesInsteadOfOverwritingOtherProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"snapcut-placement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "placements.json");
            var resident = new WindowPlacementStore(path);
            var feature = new WindowPlacementStore(path);
            var floating = new WindowPlacementRecord(1, 2, 101, 102, false);
            var settings = new WindowPlacementRecord(3, 4, 503, 604, false);
            Assert.True(resident.TrySave("floating", floating));
            Assert.True(feature.TrySave("settings", settings));
            Assert.True(resident.TryGet("settings", out var stored));
            Assert.Equal(settings, stored);
            Assert.True(feature.TryGet("floating", out stored));
            Assert.Equal(floating, stored);
            Assert.True(feature.TryRemove("settings"));
            Assert.True(resident.TrySave("floating", floating with { Left = 2 }));
            Assert.False(feature.TryGet("settings", out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WorkerRetirementRequiresNoTasksAndAcknowledgementOfLatestCommand()
    {
        var name = "SnapCut.Test.Features." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        FeatureProcessWorker? worker = null;
        var busy = true;
        var retired = false;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? connected = null;
        WpfTestHost.Invoke(() =>
        {
            worker = new(Dispatcher.CurrentDispatcher, () => busy,
                message => { if (message.Kind == "command") received.TrySetResult(); return Task.CompletedTask; },
                () => retired = true, () => { });
            connected = worker.ConnectAsync(name);
        });
        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var connection = new FeatureProcessConnection(server);
            await connection.SendAsync(new("bootstrap") { Settings = AppSettings.CreateDefault() });
            Assert.Equal("ready", (await connection.ReadAsync()).Kind);
            await connected!;
            await connection.SendAsync(new("command") { Sequence = 1, Command = "settings" });
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.SendAsync(new("retire") { Sequence = 1 });
            Assert.Equal("busy", (await connection.ReadAsync()).Kind);
            Assert.False(retired);
            WpfTestHost.Invoke(() => { busy = false; worker!.ScheduleIdle(); });
            var idle = await connection.ReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("idle", idle.Kind);
            Assert.Equal(1, idle.Sequence);
            await connection.SendAsync(new("retire") { Sequence = 0 });
            Assert.Equal("busy", (await connection.ReadAsync()).Kind);
            // A hotkey arriving while the parent acknowledges an idle worker
            // invalidates that acknowledgement even if the new command is fast.
            await connection.SendAsync(new("command") { Sequence = 2, Command = "settings" });
            await connection.SendAsync(new("retire") { Sequence = 1 });
            Assert.Equal("busy", (await connection.ReadAsync()).Kind);
            Assert.False(retired);
            using (FeatureActivity.Begin())
            {
                await connection.SendAsync(new("retire") { Sequence = 2 });
                Assert.Equal("busy", (await connection.ReadAsync()).Kind);
            }
            await connection.SendAsync(new("retire") { Sequence = 2 });
            for (var i = 0; i < 50 && !retired; i++) await Task.Delay(20);
            Assert.True(retired);
        }
        finally { WpfTestHost.Invoke(() => worker?.Dispose()); }
    }
}
