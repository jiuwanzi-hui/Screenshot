using System.Reflection;
using System.Windows.Interop;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class PreparedRecordingToolbarTests
{
    private static readonly string[] SessionFields = ["_recorder", "_frameWindow", "_inputOverlay", "_annotationOverlay", "_cameraOverlay", "_inputMonitor", "_elapsedTimer"];
    [Fact]
    public void PreparingRecordingVisualsDoesNotConstructRecorderOrStartDevicesAndCanBeDiscarded()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = VideoRecordingControlWindow.PrepareForResidentSelection();
            try
            {
                Assert.False(window.IsVisible);
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                foreach (var field in SessionFields)
                    Assert.Null(typeof(VideoRecordingControlWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            }
            finally { window.Close(); }
        });
    }
}
