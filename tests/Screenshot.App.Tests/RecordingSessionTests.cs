using System.IO;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using ScreenRecorderLib;

namespace Screenshot.App.Tests;

public sealed class RecordingSessionTests
{
    [Fact]
    public async Task AllocatorFailureBeforeFirstFrameRetriesSoftwareEvenWithoutWgc()
    {
        var failed = new FakeBackend { StartAction = b => b.SignalFailed("Write error (0xc00d4a3e) in video encoder") };
        var replacement = new FakeBackend { StartAction = b => b.SignalStarted() };
        var profiles = new List<RecordingProfile>();
        using var session = new RecordingSession(Path.Combine(Path.GetTempPath(), $"SnapCut-fake-{Guid.NewGuid():N}.mp4"),
            RecordingProfile.Default, false, profile =>
            {
                profiles.Add(profile);
                if (profiles.Count == 1) return failed;
                Assert.True(failed.Disposed);
                return replacement;
            });
        var observed = new List<string>();
        var diagnostics = new List<string>();
        session.DiagnosticFailure += (stage, error) => diagnostics.Add($"{stage}: {error}");
        session.AttemptFailed += (_, error) => observed.Add(error);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { RecordingProfile.Default, RecordingProfile.Software }, profiles);
        Assert.Contains("0xc00d4a3e", Assert.Single(observed));
        Assert.Contains("0xc00d4a3e", session.StartupFallbackReason);
        Assert.Contains("startup-attempt-1", Assert.Single(diagnostics));
        Assert.Contains("InvalidOperationException", diagnostics[0]);
        Assert.True((await session.StopAsync()).IsSuccess);
    }

    [Fact]
    public async Task RuntimeEncoderFailureIsRememberedWithoutReplacingExistingFootage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SnapCut-fake-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(path, "existing-footage");
        try
        {
            var backend = new FakeBackend { StartAction = b => b.SignalStarted() };
            var calls = 0;
            using var session = new RecordingSession(path, RecordingProfile.Default, true, _ => { calls++; return backend; });
            var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.AttemptFailed += (profile, error) =>
            {
                Assert.True(profile.HardwareEncoding);
                Assert.True(RecordingEncoderHealth.IsEncoderFailure(error));
                reported.TrySetResult();
            };
            await session.StartAsync();
            backend.SignalFailed("Write error (0xc00d4a3e) in video encoder");
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await session.StopAsync();
            Assert.False(result.IsSuccess);
            Assert.Contains("软件编码", result.ErrorMessage);
            Assert.Equal(1, calls);
            Assert.Equal("existing-footage", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(VideoRecordingCodec.H264, 30)]
    [InlineData(VideoRecordingCodec.H265, 60)]
    public void EncoderOnlyFallbackKeepsCaptureApiAndAllUserOutputOptions(VideoRecordingCodec codec, int fps)
    {
        var options = RegionVideoRecorder.CreateOptions("DISPLAY1", new(0, 0, 1920, 1080),
            true, true, codec, fps, "USB-Mic", RecordingProfile.Software);
        Assert.False(options.VideoEncoderOptions.IsHardwareEncodingEnabled);
        Assert.False(options.VideoEncoderOptions.IsThrottlingDisabled);
        Assert.False(options.OutputOptions.IsVideoFramePreviewEnabled);
        Assert.Equal(fps, options.VideoEncoderOptions.Framerate);
        Assert.Equal(codec == VideoRecordingCodec.H265 ? typeof(H265VideoEncoder) : typeof(H264VideoEncoder), options.VideoEncoderOptions.Encoder.GetType());
        Assert.Equal(75, options.VideoEncoderOptions.Quality);
        Assert.True(options.AudioOptions.IsInputDeviceEnabled);
        Assert.True(options.AudioOptions.IsOutputDeviceEnabled);
        Assert.Equal("USB-Mic", options.AudioOptions.AudioInputDevice);
        var source = Assert.IsType<DisplayRecordingSource>(Assert.Single(options.SourceOptions.RecordingSources));
        Assert.Equal(RecorderApi.DesktopDuplication, source.RecorderApi);
        Assert.Equal(1920, source.OutputSize.Width);
        Assert.Equal(1080, source.OutputSize.Height);
    }

    [Theory]
    [InlineData("wjIddDriver Device")]
    [InlineData("OrayIddDriver Device")]
    [InlineData("Microsoft Remote Display Adapter")]
    public void VirtualTargetUsesCompatibleCapture(string description)
    {
        var profile = RecordingCompatibility.Choose("DISPLAY2",
            [new("DISPLAY1", "Intel(R) UHD Graphics 730", true, false), new("DISPLAY2", description, true, false)], false);
        Assert.Equal(RecordingProfile.Compatible, profile);
    }

    [Fact]
    public void InstalledVirtualAdapterDoesNotDisableThePhysicalIntelDisplay()
    {
        var displays = new[] { new RecordingDisplay("DISPLAY1", "Intel(R) UHD Graphics 730", true, false),
            new RecordingDisplay("DISPLAY2", "wjIddDriver Device", true, false) };
        Assert.Equal(RecordingProfile.Default, RecordingCompatibility.Choose("DISPLAY1", displays, false));
        Assert.Equal(RecordingProfile.Compatible, RecordingCompatibility.Choose("DISPLAY1", displays, true));
        Assert.Equal(RecordingProfile.Compatible, RecordingCompatibility.Choose("DISPLAY1", displays, false, true));
    }

    [Theory]
    [InlineData(VideoRecordingCodec.H264, 30)]
    [InlineData(VideoRecordingCodec.H265, 60)]
    public void CompatibleOptionsRetainOutputQualityAndAudio(VideoRecordingCodec codec, int fps)
    {
        var region = new ScreenRegion(12, 24, 1920, 1080);
        var normal = RegionVideoRecorder.CreateOptions("DISPLAY1", region, true, true, codec, fps, "USB-Mic", RecordingProfile.Default);
        var compatible = RegionVideoRecorder.CreateOptions("DISPLAY1", region, true, true, codec, fps, "USB-Mic", RecordingProfile.Compatible);
        Assert.Equal(normal.VideoEncoderOptions.Encoder.GetType(), compatible.VideoEncoderOptions.Encoder.GetType());
        Assert.Equal(normal.VideoEncoderOptions.Quality, compatible.VideoEncoderOptions.Quality);
        Assert.Equal(fps, compatible.VideoEncoderOptions.Framerate);
        Assert.False(compatible.VideoEncoderOptions.IsHardwareEncodingEnabled);
        Assert.True(compatible.AudioOptions.IsOutputDeviceEnabled);
        Assert.True(compatible.AudioOptions.IsInputDeviceEnabled);
        Assert.Equal("USB-Mic", compatible.AudioOptions.AudioInputDevice);
        var source = Assert.IsType<DisplayRecordingSource>(Assert.Single(compatible.SourceOptions.RecordingSources));
        Assert.Equal(RecorderApi.WindowsGraphicsCapture, source.RecorderApi);
        Assert.Equal(region.Width, source.SourceRect.Width);
        Assert.Equal(region.Height, source.SourceRect.Height);
        Assert.Equal(region.X, source.SourceRect.Left);
        Assert.Equal(region.Y, source.SourceRect.Top);
    }

    [Fact]
    public async Task StartupWaitsForNativeConfirmationAndDoesNotSampleOrStopDarkVideo()
    {
        var backend = new FakeBackend();
        using var session = Session(_ => backend);
        var start = session.StartAsync();
        await backend.StartCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RegionVideoRecorderState.Starting, session.State);
        Assert.False(start.IsCompleted);
        backend.SignalStarted();
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(2400); // Exceeds the old three-black-snapshot auto-stop deadline.
        Assert.Equal(RegionVideoRecorderState.Recording, session.State);
        Assert.Equal(0, backend.Snapshots);
        Assert.Equal(0, backend.Stops);
    }

    [Fact]
    public async Task FailedStartupReleasesOldInstanceAndRetriesOnceWithoutAFalseFailure()
    {
        var first = new FakeBackend { StartAction = b => b.SignalFailed("硬件编码器初始化失败") };
        var second = new FakeBackend { StartAction = b => b.SignalStarted() };
        var profiles = new List<RecordingProfile>();
        using var session = Session(profile =>
        {
            profiles.Add(profile);
            if (profiles.Count == 1) return first;
            Assert.True(first.Disposed);
            return second;
        });
        var failures = 0;
        session.Failed += _ => failures++;
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { RecordingProfile.Default, RecordingProfile.Compatible }, profiles);
        Assert.Contains("硬件编码器初始化失败", session.StartupFallbackReason);
        Assert.Equal(0, failures);
        Assert.Equal(RegionVideoRecorderState.Recording, session.State);
        Assert.True((await session.StopAsync()).IsSuccess);
        Assert.Equal(1, second.Stops);
    }

    [Fact]
    public async Task BothStartupFailuresAreReportedAndNoThirdAttemptIsMade()
    {
        var attempts = 0;
        using var session = Session(profile =>
        {
            attempts++;
            return new FakeBackend { StartAction = b => b.SignalFailed(profile.HardwareEncoding ? "硬件失败" : "兼容失败") };
        });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.Contains("硬件失败", exception.Message);
        Assert.Contains("兼容失败", exception.Message);
        Assert.Equal(2, attempts);
        Assert.Equal(RegionVideoRecorderState.Failed, session.State);
        Assert.False((await session.StopAsync()).IsSuccess);
    }

    [Fact]
    public async Task FailureAfterStartingDoesNotSilentlyRestartOrReplaceFootage()
    {
        var count = 0;
        var backend = new FakeBackend { StartAction = b => b.SignalStarted() };
        using var session = Session(_ => { count++; return backend; });
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Failed += error => failure.TrySetResult(error);
        await session.StartAsync();
        backend.SignalFailed("显示设备丢失");
        Assert.Equal("显示设备丢失", await failure.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, count);
        Assert.Equal(RegionVideoRecorderState.Failed, session.State);
    }

    [Fact]
    public async Task DisposalWaitsForSnapshotAndRejectsFurtherNativeAccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend { StartAction = b => b.SignalStarted(), SnapshotAction = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
        } };
        using var session = Session(_ => backend);
        await session.StartAsync();
        using var output = new MemoryStream();
        var snapshot = Task.Run(() => session.TryTakeSnapshot(output));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dispose = Task.Run(session.Dispose);
        try
        {
            await Task.Delay(50);
            Assert.False(dispose.IsCompleted);
            Assert.False(backend.Disposed);
        }
        finally { release.Set(); }
        Assert.True(await snapshot);
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(backend.Disposed);
        Assert.False(session.TryTakeSnapshot(output));
        Assert.Equal(1, backend.Snapshots);
    }

    [Fact]
    public async Task DelayedCallbacksFromAFailedAttemptCannotFailTheReplacement()
    {
        Action<string>? staleCallback = null;
        var first = new FakeBackend { StartAction = b => { staleCallback = b.Failed; b.SignalFailed("启动失败"); } };
        var second = new FakeBackend { StartAction = b => b.SignalStarted() };
        var count = 0;
        using var session = Session(_ => ++count == 1 ? first : second);
        await session.StartAsync();
        staleCallback!("旧录制器的延迟失败");
        Assert.True((await session.StopAsync()).IsSuccess);
        Assert.Equal(RegionVideoRecorderState.Completed, session.State);
    }

    [Fact]
    public async Task PendingStartupCanBeCancelledAndReleasesItsBackend()
    {
        var backend = new FakeBackend();
        using var session = Session(_ => backend);
        var start = session.StartAsync();
        await backend.StartCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = session.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Null((await stop.WaitAsync(TimeSpan.FromSeconds(3))).ErrorMessage);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task SlowDeviceCreationDoesNotBlockStatusReadsOrStartAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var backend = new FakeBackend();
        using var session = Session(_ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            return backend;
        });
        var start = session.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<RegionVideoRecordingResult> stop;
        try
        {
            Assert.Equal(RegionVideoRecorderState.Starting,
                await Task.Run(() => session.State).WaitAsync(TimeSpan.FromSeconds(1)));
            stop = session.StopAsync();
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Null((await stop.WaitAsync(TimeSpan.FromSeconds(3))).ErrorMessage);
        Assert.False(backend.StartCalled.Task.IsCompleted);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task StartupTimeoutIsBoundedAndDoesNotRetryAnAlreadyCompatibleBackend()
    {
        var backend = new FakeBackend();
        using var session = new RecordingSession(Path.Combine(Path.GetTempPath(), $"SnapCut-fake-{Guid.NewGuid():N}.mp4"),
            RecordingProfile.Compatible, true, _ => backend, TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(session.StartAsync);
        Assert.Contains("启动超时", error.Message);
        Assert.True(backend.Disposed);
        Assert.Equal(RegionVideoRecorderState.Failed, session.State);
    }

    private static RecordingSession Session(Func<RecordingProfile, IRecordingBackend> factory) =>
        new(Path.Combine(Path.GetTempPath(), $"SnapCut-fake-{Guid.NewGuid():N}.mp4"), RecordingProfile.Default, true, factory);

    [Fact]
    public async Task NativeSoftwareEncoderRecordsBlackImageOffScreenAndFinalizesMp4()
    {
        // This is an image source, not a desktop/window capture. Nothing is
        // displayed and no global input or user's screen content is captured.
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-native-recording", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "black.png");
        var videoPath = Path.Combine(directory, "black.mp4");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(320, 240))
            {
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.Black);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }
            using var session = new RecordingSession(videoPath, RecordingProfile.Compatible, false, _ =>
                new NativeRecordingBackend(new RecorderOptions
                {
                    SourceOptions = new SourceOptions { RecordingSources = [new ImageRecordingSource(imagePath)] },
                    OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
                    VideoEncoderOptions = new VideoEncoderOptions
                    {
                        Encoder = new H264VideoEncoder(), Framerate = 24, IsFixedFramerate = true,
                        IsHardwareEncodingEnabled = false, Quality = 75, IsMp4FastStartEnabled = true,
                    },
                    AudioOptions = new AudioOptions { IsAudioEnabled = false },
                }));
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(2500);
            Assert.Equal(RegionVideoRecorderState.Recording, session.State);
            session.Pause();
            Assert.Equal(RegionVideoRecorderState.Paused, session.State);
            session.Resume();
            var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(videoPath, result.FilePath);
            Assert.True(new FileInfo(videoPath).Length > 1024);
            var header = new byte[12];
            using var input = File.OpenRead(videoPath);
            input.ReadExactly(header);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(header, 4, 4));
        }
        finally
        {
            File.Delete(videoPath);
            File.Delete(imagePath);
            Directory.Delete(directory); // Unique test-owned directory, nonrecursive.
        }
    }

    private sealed class FakeBackend : IRecordingBackend
    {
        public event Action? Started;
        public event Action<string?>? Completed;
        public Action<string>? Failed;
        event Action<string>? IRecordingBackend.Failed { add => Failed += value; remove => Failed -= value; }
        internal readonly TaskCompletionSource StartCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<FakeBackend>? StartAction;
        internal Action? SnapshotAction;
        internal bool Disposed;
        internal int Snapshots;
        internal int Stops;
        internal void SignalStarted() => Started?.Invoke();
        internal void SignalFailed(string error) => Failed?.Invoke(error);
        public void Start(string path) { StartCalled.TrySetResult(); StartAction?.Invoke(this); }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { Stops++; Completed?.Invoke("recording.mp4"); }
        public void SetAudioSources(bool systemAudio, bool microphone) { }
        public bool TakeSnapshot(Stream output) { Assert.False(Disposed); Snapshots++; SnapshotAction?.Invoke(); return true; }
        public void Dispose() => Disposed = true;
    }
}
