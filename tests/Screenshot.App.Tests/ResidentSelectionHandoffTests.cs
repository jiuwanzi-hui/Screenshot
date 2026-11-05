using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ResidentSelectionHandoffTests
{
    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledResidentHandoffClosesPinOrReadyRecorderAndReleasesInteraction(bool video)
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapcut-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PinnedImageManager? pins = null;
        RegionCaptureCoordinator? coordinator = null;
        Task? operation = null;
        using var http = new HttpClient();
        var states = new List<bool>();
        var errors = new List<string>();
        var handoffReached = false;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                pins = new PinnedImageManager();
                coordinator = new RegionCaptureCoordinator(() => AppSettings.CreateDefault() with
                {
                    VideoSaveDirectory = directory, RecordMicrophone = false, RecordSystemAudio = false,
                    ShowCameraInRecording = false,
                }, new CaptureHistoryService(), pins, new UnusedCredentials(), http, errors.Add);
                coordinator.CaptureStateChanged += states.Add;
                var desktop = VirtualScreen.GetBounds();
                var region = new ScreenRegion(desktop.X + 30, desktop.Y + 30, 240, 120);
                Task Reject()
                {
                    handoffReached = true;
                    Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), window => window is CaptureOverlayWindow);
                    if (video)
                    {
                        var controls = Assert.Single(Application.Current.Windows.OfType<VideoRecordingControlWindow>());
                        Assert.False(controls.IsEnabled);
                        var recorder = (RegionVideoRecorder)typeof(VideoRecordingControlWindow).GetField("_recorder",
                            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controls)!;
                        Assert.Equal(RegionVideoRecorderState.Ready, recorder.State);
                    }
                    else Assert.Equal(1, pins.Count);
                    return Task.FromException(new OperationCanceledException("Selection was cancelled in resident."));
                }
                operation = video
                    ? coordinator.RequestVideoRecordingAsync(initialSelection: region, selectionPresented: Reject)
                    : coordinator.RequestPinCaptureAsync(ScreenCaptureService.Capture(region), initialSelection: region, selectionPresented: Reject);
            });
            if (video) await operation!.WaitAsync(TimeSpan.FromSeconds(10));
            else await Assert.ThrowsAsync<OperationCanceledException>(() => operation!.WaitAsync(TimeSpan.FromSeconds(10)));
            WpfTestHost.Invoke(() =>
            {
                Assert.True(handoffReached);
                Assert.Equal([true, false], states);
                Assert.Empty(errors);
                Assert.Equal(0, pins!.Count);
                Assert.False(coordinator!.IsRecordingInProgress);
                Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), window =>
                    window is VideoRecordingControlWindow or PinnedImageWindow);
            });
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            WpfTestHost.Invoke(() => pins?.Dispose());
            Directory.Delete(directory);
        }
    }

    private sealed class UnusedCredentials : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => throw new InvalidOperationException("No translation expected.");
        public void SetApiKey(string providerId, string? apiKey) => throw new InvalidOperationException("No configuration changes expected.");
    }
}
