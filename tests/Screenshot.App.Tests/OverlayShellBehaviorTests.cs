using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class OverlayShellBehaviorTests
{
    [Fact]
    public void InvalidHandleCannotChangeShellState() =>
        Assert.Throws<ArgumentException>(() => OverlayShellBehavior.Apply(IntPtr.Zero));

    // Hidden HWNDs only: no screen capture, fullscreen test pattern, global
    // input or taskbar modifications. Opt-in like the other native HWND tests.
    [DesktopFact]
    public void NativeLayersAreMarkedBeforeDisplayAndAfterHandleRecreation()
    {
        WpfTestHost.Invoke(() =>
        {
            using var mask = new NativeSelectionMaskWindow();
            using var frame = new NativeSelectionFrameWindow();
            using var size = new NativeSelectionSizeWindow();
            foreach (var window in new System.Windows.Forms.Form[] { mask, frame, size })
            {
                AssertMarkedAndHidden(window.Handle);
                typeof(System.Windows.Forms.Control).GetMethod("RecreateHandle",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                AssertMarkedAndHidden(window.Handle);
            }
        });
    }

    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureIsMarkedBeforeFirstShowIncludingDeferredStartup(bool deferred)
    {
        WpfTestHost.Invoke(() =>
        {
            using var frame = new CapturedImage(new Bitmap(80, 60));
            using var overlay = new CaptureOverlayWindow(options: null, initialScreenSnapshot: frame,
                deferInitialScreenCapture: deferred);
            var unrelated = new Window { Width = 120, Height = 90 };
            try
            {
                var unrelatedHandle = new WindowInteropHelper(unrelated).EnsureHandle();
                var handle = new WindowInteropHelper(overlay).EnsureHandle();
                AssertMarkedAndHidden(handle);
                Assert.Equal(IntPtr.Zero, GetProp(unrelatedHandle, "NonRudeHWND"));
                OverlayShellBehavior.Apply(handle); // Idempotent; no shell-wide restoration.
                AssertMarkedAndHidden(handle);
            }
            finally { overlay.Close(); unrelated.Close(); }
        });
    }

    [DesktopFact]
    public void RecordingLayersAreMarkedBeforeFirstShow()
    {
        WpfTestHost.Invoke(() =>
        {
            var region = new ScreenRegion(0, 0, 160, 90);
            var annotation = new RecordingAnnotationOverlayWindow(region);
            var input = new RecordingInputOverlayWindow(region);
            try
            {
                AssertMarkedAndHidden(new WindowInteropHelper(annotation).EnsureHandle());
                AssertMarkedAndHidden(new WindowInteropHelper(input).EnsureHandle());
            }
            finally { annotation.Close(); input.Close(); }
        });
    }

    private static void AssertMarkedAndHidden(IntPtr handle)
    {
        Assert.Equal(new IntPtr(1), GetProp(handle, "NonRudeHWND"));
        Assert.False(IsWindowVisible(handle));
    }

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
