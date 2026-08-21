using System.IO;
using System.Drawing;
using Screenshot.App.Capture;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Tests;

public sealed class RegionVideoRecorderTests
{
    [Fact]
    public void AutomaticRecordingControlsStayCenteredWhenWidthChanges()
    {
        var region = new ScreenRegion(100, 100, 800, 600);
        var workArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var expanded = VideoRecordingControlWindow
            .CalculateAutomaticControlBounds(region, workArea, 668, 54);
        var compact = VideoRecordingControlWindow
            .CalculateAutomaticControlBounds(region, workArea, 216, 54);

        Assert.Equal(100 + ((800 - 668) / 2), expanded.X);
        Assert.Equal(100 + ((800 - 216) / 2), compact.X);
        Assert.Equal(region.Y + region.Height + 10, compact.Y);
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void CreatesAudioOptionsFromIndependentSourceSettings(
        bool systemAudio,
        bool microphone,
        bool audioEnabled,
        bool outputEnabled,
        bool inputEnabled)
    {
        var options = RegionVideoRecorder.ResolveAudioConfiguration(
            systemAudio,
            microphone);

        Assert.Equal(audioEnabled, options.IsAudioEnabled);
        Assert.Equal(outputEnabled, options.IsOutputDeviceEnabled);
        Assert.Equal(inputEnabled, options.IsInputDeviceEnabled);
    }

    [Theory]
    [InlineData(24, 24)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(0, 30)]
    [InlineData(144, 30)]
    public void NormalizesSupportedRecordingFrameRates(int requested, int expected)
    {
        Assert.Equal(expected, RegionVideoRecorder.NormalizeFrameRate(requested));
    }

    [Fact]
    public void SuccessfulCompletionFeedbackIsExplicitAndVisibleForOnePointFiveSeconds()
    {
        Assert.Equal(
            "录制完成，已保存",
            VideoRecordingControlWindow.SuccessfulCompletionMessage);
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            VideoRecordingControlWindow.SuccessfulCompletionHoldDuration);
    }

    [Fact]
    public void RecordingInputUsesPlusSignsBetweenKeyboardAndMouseTokens()
    {
        var display = RecordingInputMonitor.JoinInputTokens(
            ["Ctrl", "Shift"],
            ["鼠标左键"]);

        Assert.Equal("Ctrl + Shift + 鼠标左键", display);
    }

    [Theory]
    [InlineData(0x41, "A")]
    [InlineData(0x11, "Ctrl")]
    [InlineData(0xA1, "Shift")]
    [InlineData(0x0D, "Enter")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    public void RecordingInputFormatsCommonKeyboardKeys(
        uint virtualKey,
        string expected)
    {
        Assert.Equal(
            expected,
            RecordingInputMonitor.GetKeyboardToken(virtualKey));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 3)]
    public void ResolvesRecordingInputDisplayMode(
        bool showKeyboard,
        bool showMouse,
        int expectedValue)
    {
        var expected = (RecordingInputDisplayMode)expectedValue;
        var mode = VideoRecordingControlWindow.ResolveInputDisplayMode(
            showKeyboard,
            showMouse);

        Assert.Equal(expected, mode);
        Assert.Equal(
            showKeyboard,
            VideoRecordingControlWindow.ShowsKeyboardInput(mode));
        Assert.Equal(
            showMouse,
            VideoRecordingControlWindow.ShowsMouseInput(mode));
    }

    [Fact]
    public async Task RecordsPausesResumesAndFinalizesMp4()
    {
        var screen = WinForms.Screen.PrimaryScreen ??
            Assert.Single(WinForms.Screen.AllScreens);
        var bounds = screen.Bounds;
        var region = new ScreenRegion(
            bounds.Left + 20,
            bounds.Top + 20,
            Math.Min(320, (bounds.Width - 20) & ~1),
            Math.Min(240, (bounds.Height - 20) & ~1));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SnapCut-Recording-Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            using var recorder = new RegionVideoRecorder(region, directory);
            recorder.Start();
            await Task.Delay(800);
            recorder.Pause();
            await Task.Delay(180);
            recorder.Resume();
            await Task.Delay(500);

            var result = await recorder.StopAsync();

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.FilePath);
            var video = new FileInfo(result.FilePath);
            Assert.True(video.Exists);
            Assert.True(video.Length > 1024, $"MP4 文件过小：{video.Length} 字节");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolvesSingleDisplayRegionAndNormalizesDimensionsForH264()
    {
        var screen = WinForms.Screen.PrimaryScreen ??
            Assert.Single(WinForms.Screen.AllScreens);
        var bounds = screen.Bounds;
        var requested = new ScreenRegion(
            bounds.Left + 10,
            bounds.Top + 12,
            Math.Min(301, bounds.Width - 10),
            Math.Min(203, bounds.Height - 12));

        var resolved = RegionVideoRecorder.TryResolveRecordingTarget(
            requested,
            out var normalized,
            out var deviceName,
            out var sourceRegion);

        Assert.True(resolved);
        Assert.Equal(screen.DeviceName, deviceName);
        Assert.Equal(0, normalized.Width % 2);
        Assert.Equal(0, normalized.Height % 2);
        Assert.Equal(normalized.Width, sourceRegion.Width);
        Assert.Equal(normalized.Height, sourceRegion.Height);
        Assert.Equal(normalized.X - bounds.Left, sourceRegion.X);
        Assert.Equal(normalized.Y - bounds.Top, sourceRegion.Y);
    }

    [Fact]
    public void RejectsRegionThatIsNotContainedByOneDisplay()
    {
        var screen = WinForms.Screen.PrimaryScreen ??
            Assert.Single(WinForms.Screen.AllScreens);
        var bounds = screen.Bounds;
        var outsideAllDisplays = new ScreenRegion(
            bounds.Left,
            WinForms.SystemInformation.VirtualScreen.Bottom + 20,
            100,
            100);

        Assert.False(RegionVideoRecorder.TryResolveRecordingTarget(
            outsideAllDisplays,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void DetectsUniformProtectedContentBlackFrame()
    {
        using var bitmap = new Bitmap(120, 80);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        Assert.True(RegionVideoRecorder.IsNearlyBlackFrame(bitmap));
    }

    [Fact]
    public void DoesNotRejectANormalDarkSceneWithVisibleDetail()
    {
        using var bitmap = new Bitmap(120, 80);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(4, 4, 4));
        graphics.FillRectangle(Brushes.White, 20, 20, 50, 12);

        Assert.False(RegionVideoRecorder.IsNearlyBlackFrame(bitmap));
    }
}
