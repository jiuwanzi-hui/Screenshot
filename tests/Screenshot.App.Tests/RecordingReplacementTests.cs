using System.IO;
using System.Reflection;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class RecordingReplacementTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task FailureBeforeNativeStartupIsWrittenBeforeTheWindowCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-recording-replacement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blockedSave = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blockedSave, "keep");
        VideoRecordingControlWindow? window = null;
        RegionVideoRecorder? recorder = null;
        Task<RegionVideoRecordingResult>? completed = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var region = new ScreenRegion(30, 30, 160, 120);
                recorder = new RegionVideoRecorder(region, directory);
                typeof(RegionVideoRecorder).GetField("_errorLog", Private)!.SetValue(recorder, new RecordingErrorLog(directory, directory));
                window = (VideoRecordingControlWindow)Activator.CreateInstance(typeof(VideoRecordingControlWindow), true)!;
                typeof(VideoRecordingControlWindow).GetMethod("InitializeSession", Private)!.Invoke(window,
                    [recorder, blockedSave, false, false, VideoRecordingCodec.H264, 30, false, false, false, false,
                     null, null, VideoRecordingOutputFormat.Mp4, new RecordingInputOverlayWindow(region), new RecordingAnnotationOverlayWindow(region), null,
                     new VideoRecordingAnnotationPreferences(ShapeToolMode.Rectangle, ArrowToolMode.Straight, ArrowStyle.Filled, "#F04455", 3),
                     null, null, null, "Alt+2", 100d, null, null]);
                completed = ((TaskCompletionSource<RegionVideoRecordingResult>)typeof(VideoRecordingControlWindow).GetField("_completion", Private)!.GetValue(window)!).Task;
                ((System.Windows.Controls.ComboBox)window.FindName("CodecComboBox")).SelectedValue = "H265";
                ((System.Windows.Controls.Button)window.FindName("StartButton")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            });
            var result = await completed!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsSuccess);
            var logPath = Path.Combine(directory, RecordingErrorLog.FileName);
            Assert.Contains(logPath, result.ErrorMessage);
            var error = await File.ReadAllTextAsync(logPath);
            Assert.Contains("start-controls", error);
            Assert.Contains("IOException", error);
            Assert.Contains("H265", error);
            Assert.Equal("keep", await File.ReadAllTextAsync(blockedSave));
            WpfTestHost.Invoke(() => Assert.False(window!.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
            recorder?.Dispose();
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(VideoRecordingCodec.H265, 60, false, false)]
    [InlineData(VideoRecordingCodec.H264, 24, true, true)]
    public async Task ChangingOptionsKeepsSaveDirectoryAndReplacesRecorderBeforeStarting(VideoRecordingCodec codec, int fps, bool systemAudio, bool microphone)
    {
        VideoRecordingControlWindow? window = null;
        RegionVideoRecorder? initial = null;
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-recording-replacement", Guid.NewGuid().ToString("N"));
        var region = new ScreenRegion(30, 30, 160, 120);
        try
        {
            Task? replace = null;
            WpfTestHost.Invoke(() =>
            {
                initial = new RegionVideoRecorder(region, directory);
                window = (VideoRecordingControlWindow)Activator.CreateInstance(typeof(VideoRecordingControlWindow), true)!;
                var input = new RecordingInputOverlayWindow(region);
                var annotation = new RecordingAnnotationOverlayWindow(region);
                typeof(VideoRecordingControlWindow).GetMethod("InitializeSession", Private)!.Invoke(window,
                    [initial, directory, false, false, VideoRecordingCodec.H264, 30, false, false, false, false,
                     null, null, VideoRecordingOutputFormat.Mp4, input, annotation, null,
                     new VideoRecordingAnnotationPreferences(ShapeToolMode.Rectangle, ArrowToolMode.Straight, ArrowStyle.Filled, "#F04455", 3),
                     null, null, null, "Alt+2", 100d, null, null]);
                replace = (Task)typeof(VideoRecordingControlWindow).GetMethod("ReplaceRecorderAsync", Private)!.Invoke(window,
                    [new VideoRecordingPreferences(codec, fps, systemAudio, microphone)])!;
            });
            await replace!.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var recorder = (RegionVideoRecorder)typeof(VideoRecordingControlWindow).GetField("_recorder", Private)!.GetValue(window)!;
                Assert.NotSame(initial, recorder);
                Assert.Equal(RegionVideoRecorderState.Ready, recorder.State);
                Assert.Equal(directory, typeof(VideoRecordingControlWindow).GetField("_saveDirectory", Private)!.GetValue(window));
                Assert.False(window!.IsVisible);
            });
            await Assert.ThrowsAsync<ObjectDisposedException>(() => initial!.StartAsync());
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
            initial?.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
