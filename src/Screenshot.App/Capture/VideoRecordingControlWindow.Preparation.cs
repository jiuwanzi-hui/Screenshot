using System.Windows;
using System.Windows.Interop;

namespace Screenshot.App.Capture;

public partial class VideoRecordingControlWindow
{
    private bool _isPreparedForSelection = true;

    private VideoRecordingControlWindow() => InitializeComponent();

    // Only the real visual tree is prepared. No recorder, desktop capture,
    // device enumeration, camera, microphone or timer is started here.
    internal static VideoRecordingControlWindow PrepareForResidentSelection()
    {
        CaptureTimingDiagnostics.Mark("recording-toolbar-prepare-start");
        var window = new VideoRecordingControlWindow();
        CaptureTimingDiagnostics.Mark("recording-toolbar-xaml-ready");
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.ControlSurface.Measure(new System.Windows.Size(4096, 512));
            window.ControlSurface.Arrange(new Rect(window.ControlSurface.DesiredSize));
            window.ControlSurface.UpdateLayout();
            CaptureTimingDiagnostics.Mark("recording-toolbar-prepare-end");
            return window;
        }
        catch { window.Close(); throw; }
    }
}
