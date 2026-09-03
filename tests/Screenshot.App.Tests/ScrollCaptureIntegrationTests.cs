using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
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
        var firstWheelCallbackCount = 0;

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
                    Interlocked.Increment(ref firstWheelCallbackCount);
                    var lockTask = selection.LockForScrollingAsync();
                    Assert.True(lockTask.IsCompletedSuccessfully);
                }));

            Assert.True(ForegroundWindowCaptureService.Scroll(target, -120));
            var detectedDelta = await wheelMonitor.WheelEvents
                .ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(-120, detectedDelta);
            Assert.True(ForegroundWindowCaptureService.Scroll(target, -120));
            detectedDelta = await wheelMonitor.WheelEvents
                .ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(-120, detectedDelta);
            Assert.Equal(1, Volatile.Read(ref firstWheelCallbackCount));
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
                    scrollViewer.VerticalOffset + (-eventArgs.Delta / 3d),
                    0,
                    scrollViewer.ScrollableHeight);
                scrollViewer.ScrollToVerticalOffset(nextOffset);
                scrollViewer.UpdateLayout();
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
            var previewStates = new ConcurrentQueue<ScrollCapturePreviewState>();
            var captureVisibilityStates = new ConcurrentQueue<bool>();
            await Task.Delay(250);
            var captureTask = ScrollCaptureService.CaptureOnWheelAsync(
                target,
                completionRequested.Task,
                wheelEvents.Reader,
                new ScrollCaptureOptions(
                    MaximumFrames: 80,
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
                previewChanged: previewStates.Enqueue);

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

            // Exercise the live sampler and matcher queue with a compact burst:
            // accelerate down, reverse for several frames, fling down again,
            // then reverse once more. Short inter-frame delays intentionally
            // let capture outpace matching for part of the run.
            var stressSequence = new (double Offset, int WheelDelta)[]
            {
                (304, -120),
                (384, -120),
                (524, -240),
                (724, -240),
                (884, -120),
                (824, 120),
                (704, 120),
                (524, 240),
                (344, 240),
                (244, 120),
                (304, -120),
                (444, -240),
                (664, -240),
                (804, -120),
                (684, 120),
                (484, 240),
                (324, 120),
                (224, 120),
            };

            foreach (var (offset, wheelDelta) in stressSequence)
            {
                WpfTestHost.Invoke(() =>
                {
                    controlledScrollViewer.ScrollToVerticalOffset(offset);
                    window!.UpdateLayout();
                });
                wheelEvents.Writer.TryWrite(wheelDelta);
                await Task.Delay(35);
            }

            double bottomOffset = 0;
            double currentOffset = 0;
            WpfTestHost.Invoke(() =>
            {
                bottomOffset = controlledScrollViewer.ScrollableHeight;
                currentOffset = controlledScrollViewer.VerticalOffset;
            });
            foreach (var offset in CreateOffsetSteps(
                         currentOffset,
                         bottomOffset,
                         maximumStep: 180))
            {
                WpfTestHost.Invoke(() =>
                {
                    controlledScrollViewer.ScrollToVerticalOffset(offset);
                    window!.UpdateLayout();
                });
                wheelEvents.Writer.TryWrite(-120);
                await Task.Delay(45);
            }

            // Keep throwing the wheel at the unchanged bottom long enough for
            // the sampler to enqueue an ordered boundary marker.
            for (var index = 0; index < 6; index++)
            {
                wheelEvents.Writer.TryWrite(-120);
                await Task.Delay(50);
            }

            var firstBottomHeight = await WaitForPreviewHeightToStabilizeAsync(
                previewStates);

            var priorOffset = bottomOffset;
            foreach (var offset in new[]
                     {
                         Math.Max(0, bottomOffset - 180),
                         Math.Max(0, bottomOffset - 360),
                         Math.Max(0, bottomOffset - 540),
                         Math.Max(0, bottomOffset - 360),
                         Math.Max(0, bottomOffset - 180),
                         bottomOffset,
                     })
            {
                var wheelDelta = offset < priorOffset
                    ? 120
                    : -120;
                WpfTestHost.Invoke(() =>
                {
                    controlledScrollViewer.ScrollToVerticalOffset(offset);
                    window!.UpdateLayout();
                });
                wheelEvents.Writer.TryWrite(wheelDelta);
                await Task.Delay(55);
                priorOffset = offset;
            }

            for (var index = 0; index < 5; index++)
            {
                wheelEvents.Writer.TryWrite(-120);
                await Task.Delay(45);
            }

            var returnedBottomHeight = await WaitForPreviewHeightToStabilizeAsync(
                previewStates);
            Assert.Equal(firstBottomHeight, returnedBottomHeight);

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
            Assert.Equal(bottomOffset, finalOffset);

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

    [Fact]
    public async Task ContinuousControlledInputDoesNotPauseOrTearWhileCapturing()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureTarget? target = null;
        Assert.True(ForegroundWindowCaptureService.TryGetCursorPosition(
            out var originalCursorPosition));
        var virtualScreen = VirtualScreen.GetBounds();
        var parkedCursor = new ScreenPoint(
            virtualScreen.X + virtualScreen.Width - 8,
            virtualScreen.Y + virtualScreen.Height - 8);
        ForegroundWindowCaptureService.RestoreCursorPosition(parkedCursor);
        var expectedCursor = parkedCursor;

        WpfTestHost.Invoke(() =>
        {
            scrollViewer = new ScrollViewer
            {
                Background = Brushes.Black,
                CanContentScroll = false,
                Content = new VerticalBandPatternElement(
                    width: 420,
                    height: 6000),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
            scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
            {
                var nextOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + (-eventArgs.Delta / 3d),
                    0,
                    scrollViewer.ScrollableHeight);
                scrollViewer.ScrollToVerticalOffset(nextOffset);
                scrollViewer.UpdateLayout();
                eventArgs.Handled = true;
            };

            window = new Window
            {
                Width = 420,
                Height = 170,
                Content = scrollViewer,
                Left = 80,
                Top = 80,
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
                clientRegion.X + 8,
                clientRegion.Y + 8,
                clientRegion.Width - 16,
                clientRegion.Height - 16);
            Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                windowHandle,
                selectedRegion,
                out target));
        });

        ControlledScrollDriver? driver = null;
        try
        {
            Assert.NotNull(target);
            var controlledScrollViewer = Assert.IsType<ScrollViewer>(scrollViewer);
            driver = new ControlledScrollDriver(target);
            await Task.Delay(250);
            driver.SetDirection(ScrollCaptureDirection.Down);

            var offsets = new List<double>();
            for (var sample = 0; sample < 40; sample++)
            {
                if (sample == 20)
                {
                    expectedCursor = new ScreenPoint(
                        parkedCursor.X - 160,
                        parkedCursor.Y - 80);
                    ForegroundWindowCaptureService.RestoreCursorPosition(
                        expectedCursor);
                }

                await Task.Delay(80);
                using var frame = driver.CaptureFrame();
                AssertVerticalBandContinuity(frame);
                WpfTestHost.Invoke(
                    () => offsets.Add(controlledScrollViewer.VerticalOffset));
            }

            driver.SetDirection(null);
            Assert.False(
                driver.HasInputFailure,
                $"Controlled input failed at {driver.InputFailureStage} " +
                $"({driver.InputFailureCode}).");
            Assert.True(
                driver.InputStepCount >= 10,
                "The fixed wheel driver did not deliver enough input steps.");
            Assert.True(
                offsets[^1] - offsets[0] >= 80,
                $"Expected continuous travel, but offsets only moved from " +
                $"{offsets[0]:F1} to {offsets[^1]:F1}.");

            var longestStationaryRun = 0;
            var currentStationaryRun = 0;
            var firstMovementIndex = offsets.FindIndex(
                offset => offset > offsets[0] + 0.1);
            Assert.True(firstMovementIndex >= 0, "Wheel input never moved the viewport.");
            for (var index = firstMovementIndex + 1;
                 index < offsets.Count;
                 index++)
            {
                if (offsets[index] <= offsets[index - 1] + 0.1)
                {
                    currentStationaryRun++;
                    longestStationaryRun = Math.Max(
                        longestStationaryRun,
                        currentStationaryRun);
                }
                else
                {
                    currentStationaryRun = 0;
                }
            }

            Assert.True(
                longestStationaryRun <= 4,
                $"Continuous input paused for {longestStationaryRun} consecutive " +
                $"80 ms samples. Offsets: {string.Join(", ", offsets.Select(
                    offset => offset.ToString(
                        "F0",
                        System.Globalization.CultureInfo.InvariantCulture)))}");
            Assert.True(ForegroundWindowCaptureService.TryGetCursorPosition(
                out var cursorAfterInput));
            Assert.Equal(expectedCursor, cursorAfterInput);
        }
        finally
        {
            if (driver is not null)
            {
                await driver.DisposeAsync();
            }

            WpfTestHost.Invoke(() => window?.Close());
            ForegroundWindowCaptureService.RestoreCursorPosition(
                originalCursorPosition);
        }
    }

    [Fact]
    public async Task ControlledCaptureProducesContinuousCompositeAfterPause()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureTarget? target = null;
        Assert.True(ForegroundWindowCaptureService.TryGetCursorPosition(
            out var originalCursorPosition));
        var virtualScreen = VirtualScreen.GetBounds();
        ForegroundWindowCaptureService.RestoreCursorPosition(new ScreenPoint(
            virtualScreen.X + virtualScreen.Width - 8,
            virtualScreen.Y + virtualScreen.Height - 8));

        WpfTestHost.Invoke(() =>
        {
            scrollViewer = new ScrollViewer
            {
                Background = Brushes.Black,
                CanContentScroll = false,
                Content = new VerticalBandPatternElement(
                    width: 420,
                    height: 6000),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
            scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
            {
                var nextOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + (-eventArgs.Delta / 3d),
                    0,
                    scrollViewer.ScrollableHeight);
                scrollViewer.ScrollToVerticalOffset(nextOffset);
                scrollViewer.UpdateLayout();
                eventArgs.Handled = true;
            };

            window = new Window
            {
                Width = 420,
                Height = 170,
                Content = scrollViewer,
                Left = 120,
                Top = 120,
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
                clientRegion.X + 8,
                clientRegion.Y + 8,
                clientRegion.Width - 16,
                clientRegion.Height - 16);
            Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                windowHandle,
                selectedRegion,
                out target));
        });

        var pointerActions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        var completionRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.NotNull(target);
            var controlledScrollViewer = Assert.IsType<ScrollViewer>(scrollViewer);

            await BringWindowToFrontForCaptureAsync(window!);

            var captureTask = ScrollCaptureService.CaptureControlledAsync(
                target,
                completionRequested.Task,
                pointerActions.Reader,
                ScrollCaptureOptions.Default with
                {
                    MaximumFrames = 200,
                    MinimumOverlapConfidence = 0.90,
                    MinimumNewRows = 1,
                },
                stateChanged: state =>
                {
                    if (state == ControlledScrollCaptureState.PausedDown)
                    {
                        paused.TrySetResult();
                    }
                });

            await Task.Delay(250);
            Assert.True(pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.Click));

            var movementDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            double offset = 0;
            while (DateTime.UtcNow < movementDeadline)
            {
                WpfTestHost.Invoke(
                    () =>
                    {
                        controlledScrollViewer.UpdateLayout();
                        window!.UpdateLayout();
                        offset = controlledScrollViewer.VerticalOffset;
                    });
                if (offset >= 260)
                {
                    break;
                }

                await Task.Delay(80);
            }

            Assert.True(offset >= 260, $"Capture only scrolled {offset:F1} px.");
            Assert.True(pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.Click));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completionRequested.TrySetResult();
            pointerActions.Writer.TryComplete();

            var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Image);
            using (result.Image)
            {
                Assert.True(
                    result.Image.Bitmap.Height >= target.CaptureRegion.Height + 240,
                    $"Composite height was only {result.Image.Bitmap.Height}.");
                AssertCompositeBandContinuity(result.Image.Bitmap);
            }
        }
        finally
        {
            completionRequested.TrySetResult();
            pointerActions.Writer.TryComplete();
            WpfTestHost.Invoke(() => window?.Close());
            ForegroundWindowCaptureService.RestoreCursorPosition(
                originalCursorPosition);
        }
    }

    [Fact]
    public async Task ControlledCaptureReturnsToStartAndPrependsUpwardFrames()
    {
        Window? window = null;
        ScrollViewer? scrollViewer = null;
        ScrollCaptureTarget? target = null;
        Assert.True(ForegroundWindowCaptureService.TryGetCursorPosition(
            out var originalCursorPosition));
        var pointerActions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        var completionRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scrollingUp = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedUp = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                scrollViewer = new ScrollViewer
                {
                    Background = Brushes.Black,
                    CanContentScroll = false,
                    Content = new VerticalBandPatternElement(
                        width: 420,
                        height: 6000),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    PanningMode = PanningMode.VerticalOnly,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                };
                scrollViewer.PreviewMouseWheel += (_, eventArgs) =>
                {
                    var nextOffset = Math.Clamp(
                        scrollViewer.VerticalOffset + (-eventArgs.Delta / 3d),
                        0,
                        scrollViewer.ScrollableHeight);
                    scrollViewer.ScrollToVerticalOffset(nextOffset);
                    scrollViewer.UpdateLayout();
                    eventArgs.Handled = true;
                };

                window = new Window
                {
                    Width = 420,
                    Height = 170,
                    Content = scrollViewer,
                    Left = 160,
                    Top = 160,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowStyle = WindowStyle.None,
                };
                window.Show();
                window.Activate();
                scrollViewer.ScrollToVerticalOffset(800);
                window.UpdateLayout();

                var windowHandle = new WindowInteropHelper(window).Handle;
                Assert.True(ForegroundWindowCaptureService.TryGetClientScreenRegion(
                    windowHandle,
                    out var clientRegion));
                var selectedRegion = new ScreenRegion(
                    clientRegion.X + 8,
                    clientRegion.Y + 8,
                    clientRegion.Width - 16,
                    clientRegion.Height - 16);
                Assert.True(ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                    windowHandle,
                    selectedRegion,
                    out target));
            });

            Assert.NotNull(target);
            var controlledScrollViewer = Assert.IsType<ScrollViewer>(scrollViewer);
            var insideCursor = new ScreenPoint(
                target.CaptureRegion.X + 24,
                target.CaptureRegion.Y + 36);
            ForegroundWindowCaptureService.RestoreCursorPosition(insideCursor);

            await BringWindowToFrontForCaptureAsync(window!);

            var captureTask = ScrollCaptureService.CaptureControlledAsync(
                target,
                completionRequested.Task,
                pointerActions.Reader,
                ScrollCaptureOptions.Default with
                {
                    MaximumFrames = 240,
                    MinimumOverlapConfidence = 0.90,
                    MinimumNewRows = 1,
                },
                stateChanged: state =>
                {
                    if (state == ControlledScrollCaptureState.ScrollingUp)
                    {
                        scrollingUp.TrySetResult();
                    }
                    else if (state == ControlledScrollCaptureState.PausedUp)
                    {
                        pausedUp.TrySetResult();
                    }
                });

            await Task.Delay(250);
            Assert.True(pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.Click));
            var downwardOffset = await WaitForVerticalOffsetAsync(
                controlledScrollViewer,
                () => window!,
                offset => offset >= 1080,
                TimeSpan.FromSeconds(8));
            Assert.True(downwardOffset >= 1080);

            Assert.True(pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.DoubleClick));
            await scrollingUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var upwardOffset = await WaitForVerticalOffsetAsync(
                controlledScrollViewer,
                () => window!,
                offset => offset <= 520,
                TimeSpan.FromSeconds(8));
            Assert.True(upwardOffset <= 520);

            Assert.True(pointerActions.Writer.TryWrite(
                ScrollCapturePointerAction.Click));
            await pausedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completionRequested.TrySetResult();
            pointerActions.Writer.TryComplete();

            var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Image);
            using (result.Image)
            {
                Assert.True(
                    result.Image.Bitmap.Height >= target.CaptureRegion.Height + 550,
                    $"Composite height was only {result.Image.Bitmap.Height}.");
                AssertCompositeBandContinuity(result.Image.Bitmap);
            }

            Assert.True(ForegroundWindowCaptureService.TryGetCursorPosition(
                out var cursorAfterCapture));
            Assert.Equal(insideCursor, cursorAfterCapture);
        }
        finally
        {
            completionRequested.TrySetResult();
            pointerActions.Writer.TryComplete();
            WpfTestHost.Invoke(() => window?.Close());
            ForegroundWindowCaptureService.RestoreCursorPosition(
                originalCursorPosition);
        }
    }

    private static void AssertVerticalBandContinuity(
        System.Drawing.Bitmap frame)
    {
        int? priorBand = null;
        var decodedRows = 0;
        for (var y = 2; y < frame.Height - 2; y += 2)
        {
            if (!TryDecodeDominantBand(frame, y, priorBand, out var band))
            {
                continue;
            }

            decodedRows++;
            if (priorBand is { } prior)
            {
                Assert.True(
                    band == prior || band == prior + 1,
                    $"Captured frame jumped from pattern band {prior} to {band} " +
                    $"at y={y}; the screen changed during CopyFromScreen.");
            }

            priorBand = band;
        }

        Assert.True(
            decodedRows >= frame.Height / 3,
            $"Only {decodedRows} of {frame.Height / 2} sampled rows matched the " +
            "test pattern.");
    }

    private static void AssertCompositeBandContinuity(
        System.Drawing.Bitmap composite)
    {
        var bands = new List<int>(composite.Height);
        int? priorBand = null;
        for (var y = 0; y < composite.Height; y++)
        {
            if (TryDecodeDominantBand(composite, y, priorBand, out var band))
            {
                bands.Add(band);
                priorBand = band;
            }
        }

        Assert.True(
            bands.Count >= composite.Height * 0.70,
            $"Only {bands.Count} of {composite.Height} composite rows matched " +
            "the source pattern.");
        var runs = new List<(int Band, int Length)>();
        foreach (var band in bands)
        {
            if (runs.Count == 0 || runs[^1].Band != band)
            {
                if (runs.Count > 0)
                {
                    Assert.Equal(runs[^1].Band + 1, band);
                }

                runs.Add((band, 1));
                continue;
            }

            var run = runs[^1];
            runs[^1] = (run.Band, run.Length + 1);
        }

        Assert.True(runs.Count >= 20, $"Only {runs.Count} bands were captured.");
        var interiorRuns = runs.Skip(1).SkipLast(1).ToArray();
        var orderedLengths = interiorRuns
            .Select(run => run.Length)
            .OrderBy(length => length)
            .ToArray();
        var medianLength = orderedLengths[orderedLengths.Length / 2];
        foreach (var run in interiorRuns)
        {
            Assert.InRange(
                run.Length,
                Math.Max(1, medianLength - 2),
                medianLength + 2);
        }
    }

    private static bool TryDecodeDominantBand(
        System.Drawing.Bitmap bitmap,
        int y,
        int? priorBand,
        out int band)
    {
        var counts = new Dictionary<int, int>();
        for (var x = 2; x < bitmap.Width - 2; x += 8)
        {
            if (VerticalBandPatternElement.TryDecodeBand(
                    bitmap.GetPixel(x, y),
                    out var candidate))
            {
                counts[candidate] = counts.GetValueOrDefault(candidate) + 1;
            }
        }

        if (counts.Count == 0)
        {
            band = -1;
            return false;
        }

        if (priorBand is { } prior)
        {
            var continuous = counts
                .Where(pair => pair.Key == prior || pair.Key == prior + 1)
                .OrderByDescending(pair => pair.Value)
                .FirstOrDefault();
            if (continuous.Value >= 2)
            {
                band = continuous.Key;
                return true;
            }

            band = -1;
            return false;
        }

        var dominant = counts.MaxBy(pair => pair.Value);
        band = dominant.Key;
        return dominant.Value >= Math.Max(2, bitmap.Width / 40);
    }

    private sealed class VerticalBandPatternElement : FrameworkElement
    {
        private const int BandHeight = 8;
        private const int RedBase = 32;
        private const int GreenBase = 32;
        private const int ColorRange = 192;
        private const int Blue = 96;
        private readonly double _width;
        private readonly double _height;

        public VerticalBandPatternElement(double width, double height)
        {
            _width = width;
            _height = height;
            Width = width;
            Height = height;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        public static bool TryDecodeBand(
            System.Drawing.Color color,
            out int band)
        {
            var red = color.R - RedBase;
            var green = color.G - GreenBase;
            if (red is < 0 or >= ColorRange ||
                green is < 0 or >= ColorRange ||
                Math.Abs(color.B - Blue) > 1)
            {
                band = -1;
                return false;
            }

            band = (green * ColorRange) + red;
            return true;
        }

        protected override System.Windows.Size MeasureOverride(
            System.Windows.Size availableSize)
        {
            return new System.Windows.Size(_width, _height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var bandCount = (int)Math.Ceiling(_height / BandHeight);
            for (var band = 0; band < bandCount; band++)
            {
                var color = Color.FromRgb(
                    (byte)(RedBase + (band % ColorRange)),
                    (byte)(GreenBase + ((band / ColorRange) % ColorRange)),
                    Blue);
                drawingContext.DrawRectangle(
                    new SolidColorBrush(color),
                    pen: null,
                    new Rect(
                        0,
                        band * BandHeight,
                        _width,
                        Math.Min(BandHeight, _height - (band * BandHeight))));
            }

            // Keep the center column clean for output validation, while both
            // sides carry enough row-level texture for the overlap matcher at
            // fractional DPI scales.
            for (var row = 0; row < _height; row += 4)
            {
                var detailColor = Color.FromRgb(
                    (byte)((row * 17 + 41) & 0xff),
                    (byte)((row * 29 + 83) & 0xff),
                    (byte)((row * 43 + 127) & 0xff));
                var brush = new SolidColorBrush(detailColor);
                drawingContext.DrawRectangle(
                    brush,
                    pen: null,
                    new Rect(0, row, _width * 0.35, 1));
                drawingContext.DrawRectangle(
                    brush,
                    pen: null,
                    new Rect(_width * 0.65, row + 1, _width * 0.35, 1));
            }
        }
    }

    private static Color CreateColor(int index)
    {
        return Color.FromRgb(
            (byte)((index * 73 + 31) & 0xff),
            (byte)((index * 47 + 89) & 0xff),
            (byte)((index * 29 + 151) & 0xff));
    }

    private static IEnumerable<double> CreateOffsetSteps(
        double start,
        double end,
        double maximumStep)
    {
        var current = start;
        while (current + maximumStep < end)
        {
            current += maximumStep;
            yield return current;
        }

        yield return end;
    }

    private static async Task<int> WaitForPreviewHeightToStabilizeAsync(
        ConcurrentQueue<ScrollCapturePreviewState> previewStates)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var stableSince = DateTime.UtcNow;
        var lastHeight = previewStates.Select(state => state.PixelHeight)
            .DefaultIfEmpty(0)
            .Max();

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            var height = previewStates.Select(state => state.PixelHeight)
                .DefaultIfEmpty(0)
                .Max();
            if (height != lastHeight)
            {
                lastHeight = height;
                stableSince = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500))
            {
                return height;
            }
        }

        return lastHeight;
    }

    private static async Task<double> WaitForVerticalOffsetAsync(
        ScrollViewer scrollViewer,
        Func<Window> getWindow,
        Func<double, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        double offset = 0;
        while (DateTime.UtcNow < deadline)
        {
            WpfTestHost.Invoke(() =>
            {
                scrollViewer.UpdateLayout();
                getWindow().UpdateLayout();
                offset = scrollViewer.VerticalOffset;
            });
            if (predicate(offset))
            {
                return offset;
            }

            await Task.Delay(60);
        }

        return offset;
    }

    private static async Task BringWindowToFrontForCaptureAsync(Window window)
    {
        // Other desktop tests can leave a closing topmost window above the
        // next one for a compositor frame. Reassert z-order immediately before
        // the baseline capture so the initial and scrolled frames always come
        // from the same HWND.
        WpfTestHost.Invoke(() =>
        {
            window.Topmost = false;
            window.Topmost = true;
            window.Activate();
            window.UpdateLayout();
        });
        await Task.Delay(150);
    }
}
