using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using System.IO;

namespace Screenshot.App.Tests;

public sealed class VideoResourceLifetimeTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task ClosingExportCancelsWorkAndRejectsQueuedProgressAndLateCompletion()
    {
        var entered = new TaskCompletionSource<(IProgress<double>, CancellationToken)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPostProcessWindow? window = null;
        Task? operation = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new VideoPostProcessWindow(@"C:\unused.mp4");
                Func<IProgress<double>, CancellationToken, Task<string>> export = (progress, token) =>
                {
                    entered.TrySetResult((progress, token));
                    // Deliberately ignore cancellation to simulate late native completion.
                    return finish.Task;
                };
                operation = (Task)typeof(VideoPostProcessWindow).GetMethod("RunAsync", Fields)!.Invoke(window, [export])!;
            });
            var (progress, token) = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => canceled.TrySetResult());
            WpfTestHost.Invoke(() => window!.Close());
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.IsCancellationRequested);
            progress.Report(0.9);
            finish.SetResult(@"C:\must-not-copy.mp4");
            await operation!.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var status = (TextBlock)typeof(VideoPostProcessWindow).GetField("_statusText", Fields)!.GetValue(window)!;
                var bar = (ProgressBar)typeof(VideoPostProcessWindow).GetField("_progress", Fields)!.GetValue(window)!;
                Assert.Equal("正在处理，请稍候...", status.Text);
                Assert.Equal(0, bar.Value);
            });
        }
        finally
        {
            finish.TrySetCanceled();
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public void ClosingVideoEditorDetachesFrameAndTimelinePixels()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new VideoPostProcessWindow(@"C:\unused.mp4");
            var preview = (Image)typeof(VideoPostProcessWindow).GetField("_previewFrame", Fields)!.GetValue(window)!;
            var timeline = (Canvas)typeof(VideoPostProcessWindow).GetField("_timelineFramesCanvas", Fields)!.GetValue(window)!;
            preview.Source = new WriteableBitmap(320, 240, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            timeline.Children.Add(new Image { Source = preview.Source });
            window.Close();
            Assert.Null(preview.Source);
            Assert.Empty(timeline.Children);
        });
    }

    [Fact]
    public async Task RetiringPreviewCancelsQueuedFramesThenReleasesItsComposition()
    {
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 12, 34, 56), TimeSpan.FromSeconds(1)));
        var session = (VideoPreviewSession)Activator.CreateInstance(typeof(VideoPreviewSession), Fields, null,
            [composition, TimeSpan.FromSeconds(1), 64, 64], null)!;
        var gate = (SemaphoreSlim)typeof(VideoPreviewSession).GetField("_thumbnailGate", Fields)!.GetValue(session)!;
        await gate.WaitAsync();
        try
        {
            var frame = session.GetFrameAsync(TimeSpan.Zero);
            var close = session.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frame.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(close.IsCompleted);
        }
        finally { gate.Release(); }
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(composition.Clips);
        Assert.Null(typeof(VideoPreviewSession).GetField("_composition", Fields)!.GetValue(session));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetFrameAsync(TimeSpan.Zero));
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeVideoPreviewAndExportReleaseFilesAndRecoverAfterCancellation(bool webp)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-video-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "source.mp4");
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync("source.mp4");
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 12, 34, 56), TimeSpan.FromSeconds(1)));
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
            profile.Video.Width = 160;
            profile.Video.Height = 96;
            Assert.Equal(TranscodeFailureReason.None,
                await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            composition.Clips.Clear();
            var original = await File.ReadAllBytesAsync(input);
            for (int i = 0; i < 2; i++)
            {
                await using var session = await VideoPreviewSession.CreateAsync(input);
                Assert.Equal(160, session.PixelWidth);
                Assert.Equal(96, session.PixelHeight);
                var bytes = await session.GetFrameAsync(TimeSpan.Zero, 160);
                using var frame = SixLabors.ImageSharp.Image.Load(bytes);
                Assert.Equal(160, frame.Width);
            }
            var format = webp ? AnimatedImageFormat.WebP : AnimatedImageFormat.Gif;
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoPostProcessingService.ExportAnimatedImageAsync(
                input, TimeSpan.Zero, TimeSpan.FromSeconds(0.5), format,
                new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
            Assert.Single(Directory.GetFiles(directory));
            var output = await VideoPostProcessingService.ExportAnimatedImageAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(0.5), format);
            using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(output))
            {
                Assert.True(image.Frames.Count >= 2);
                var pixel = image[5, 5];
                Assert.InRange((int)pixel.R, 2, 22);
                Assert.InRange((int)pixel.G, 24, 44);
                Assert.InRange((int)pixel.B, 46, 66);
            }
            var trimmed = await VideoPostProcessingService.TrimMp4Async(input, TimeSpan.Zero, TimeSpan.FromSeconds(0.5));
            Assert.True(new FileInfo(trimmed).Length > 0);
            Assert.Equal(original, await File.ReadAllBytesAsync(input));
            File.Delete(output);
            File.Delete(trimmed);
            // Successful deletion also checks that no preview stream/file handle remains open.
            File.Delete(input);
        }
        finally
        {
            foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
