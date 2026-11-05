using System.Drawing;
using System.Reflection;
using Image = System.Windows.Controls.Image;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class CaptureStartupHandoffTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public async Task HoverCaptureKeepsWindowHiddenAndDispatcherResponsiveUntilFrameAndAllowsEscape()
    {
        using var pins = new PinnedImageManager();
        CaptureOverlayWindow? overlay = null;
        Task? captureTask = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                overlay = new CaptureOverlayWindow(Options(pins));
                overlay.Closed += (_, _) => closed.TrySetResult();
                captureTask = overlay.CaptureBeforeShowingAsync(token =>
                {
                    Interlocked.Increment(ref captures);
                    entered.TrySetResult();
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Unreachable");
                });
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                Assert.False(overlay!.IsVisible);
                Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(overlay).Handle);
                Assert.Null(((Image)overlay.FindName("FrozenScreenImage")).Source);
                Assert.True(overlay.CaptureBeforeShowingAsync(_ => throw new InvalidOperationException("Duplicate capture")).IsCompleted);
                Assert.True(CaptureOverlayWindow.TryHandleGlobalPickerKey(0x1B, HotKeyModifiers.None));
            });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await captureTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, captures);
            WpfTestHost.Invoke(() => Assert.False(overlay!.IsVisible));
        }
        finally { WpfTestHost.Invoke(() => { overlay?.Close(); overlay?.Dispose(); }); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedDesktopFrameIsPublishedEvenWhenPickerPublicationWasDeferred(bool deferredPicker)
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var overlay = new CaptureOverlayWindow(Options(pins),
                initialScreenSnapshot: new CapturedImage(new Bitmap(80, 60)),
                deferInitialColorPickerActivation: deferredPicker) { Opacity = 0 };
            try
            {
                Assert.Null(((Image)overlay.FindName("FrozenScreenImage")).Source);
                Assert.True((bool)typeof(CaptureOverlayWindow).GetMethod("PrepareInitialInputSurface", Private)!.Invoke(overlay, null)!);
                Assert.NotNull(((Image)overlay.FindName("FrozenScreenImage")).Source);
                Assert.Equal(1, overlay.Opacity);
                // No HWND exists: this cannot report successful input ownership.
                Assert.False((bool)typeof(CaptureOverlayWindow).GetMethod("EnsureInteractiveInputOwnership", Private)!.Invoke(overlay, null)!);
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void StagedFrameDoesNotStartPickerOrMaskBeforeNativePresentation()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var overlay = new CaptureOverlayWindow(Options(pins),
                initialScreenSnapshot: new CapturedImage(new Bitmap(80, 60)));
            try
            {
                typeof(CaptureOverlayWindow).GetMethod("PrepareInitialInputSurface", Private)!.Invoke(overlay, null);
                typeof(CaptureOverlayWindow).GetMethod("ActivateInitialColorPickerAfterShow", Private)!.Invoke(overlay, null);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_initialScreenInputReady", Private)!.GetValue(overlay)!);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_isColorPickerActive", Private)!.GetValue(overlay)!);
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(CaptureOverlayWindow).GetField("_windowSnapTimer", Private)!.GetValue(overlay)!;
                Assert.False(timer.IsEnabled);
                var frame = (NativeSelectionFrameWindow)typeof(CaptureOverlayWindow).GetField("_nativeSelectionFrame", Private)!.GetValue(overlay)!;
                Assert.False(frame.IsMaskEnabled);
                Assert.False(overlay.IsVisible);
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void LatePickerInitializationDoesNotReplaceAnExistingSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var overlay = new CaptureOverlayWindow(Options(pins),
                initialScreenSnapshot: new CapturedImage(new Bitmap(80, 60)));
            try
            {
                var selection = (System.Windows.Shapes.Rectangle)overlay.FindName("SelectionRectangle");
                System.Windows.Controls.Canvas.SetLeft(selection, 10);
                System.Windows.Controls.Canvas.SetTop(selection, 10);
                selection.Width = 40;
                selection.Height = 30;
                typeof(CaptureOverlayWindow).GetMethod("ActivateInitialColorPickerAfterShow", Private)!.Invoke(overlay, null);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_isColorPickerActive", Private)!.GetValue(overlay)!);
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void InputSurfaceCannotBecomeReadyWithoutItsFrozenFrame()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var overlay = new CaptureOverlayWindow(Options(pins)) { Opacity = 0 };
            try
            {
                var prepare = typeof(CaptureOverlayWindow).GetMethod("PrepareInitialInputSurface", Private)!;
                Assert.False((bool)prepare.Invoke(overlay, null)!);
                Assert.Null(((Image)overlay.FindName("FrozenScreenImage")).Source);
                var frame = new CapturedImage(new Bitmap(80, 60));
                typeof(CaptureOverlayWindow).GetField("_screenSnapshot", Private)!.SetValue(overlay, frame);
                Assert.True((bool)prepare.Invoke(overlay, null)!);
                Assert.Same(frame.Preview, ((Image)overlay.FindName("FrozenScreenImage")).Source);
                Assert.Equal(1, overlay.Opacity);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_initialInputPassthrough", Private)!.GetValue(overlay)!);
                Assert.False(overlay.IsVisible); // no desktop pattern or global input
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public async Task EscapeCancelsEvenBeforePickerInitializationAndWithoutFocus()
    {
        CaptureOverlayWindow? overlay = null;
        PinnedImageManager? pins = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                pins = new PinnedImageManager();
                overlay = new CaptureOverlayWindow(Options(pins, () => closed.TrySetResult()));
                typeof(CaptureOverlayWindow).GetField("_activeInteractiveOverlay", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, overlay);
                Assert.False(overlay.IsActive);
                Assert.False(CaptureOverlayWindow.TryHandleGlobalPickerKey(0x43, HotKeyModifiers.None));
                Assert.True(CaptureOverlayWindow.TryHandleGlobalPickerKey(0x1B, HotKeyModifiers.None));
                Assert.False(closed.Task.IsCompleted); // hook only enqueues; no synchronous UI work
            });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { WpfTestHost.Invoke(() => { overlay?.Close(); overlay?.Dispose(); pins?.Dispose(); }); }
    }

    [Fact]
    public void FrameThatCompletesAfterCancellationIsDisposedWithoutPublishing()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var overlay = new CaptureOverlayWindow(Options(pins));
            using var lateFrame = new CapturedImage(new Bitmap(80, 60));
            try
            {
                typeof(CaptureOverlayWindow).GetField("_isCompleted", Private)!.SetValue(overlay, true);
                typeof(CaptureOverlayWindow).GetMethod("SetInitialScreenSnapshotCore", Private)!.Invoke(overlay, [lateFrame]);
                Assert.Throws<ObjectDisposedException>(() => lateFrame.Preview);
                Assert.Null(((Image)overlay.FindName("FrozenScreenImage")).Source);
                Assert.False(overlay.IsVisible);
            }
            finally { overlay.Close(); }
        });
    }

    private static CaptureOverlayOptions Options(PinnedImageManager pins, Action? closed = null) => new()
    {
        SaveDirectory = System.IO.Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
        HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
        StartOcrAsync = _ => Task.CompletedTask, CaptureClosed = closed, DeferInitialScreenCapture = true,
    };
}
