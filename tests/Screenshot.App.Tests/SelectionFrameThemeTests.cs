using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using DrawingColor = System.Drawing.Color;

namespace Screenshot.App.Tests;

public sealed class SelectionFrameThemeTests
{
    private static readonly string[] NativeColorFields = ["_startColor", "_endColor"];
    [Fact]
    public void ExistingCapturePinAndRecordingFramesFollowEveryThemeWithoutBeingRecreated()
    {
        WpfTestHost.Invoke(() =>
        {
            var application = Application.Current;
            var originalResources = application.Resources;
            application.Resources = new ResourceDictionary();
            using var image = new CapturedImage(new System.Drawing.Bitmap(96, 72));
            using var capture = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var recording = new RecordingRegionFrameWindow(new ScreenRegion(100, 100, 320, 240));
            var pin = new PinnedImageWindow(image.Clone());
            try
            {
                var themes = new Screenshot.App.Presentation.SettingsViewModel(AppSettings.CreateDefault())
                    .ThemeOptions.Select(option => Enum.Parse<AppTheme>(option.Value));
                foreach (var theme in themes.Append(AppTheme.AuroraMist))
                {
                    AppThemeManager.ApplySettingsPalette(application.Resources, theme);
                    var expected = Assert.IsType<SolidColorBrush>(application.Resources["SelectionBorderBrush"]).Color;
                    Assert.Equal(255, expected.A);
                    foreach (var (owner, field) in new[] { ((Window)capture, "_nativeSelectionFrame"), (recording, "_nativeFrame") })
                    {
                        var frame = (NativeSelectionFrameWindow)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
                        foreach (var colorField in NativeColorFields)
                        {
                            var actual = (DrawingColor)typeof(NativeSelectionFrameWindow).GetField(colorField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(frame)!;
                            Assert.Equal(DrawingColor.FromArgb(expected.R, expected.G, expected.B), actual);
                        }
                    }
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(((PinnedImageOutline)pin.FindName("ImageOutline")).EdgeBrush).Color);
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(((System.Windows.Shapes.Rectangle)pin.FindName("CropSelection")).Stroke).Color);
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(((System.Windows.Shapes.Rectangle)capture.FindName("WindowSnapRectangle")).Stroke).Color);
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(((Border)recording.FindName("FrameBorder")).BorderBrush).Color);
                    Assert.All(new Window[] { capture, recording, pin }, window => Assert.False(window.IsVisible));
                    if (theme == AppTheme.GlassMist)
                        Assert.InRange(Math.Max(expected.R, Math.Max(expected.G, expected.B)) - Math.Min(expected.R, Math.Min(expected.G, expected.B)), 0, 30);
                }
            }
            finally
            {
                capture.Close(); recording.Close(); pin.Close();
                application.Resources = originalResources;
            }
        });
    }
}
