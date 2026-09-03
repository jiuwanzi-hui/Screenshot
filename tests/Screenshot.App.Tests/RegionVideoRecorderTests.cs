using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using WinForms = System.Windows.Forms;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Screenshot.App.Tests;

public sealed class RegionVideoRecorderTests
{
    [Fact]
    public void RecordingColorPaletteUsesTheSharedPersistedSlots()
    {
        Assert.Equal(
            [0x123456, 0xABCDEF],
            VideoRecordingControlWindow.NormalizeCustomColorPalette(
                [0x123456, -1, 0xABCDEF, 0x1000000]));
        Assert.Equal(
            Enumerable.Range(0, 16),
            VideoRecordingControlWindow.NormalizeCustomColorPalette(
                Enumerable.Range(0, 20)));
    }

    [Fact]
    public void AutomaticRecordingControlsStayCenteredWhenWidthChanges()
    {
        var region = new ScreenRegion(100, 100, 800, 600);
        var workArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var expanded = VideoRecordingControlWindow
            .CalculateAutomaticControlBounds(region, workArea, 760, 54);
        var compact = VideoRecordingControlWindow
            .CalculateAutomaticControlBounds(region, workArea, 520, 54);

        Assert.Equal(100 + ((800 - 760) / 2), expanded.X);
        Assert.Equal(100 + ((800 - 520) / 2), compact.X);
        Assert.Equal(region.Y + region.Height + 10, compact.Y);
    }

    [Theory]
    [InlineData(50, 0.5)]
    [InlineData(84, 0.84)]
    [InlineData(100, 1)]
    [InlineData(150, 1.5)]
    [InlineData(10, 0.5)]
    [InlineData(200, 1.5)]
    public void RecordingToolbarUsesTheSharedScaleSetting(
        double percent,
        double expectedScale)
    {
        Assert.Equal(
            expectedScale,
            VideoRecordingControlWindow.NormalizeToolbarScale(percent),
            precision: 2);
    }

    [Theory]
    [InlineData(50, 30)]
    [InlineData(100, 46)]
    [InlineData(150, 62)]
    public void ActiveRecordingToolbarUsesComfortableVerticalPadding(
        double percent,
        double expectedHeight)
    {
        Assert.Equal(
            expectedHeight,
            VideoRecordingControlWindow.CalculateExpandedToolbarHeight(
                percent,
                annotationMode: true));
    }

    [Theory]
    [InlineData(50, 41)]
    [InlineData(100, 68)]
    [InlineData(150, 95)]
    public void RecordingOptionsToolbarAlsoUsesComfortableVerticalPadding(
        double percent,
        double expectedHeight)
    {
        Assert.Equal(
            expectedHeight,
            VideoRecordingControlWindow.CalculateExpandedToolbarHeight(
                percent,
                annotationMode: false));
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
    public void DisplayOnlyRecordingOptionsDoNotRecreateNativeRecorder()
    {
        var current = new VideoRecordingPreferences(
            VideoRecordingCodec.H264,
            30,
            RecordSystemAudio: true,
            RecordMicrophone: false);
        var displayChanged = current with
        {
            ShowKeyboardInput = true,
            ShowMouseInput = true,
            ShowMouseTrail = true,
            OutputFormat = VideoRecordingOutputFormat.Gif,
        };

        Assert.False(VideoRecordingControlWindow.RequiresRecorderReplacement(
            current,
            displayChanged));
        Assert.True(VideoRecordingControlWindow.RequiresRecorderReplacement(
            current,
            current with { FrameRate = 60 }));
        Assert.True(VideoRecordingControlWindow.RequiresRecorderReplacement(
            current,
            current with { RecordMicrophone = true }));
    }

    [Fact]
    public void RecordingArrowGeometryIncludesShaftAndHead()
    {
        var geometry = RecordingAnnotationOverlayWindow.CreateArrowGeometry(
            new System.Windows.Point(10, 20),
            new System.Windows.Point(110, 20));

        Assert.True(geometry.IsFrozen);
        Assert.Equal(10, geometry.Bounds.Left, precision: 3);
        Assert.Equal(110, geometry.Bounds.Right, precision: 3);
        Assert.True(geometry.Bounds.Height > 10);
    }

    [Fact]
    public void ClickingSelectedRecordingToolReturnsToPointerPassThrough()
    {
        Assert.Equal(
            RecordingAnnotationTool.Rectangle,
            VideoRecordingControlWindow.ResolveAnnotationToolSelection(
                RecordingAnnotationTool.Rectangle,
                isChecked: true));
        Assert.Equal(
            RecordingAnnotationTool.Pointer,
            VideoRecordingControlWindow.ResolveAnnotationToolSelection(
                RecordingAnnotationTool.Rectangle,
                isChecked: false));
    }

    [Fact]
    public void ActiveRecordingToolCreatesVisibleHitTestSurface()
    {
        RecordingAnnotationOverlayWindow? overlay = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                overlay = new RecordingAnnotationOverlayWindow(
                    new ScreenRegion(40, 40, 320, 240));
                overlay.EnsureWindowHandle();
                overlay.Show();
                Assert.False(overlay.IsHitTestVisible);
                Assert.False(overlay.DrawingCanvas.IsHitTestVisible);
                overlay.SelectTool(RecordingAnnotationTool.Rectangle);

                var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                    overlay.DrawingCanvas.Background);
                Assert.Equal(1, brush.Color.A);
                Assert.True(overlay.IsHitTestVisible);
                Assert.True(overlay.DrawingCanvas.IsHitTestVisible);

                overlay.SelectTool(RecordingAnnotationTool.Pointer);
                Assert.Same(
                    System.Windows.Media.Brushes.Transparent,
                    overlay.DrawingCanvas.Background);
                Assert.False(overlay.IsHitTestVisible);
                Assert.False(overlay.DrawingCanvas.IsHitTestVisible);
            });
        }
        finally
        {
            if (overlay is not null)
            {
                WpfTestHost.Invoke(overlay.Close);
            }
        }
    }

    [Fact]
    public void PassiveRecordingOverlaysAreNativeHitTestTransparent()
    {
        const int nonClientHitTestMessage = 0x0084;
        const int hitTestTransparent = -1;
        RecordingInputOverlayWindow? inputOverlay = null;
        RecordingAnnotationOverlayWindow? annotationOverlay = null;
        RecordingRegionFrameWindow? frameOverlay = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var region = new ScreenRegion(40, 40, 320, 240);
                inputOverlay = new RecordingInputOverlayWindow(region);
                annotationOverlay = new RecordingAnnotationOverlayWindow(region);
                frameOverlay = new RecordingRegionFrameWindow(region);
                inputOverlay.Show();
                annotationOverlay.Show();
                frameOverlay.Show();

                Assert.Equal(
                    new IntPtr(hitTestTransparent),
                    NativeMethods.SendMessage(
                        new WindowInteropHelper(inputOverlay).Handle,
                        nonClientHitTestMessage,
                        IntPtr.Zero,
                        IntPtr.Zero));
                Assert.Equal(
                    new IntPtr(hitTestTransparent),
                    NativeMethods.SendMessage(
                        new WindowInteropHelper(annotationOverlay).Handle,
                        nonClientHitTestMessage,
                        IntPtr.Zero,
                        IntPtr.Zero));
                Assert.Equal(
                    new IntPtr(hitTestTransparent),
                    NativeMethods.SendMessage(
                        new WindowInteropHelper(frameOverlay).Handle,
                        nonClientHitTestMessage,
                        IntPtr.Zero,
                        IntPtr.Zero));
            });
        }
        finally
        {
            WpfTestHost.Invoke(() =>
            {
                inputOverlay?.Close();
                annotationOverlay?.Close();
                frameOverlay?.Close();
            });
        }
    }

    [Fact]
    public void MouseTrailFadesContinuouslyFromTailToHead()
    {
        var opacities = Enumerable.Range(0, 8)
            .Select(index => RecordingInputOverlayWindow.CalculateTrailOpacity(index, 8))
            .ToArray();

        Assert.InRange(opacities[0], 0.05, 0.07);
        Assert.Equal(1, opacities[^1], precision: 3);
        Assert.All(
            opacities.Zip(opacities.Skip(1)),
            pair => Assert.True(pair.First < pair.Second));
        Assert.Equal(0, RecordingInputOverlayWindow.CalculateTrailOpacity(-1, 8));
        Assert.Equal(0, RecordingInputOverlayWindow.CalculateTrailOpacity(0, 0));
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
    public async Task CancelDiscardsRecordedMp4()
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
            "SnapCut-Recording-Cancel-Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            using var recorder = new RegionVideoRecorder(region, directory);
            recorder.Start();
            await Task.Delay(600);

            await recorder.CancelAsync();

            Assert.Empty(Directory.EnumerateFiles(directory, "*.mp4"));
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
    public async Task VisibleAnnotationWindowIsCapturedBySingleDisplaySource()
    {
        var screen = WinForms.Screen.PrimaryScreen ??
            Assert.Single(WinForms.Screen.AllScreens);
        var bounds = screen.Bounds;
        var region = new ScreenRegion(
            bounds.Left + 40,
            bounds.Top + 40,
            Math.Min(320, (bounds.Width - 40) & ~1),
            Math.Min(240, (bounds.Height - 40) & ~1));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SnapCut-Recording-Overlay-Tests",
            Guid.NewGuid().ToString("N"));
        RecordingAnnotationOverlayWindow? overlay = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                overlay = new RecordingAnnotationOverlayWindow(region);
                var marker = new WpfRectangle
                {
                    Width = 150,
                    Height = 100,
                    Fill = System.Windows.Media.Brushes.Red,
                };
                System.Windows.Controls.Canvas.SetLeft(marker, 70);
                System.Windows.Controls.Canvas.SetTop(marker, 60);
                overlay.DrawingCanvas.Children.Add(marker);
            });

            using var recorder = new RegionVideoRecorder(
                region,
                directory);
            WpfTestHost.Invoke(() =>
            {
                overlay!.Show();
                overlay.UpdateLayout();
            });
            recorder.Start();
            await Task.Delay(900);

            using var snapshot = new MemoryStream();
            Assert.True(recorder.TryTakeSnapshot(snapshot));
            snapshot.Position = 0;
            using var bitmap = new Bitmap(snapshot);
            Assert.Equal(region.Width, bitmap.Width);
            Assert.Equal(region.Height, bitmap.Height);
            var redSamples = 0;
            for (var y = 70; y < 150; y += 8)
            {
                for (var x = 80; x < 200; x += 8)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.R > 180 && color.G < 100 && color.B < 100)
                    {
                        redSamples++;
                    }
                }
            }

            Assert.True(redSamples >= 20, $"录制帧只检测到 {redSamples} 个标注像素。");
            var backgroundSamples = 0;
            for (var y = 8; y < 48; y += 8)
            {
                for (var x = 8; x < 48; x += 8)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.R > 8 || color.G > 8 || color.B > 8)
                    {
                        backgroundSamples++;
                    }
                }
            }

            Assert.True(
                backgroundSamples >= 10,
                $"透明标注层遮住了原画面，只检测到 {backgroundSamples} 个背景像素。");

            WpfTestHost.Invoke(() =>
            {
                var marker = Assert.IsType<WpfRectangle>(
                    overlay!.DrawingCanvas.Children[0]);
                marker.Fill = System.Windows.Media.Brushes.Blue;
                overlay.UpdateLayout();
            });
            await Task.Delay(350);

            using var updatedSnapshot = new MemoryStream();
            Assert.True(recorder.TryTakeSnapshot(updatedSnapshot));
            updatedSnapshot.Position = 0;
            using var updatedBitmap = new Bitmap(updatedSnapshot);
            var blueSamples = 0;
            for (var y = 70; y < 150; y += 8)
            {
                for (var x = 80; x < 200; x += 8)
                {
                    var color = updatedBitmap.GetPixel(x, y);
                    if (color.B > 180 && color.R < 100 && color.G < 140)
                    {
                        blueSamples++;
                    }
                }
            }

            Assert.True(
                blueSamples >= 20,
                $"录制源没有刷新后续桌面帧，只检测到 {blueSamples} 个更新像素。");
            var result = await recorder.StopAsync();
            Assert.True(result.IsSuccess, result.ErrorMessage);
        }
        finally
        {
            if (overlay is not null)
            {
                WpfTestHost.Invoke(overlay.Close);
            }
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

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);
}
