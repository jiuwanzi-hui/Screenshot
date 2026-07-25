using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureIntegrationTests
{
    [Fact]
    public async Task PhysicalWheelCreatesAndPreservesInputHoleOverUnderlyingWindow()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureSelection? selection = null;
        ScrollCaptureTarget? target = null;
        var selectedRegion = default(ScreenRegion);
        var expectedWindowHandle = IntPtr.Zero;

        try
        {
            Task<ScrollCaptureSelection?>? selectionTask = null;
            WpfTestHost.Invoke(() =>
            {
                var content = new StackPanel();
                for (var index = 0; index < 30; index++)
                {
                    content.Children.Add(new TextBlock
                    {
                        Height = 48,
                        Text = $"Scrollable row {index}",
                    });
                }

                scrollViewer = new ScrollViewer
                {
                    Content = content,
                    CanContentScroll = false,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
                {
                    scrollViewer.ScrollToVerticalOffset(Math.Clamp(
                        scrollViewer.VerticalOffset - eventArgs.Delta,
                        0,
                        scrollViewer.ScrollableHeight));
                    eventArgs.Handled = true;
                };
                window = new Window
                {
                    Width = 420,
                    Height = 320,
                    Left = 120,
                    Top = 120,
                    Content = scrollViewer,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowStyle = WindowStyle.None,
                };
                window.Show();
                window.Activate();
                window.UpdateLayout();
                expectedWindowHandle = new WindowInteropHelper(window).Handle;
                Assert.True(ForegroundWindowCaptureService.TryGetClientScreenRegion(
                    expectedWindowHandle,
                    out var clientRegion));
                selectedRegion = new ScreenRegion(
                    clientRegion.X + 20,
                    clientRegion.Y + 20,
                    clientRegion.Width - 40,
                    clientRegion.Height - 40);
                Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                    expectedWindowHandle,
                    selectedRegion,
                    out target));
                selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    selectedRegion);
            });

            selection = await selectionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(selection);
            Assert.NotNull(target);
            Assert.Equal(expectedWindowHandle, target.WindowHandle);
            Assert.Equal(expectedWindowHandle, target.ScrollTargetHandle);
            using var wheelMonitor = new ScrollCaptureWheelMonitor(
                selectedRegion,
                _ => WpfTestHost.Invoke(() =>
                {
                    var lockTask = selection.LockForScrollingAsync();
                    Assert.True(lockTask.IsCompletedSuccessfully);
                }));

            Assert.True(ForegroundWindowCaptureService.Scroll(target, -120));
            var detectedDelta = await wheelMonitor.WheelEvents
                .ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(-120, detectedDelta);
            var windowUnderSelection = ForegroundWindowCaptureService
                .GetWindowHandleUnderRegionCenter(selectedRegion);
            Assert.Equal(
                expectedWindowHandle,
                ForegroundWindowCaptureService.GetRootWindowHandle(
                    windowUnderSelection));

            // The coordinator briefly hides the overlay while discovering the
            // real scroll target. Restoring it must preserve the native hole.
            await selection.SetVisibleAsync(isVisible: false);
            await selection.SetVisibleAsync(isVisible: true);
            windowUnderSelection = ForegroundWindowCaptureService
                .GetWindowHandleUnderRegionCenter(selectedRegion);
            Assert.Equal(
                expectedWindowHandle,
                ForegroundWindowCaptureService.GetRootWindowHandle(
                    windowUnderSelection));

        }
        finally
        {
            selection?.Dispose();
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public async Task PreviewRemainsVisibleWhileOverlayIsTemporarilyHiddenAndRestored()
    {
        ScrollCaptureSelection? selection = null;
        ScrollCaptureProgressWindow? progressWindow = null;

        try
        {
            var selectedRegion = new ScreenRegion(120, 120, 420, 280);
            Task<ScrollCaptureSelection?>? selectionTask = null;
            WpfTestHost.Invoke(() =>
            {
                selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync(
                    selectedRegion);
            });
            selection = await selectionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(selection);

            WpfTestHost.Invoke(() =>
            {
                progressWindow = new ScrollCaptureProgressWindow
                {
                    Owner = selection.OverlayWindow,
                };
                progressWindow.Show();
                Assert.True(progressWindow.IsVisible);
                progressWindow.Owner = null;
            });

            await selection.SetVisibleAsync(isVisible: false);
            WpfTestHost.Invoke(() => Assert.True(progressWindow!.IsVisible));
            await selection.SetVisibleAsync(isVisible: true);

            WpfTestHost.Invoke(() =>
            {
                progressWindow!.Owner = selection.OverlayWindow;
                progressWindow.BringToFront();
                Assert.True(progressWindow.IsVisible);
                Assert.Same(selection.OverlayWindow, progressWindow.Owner);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => progressWindow?.CloseFromCoordinator());
            selection?.Dispose();
        }
    }

    [Fact]
    public async Task CapturesAndStitchesUserControlledScrolling()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureTarget? target = null;
        var wheelEvents = Channel.CreateUnbounded<int>();

        WpfTestHost.Invoke(() =>
        {
            var content = new StackPanel
            {
                Width = 420,
            };

            for (var index = 0; index < 50; index++)
            {
                content.Children.Add(new Border
                {
                    Height = 72,
                    Background = new SolidColorBrush(CreateColor(index)),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    Child = new TextBlock
                    {
                        Margin = new Thickness(16, 20, 0, 0),
                        FontSize = 24,
                        Foreground = Brushes.White,
                        Text = $"Controlled scroll content {index:D2}",
                    },
                });
            }

            scrollViewer = new ScrollViewer
            {
                Background = Brushes.Black,
                CanContentScroll = false,
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
            {
                var nextOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + (-eventArgs.Delta),
                    0,
                    scrollViewer.ScrollableHeight);
                scrollViewer.ScrollToVerticalOffset(nextOffset);
                eventArgs.Handled = true;
            };

            window = new Window
            {
                Width = 420,
                Height = 300,
                Content = scrollViewer,
                Left = 40,
                Top = 40,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                WindowStyle = WindowStyle.None,
            };
            window.Show();
            window.Activate();
            scrollViewer.ScrollToTop();
            window.UpdateLayout();

            var windowHandle = new WindowInteropHelper(window).Handle;
            Assert.True(ForegroundWindowCaptureService.TryGetClientScreenRegion(
                windowHandle,
                out var clientRegion));
            var selectedRegion = new ScreenRegion(
                clientRegion.X + 12,
                clientRegion.Y + 12,
                clientRegion.Width - 24,
                clientRegion.Height - 24);
            Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                windowHandle,
                selectedRegion,
                out target));
            Assert.Equal(selectedRegion, target?.CaptureRegion);
            scrollViewer.ScrollToVerticalOffset(384);
            window.UpdateLayout();
        });

        try
        {
            Assert.NotNull(target);
            var controlledScrollViewer = Assert.IsType<ScrollViewer>(scrollViewer);
            var completionRequested = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var previewStates = new List<ScrollCapturePreviewState>();
            var captureVisibilityStates = new ConcurrentQueue<bool>();
            await Task.Delay(250);
            var captureTask = ScrollCaptureService.CaptureOnWheelAsync(
                target,
                completionRequested.Task,
                wheelEvents.Reader,
                new ScrollCaptureOptions(
                    MaximumFrames: 8,
                    ScrollDelta: -240,
                    MinimumOverlapRows: 24,
                    MinimumOverlapConfidence: 0.93,
                    MinimumNewRows: 8,
                    FrameDelayMilliseconds: 80),
                setPreviewVisibilityAsync: (isVisible, _) =>
                {
                    captureVisibilityStates.Enqueue(isVisible);
                    return Task.CompletedTask;
                },
                previewChanged: previewStates.Add);

            await Task.Delay(240);
            WpfTestHost.Invoke(() =>
            {
                controlledScrollViewer.ScrollToVerticalOffset(504);
                window!.UpdateLayout();
            });
            wheelEvents.Writer.TryWrite(-120);
            await Task.Delay(350);
            WpfTestHost.Invoke(() =>
            {
                controlledScrollViewer.ScrollToVerticalOffset(384);
                window!.UpdateLayout();
            });
            wheelEvents.Writer.TryWrite(120);
            await Task.Delay(350);
            WpfTestHost.Invoke(() =>
            {
                controlledScrollViewer.ScrollToVerticalOffset(264);
                window!.UpdateLayout();
            });
            wheelEvents.Writer.TryWrite(120);
            await Task.Delay(350);

            completionRequested.TrySetResult();
            wheelEvents.Writer.TryComplete();
            var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Image);
            Assert.Contains(
                previewStates,
                previewState => previewState.AddedBelowFrameCount > 0);
            Assert.Contains(
                previewStates,
                previewState => previewState.AddedAboveFrameCount > 0);
            Assert.Contains(false, captureVisibilityStates);
            Assert.Contains(true, captureVisibilityStates);
            Assert.True(captureVisibilityStates.TryPeek(out _));
            Assert.True(captureVisibilityStates.Last());

            double finalOffset = 0;
            WpfTestHost.Invoke(
                () => finalOffset = controlledScrollViewer.VerticalOffset);
            Assert.True(finalOffset < 384);

            using (result.Image)
            {
                Assert.True(result.Image.Bitmap.Height > target.CaptureRegion.Height);
            }
        }
        finally
        {
            wheelEvents.Writer.TryComplete();
            WpfTestHost.Invoke(() =>
            {
                window?.Close();
            });
        }
    }

    private static Color CreateColor(int index)
    {
        return Color.FromRgb(
            (byte)((index * 73 + 31) & 0xff),
            (byte)((index * 47 + 89) & 0xff),
            (byte)((index * 29 + 151) & 0xff));
    }
}
