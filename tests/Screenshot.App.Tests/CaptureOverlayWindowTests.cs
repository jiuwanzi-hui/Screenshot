using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class CaptureOverlayWindowTests
{
    [Fact]
    public void InteractiveOverlayExposesResizeHandlesAndActionToolbar()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pinnedImageManager = new PinnedImageManager();
            var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = image =>
                {
                    image.Dispose();
                    return Task.CompletedTask;
                },
            });

            try
            {
                Assert.True(overlay.IsVisible);
                Assert.False(overlay.ShowInTaskbar);
                Assert.True(overlay.Topmost);
                Assert.NotNull(overlay.FindName("SelectionRectangle"));
                Assert.NotNull(overlay.FindName("TopLeftResizeThumb"));
                Assert.NotNull(overlay.FindName("TopResizeThumb"));
                Assert.NotNull(overlay.FindName("BottomRightResizeThumb"));
                Assert.NotNull(overlay.FindName("CaptureToolbar"));
                Assert.NotNull(overlay.FindName("TranslateButton"));
                var frozenScreen = Assert.IsType<System.Windows.Controls.Image>(
                    overlay.FindName("FrozenScreenImage"));
                Assert.False(frozenScreen.IsHitTestVisible);
                Assert.NotNull(frozenScreen.Source);
                var snapOutline = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("WindowSnapRectangle"));
                Assert.False(snapOutline.IsHitTestVisible);
                _ = Assert.IsType<ScrollViewer>(
                    overlay.FindName("InlineEmojiPalette"));
                var emojiPanel = Assert.IsType<WrapPanel>(
                    overlay.FindName("InlineEmojiPanel"));
                Assert.Equal(
                    EmojiStickerCatalog.All.Count,
                    emojiPanel.Children.Count);

                var undoButton = Assert.IsType<Button>(
                    overlay.FindName("InlineUndoButton"));
                var redoButton = Assert.IsType<Button>(
                    overlay.FindName("InlineRedoButton"));
                Assert.Equal(36, undoButton.Width);
                Assert.Equal(36, redoButton.Width);
                Assert.Equal(
                    18,
                    Assert.IsType<System.Windows.Shapes.Path>(
                        undoButton.Content).Width);
                Assert.Equal(
                    18,
                    Assert.IsType<System.Windows.Shapes.Path>(
                        redoButton.Content).Width);

                var separator = Assert.IsType<Border>(
                    overlay.FindName("ActionHistorySeparator"));
                Assert.Equal(new Thickness(14, 3, 14, 3), separator.Margin);
                Assert.Equal(2, separator.Width);
            }
            finally
            {
                overlay.Close();
            }
        });
    }

    [Fact]
    public void SelectionBoundsAreNormalizedAndKeptWithinTheCaptureSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pinnedImageManager = new PinnedImageManager();
            var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = image =>
                {
                    image.Dispose();
                    return Task.CompletedTask;
                },
            });

            try
            {
                overlay.UpdateLayout();
                var surface = Assert.IsType<Grid>(overlay.FindName("CaptureSurface"));
                var width = surface.ActualWidth;
                var height = surface.ActualHeight;
                Assert.True(width > 120);
                Assert.True(height > 120);

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var readMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "GetSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(updateMethod);
                Assert.NotNull(readMethod);
                updateMethod.Invoke(overlay, [new Rect(width - 40, height - 40, 100, 80)]);
                var bounds = Assert.IsType<Rect>(readMethod.Invoke(overlay, null));

                Assert.Equal(40, bounds.Width);
                Assert.Equal(40, bounds.Height);
                Assert.Equal(width - 40, bounds.X);
                Assert.Equal(height - 40, bounds.Y);
                var shade = Assert.IsType<Border>(overlay.FindName("CaptureShade"));
                var topMask = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("TopMask"));
                var rightMask = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("RightMask"));
                Assert.Equal(Visibility.Collapsed, shade.Visibility);
                Assert.Equal(Visibility.Visible, topMask.Visibility);
                Assert.Equal(Visibility.Visible, rightMask.Visibility);
                Assert.Equal(bounds.Top, topMask.Height);
                Assert.Equal(width - bounds.Right, rightMask.Width);
            }
            finally
            {
                overlay.Close();
            }
        });
    }

    [Fact]
    public void EditedSelectionResizeStopsBeforeCroppingAnnotationInk()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pinnedImageManager = new PinnedImageManager();
            using var bitmap = new System.Drawing.Bitmap(120, 90);
            using var image = new CapturedImage(
                (System.Drawing.Bitmap)bitmap.Clone());
            var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = capturedImage =>
                {
                    capturedImage.Dispose();
                    return Task.CompletedTask;
                },
            });

            try
            {
                overlay.UpdateLayout();
                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var resizeMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "ResizeSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var readMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "GetSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(resizeMethod);
                Assert.NotNull(readMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 120, 90)]);

                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay.FindName("InlineEditorCanvas"));
                editor.Initialize(image, displayWidth: 120, displayHeight: 90);
                Canvas.SetLeft(editor, 30);
                Canvas.SetTop(editor, 30);
                var documentField = typeof(ImageEditorCanvas).GetField(
                    "_document",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var document = Assert.IsType<EditorDocument>(
                    documentField?.GetValue(editor));
                document.Add(new RectangleAnnotation(
                    new Rect(20, 20, 30, 25),
                    System.Windows.Media.Colors.Red,
                    StrokeWidth: 4));

                resizeMethod.Invoke(overlay, [70d, 0d, 0d, 0d]);

                var resized = Assert.IsType<Rect>(readMethod.Invoke(overlay, null));
                Assert.InRange(resized.Left, 46, 48);
                Assert.Equal(150, resized.Right);
                Assert.Equal(30, Canvas.GetLeft(editor));
                Assert.Equal(30, Canvas.GetTop(editor));
            }
            finally
            {
                overlay.Close();
            }
        });
    }

    [Fact]
    public void CompletedSelectionRejectsBackgroundClickAsANewSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pinnedImageManager = new PinnedImageManager();
            var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = image =>
                {
                    image.Dispose();
                    return Task.CompletedTask;
                },
            });

            try
            {
                overlay.UpdateLayout();
                var surface = Assert.IsType<Grid>(overlay.FindName("CaptureSurface"));
                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var canStartMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "CanStartNewSelectionFromBackground",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(canStartMethod);

                Assert.True(Assert.IsType<bool>(canStartMethod.Invoke(
                    overlay,
                    [surface])));

                updateMethod.Invoke(overlay, [new Rect(30, 30, 160, 100)]);

                Assert.False(Assert.IsType<bool>(canStartMethod.Invoke(
                    overlay,
                    [surface])));
            }
            finally
            {
                overlay.Close();
            }
        });
    }

    [Fact]
    public void CopyButtonIsRemovedBecauseCheckmarkAlreadyCopies()
    {
        WpfTestHost.Invoke(() =>
        {
            var pinnedImageManager = new PinnedImageManager();
            var overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = image =>
                {
                    image.Dispose();
                    return Task.CompletedTask;
                },
            });

            Assert.Null(overlay.FindName("CopyButton"));
            overlay.Close();
            pinnedImageManager.Dispose();
        });
    }

    [Fact]
    public async Task ScrollingCaptureButtonUsesTheCurrentSelection()
    {
        CaptureOverlayWindow? overlay = null;
        ScreenRegion expectedSelection = default;
        var requestedSelection = new TaskCompletionSource<ScreenRegion>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        WpfTestHost.Invoke(() =>
        {
            var pinnedImageManager = new PinnedImageManager();
            overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
            {
                SaveDirectory = Path.GetTempPath(),
                KeepHistory = false,
                HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(),
                PinnedImageManager = pinnedImageManager,
                StartOcrAsync = image =>
                {
                    image.Dispose();
                    return Task.CompletedTask;
                },
                StartScrollCaptureAsync = selection =>
                {
                    requestedSelection.TrySetResult(selection);
                    return Task.CompletedTask;
                },
                CaptureClosed = pinnedImageManager.Dispose,
            });
            overlay.UpdateLayout();

            var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                "UpdateSelectionBounds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getPhysicalBoundsMethod = typeof(CaptureOverlayWindow).GetMethod(
                "GetPhysicalSelectionBounds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(updateMethod);
            Assert.NotNull(getPhysicalBoundsMethod);
            updateMethod.Invoke(overlay, [new Rect(40, 50, 180, 130)]);
            expectedSelection = Assert.IsType<ScreenRegion>(
                getPhysicalBoundsMethod.Invoke(overlay, null));

            var button = Assert.IsType<Button>(
                overlay.FindName("ScrollCaptureButton"));
            Assert.Equal(Visibility.Visible, button.Visibility);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });

        var actualSelection = await requestedSelection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.Equal(expectedSelection, actualSelection);
        WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
    }

    [Fact]
    public async Task ScrollingCaptureReusesAProvidedPhysicalSelection()
    {
        Task<ScrollCaptureSelection?>? selectionTask = null;
        var virtualScreen = VirtualScreen.GetBounds();
        var expected = new ScreenRegion(
            virtualScreen.X + 60,
            virtualScreen.Y + 70,
            180,
            140);

        WpfTestHost.Invoke(() =>
        {
            selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync(expected);
        });

        var selection = await Assert.IsAssignableFrom<Task<ScrollCaptureSelection?>>(
                selectionTask)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(selection);
        Assert.Equal(expected, selection.CaptureRegion);
        selection.Dispose();
    }

    [Fact]
    public async Task SelectionImmediatelyShowsTheInlineEditorAndCheckmarkCompletesCapture()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        var captureClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = () =>
                    {
                        pinnedImageManager.Dispose();
                        captureClosed.TrySetResult();
                    },
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 120, 90)]);
                Assert.Null(overlay.FindName("EditButton"));
                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(enterEditorMethod);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                Assert.True(overlay?.IsVisible);
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var tools = Assert.IsType<StackPanel>(
                    overlay?.FindName("InlineEditorTools"));
                var options = Assert.IsType<StackPanel>(
                    overlay?.FindName("InlineEditorOptions"));
                var resizeThumb = Assert.IsType<System.Windows.Controls.Primitives.Thumb>(
                    overlay?.FindName("BottomRightResizeThumb"));
                var confirmButton = Assert.IsType<Button>(
                    overlay?.FindName("ConfirmButton"));

                Assert.True(editor.HasImage);
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Equal(Visibility.Visible, tools.Visibility);
                Assert.Equal(Visibility.Visible, options.Visibility);
                Assert.Equal(Visibility.Visible, resizeThumb.Visibility);
                Assert.Empty(
                    Application.Current.Windows.OfType<ImageEditorWindow>());

                confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            await captureClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task SelectionCanBeResizedWhileTheInlineEditorIsActive()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        Task? refreshEditorTask = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = pinnedImageManager.Dispose,
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterEditorMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 120, 90)]);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var resizeMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "ResizeSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var completeAdjustmentMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "CompleteSelectionAdjustmentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(resizeMethod);
                Assert.NotNull(completeAdjustmentMethod);
                resizeMethod.Invoke(overlay, [0d, 0d, 24d, 18d]);
                refreshEditorTask = Assert.IsAssignableFrom<Task>(
                    completeAdjustmentMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(refreshEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var getBoundsMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "GetSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(getBoundsMethod);
                var bounds = Assert.IsType<Rect>(
                    getBoundsMethod.Invoke(overlay, null));
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));

                Assert.Equal(144, bounds.Width);
                Assert.Equal(108, bounds.Height);
                Assert.True(editor.HasImage);
                Assert.Equal(Visibility.Visible, editor.Visibility);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task RecognizedTextIsSelectableDirectlyOnTheCapturedImage()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        Task? recognizeTask = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = pinnedImageManager.Dispose,
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterEditorMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 200, 120)]);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var recognizeMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "RecognizeInlineTextAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(recognizeMethod);
                Func<CapturedImage, Task<OcrRecognitionResult>> recognize = _ =>
                    Task.FromResult(new OcrRecognitionResult(
                        true,
                        "可选择文字",
                        ErrorMessage: null)
                    {
                        Regions =
                        [
                            new OcrTextRegion("可选择文字", 12, 18, 90, 24),
                        ],
                    });
                recognizeTask = Assert.IsAssignableFrom<Task>(
                    recognizeMethod.Invoke(overlay, [recognize]));
            });

            await Assert.IsAssignableFrom<Task>(recognizeTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var textOverlay = Assert.IsType<Canvas>(
                    overlay?.FindName("OcrTextOverlay"));
                var textBox = Assert.Single(textOverlay.Children.OfType<TextBox>());
                textBox.SelectAll();

                Assert.Equal(Visibility.Visible, textOverlay.Visibility);
                Assert.True(Panel.GetZIndex(textOverlay) > 80);
                Assert.True(textBox.IsReadOnly);
                Assert.Equal("可选择文字", textBox.SelectedText);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task RightClickReturnsFromSelectionThenCancelsTheCapture()
    {
        CaptureOverlayWindow? overlay = null;
        var captureClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = () =>
                    {
                        pinnedImageManager.Dispose();
                        captureClosed.TrySetResult();
                    },
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var returnMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "ReturnToPreviousCaptureState",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(returnMethod);
                updateMethod.Invoke(overlay, [new Rect(20, 20, 80, 60)]);

                returnMethod.Invoke(overlay, null);

                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("SelectionRectangle"));
                Assert.True(overlay.IsVisible);
                Assert.Equal(Visibility.Collapsed, selectionRectangle.Visibility);
                Assert.Equal(0, selectionRectangle.Width);
                Assert.Equal(0, selectionRectangle.Height);

                returnMethod.Invoke(overlay, null);
            });

            await captureClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task TranslationButtonAddsAReversibleOverlayToTheCapturedImage()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        var translationRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var translationRequestCount = 0;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    RecognizeTextAsync = _ => Task.FromResult(
                        new OcrRecognitionResult(true, "hello", ErrorMessage: null)
                        {
                            Regions =
                            [
                                new OcrTextRegion("hello", 12, 16, 60, 24),
                            ],
                        }),
                    TranslateTextAsync = _ =>
                    {
                        translationRequestCount++;
                        translationRequested.TrySetResult();
                        return Task.FromResult(new TranslationSegmentsResult(
                            true,
                            ["你好"],
                            ErrorMessage: null));
                    },
                    CaptureClosed = pinnedImageManager.Dispose,
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterEditorMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 160, 100)]);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var button = Assert.IsType<Button>(
                    overlay?.FindName("TranslateButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            await translationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var status = Assert.IsType<TextBlock>(
                    overlay?.FindName("CaptureStatusText"));
                Assert.True(editor.HasTranslationOverlay);
                Assert.Contains("复制和保存会包含译文", status.Text);
                Assert.True(editor.CanUndo);
                var selectableTranslation = Assert.IsType<Canvas>(
                    overlay?.FindName("OcrTextOverlay"));
                var translatedTextBox = Assert.Single(
                    selectableTranslation.Children.OfType<TextBox>());
                Assert.Equal(Visibility.Visible, selectableTranslation.Visibility);
                Assert.Equal("你好", translatedTextBox.Text);
                Assert.True(translatedTextBox.IsReadOnly);
                Assert.Equal(
                    "原",
                    Assert.IsType<TextBlock>(
                        overlay?.FindName("TranslateButtonText")).Text);

                var button = Assert.IsType<Button>(
                    overlay?.FindName("TranslateButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(editor.IsTranslationOverlayVisible);
                Assert.Equal(
                    "hello",
                    Assert.Single(selectableTranslation.Children
                        .OfType<TextBox>()).Text);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(editor.IsTranslationOverlayVisible);
                Assert.Equal(
                    "你好",
                    Assert.Single(selectableTranslation.Children
                        .OfType<TextBox>()).Text);
                Assert.Equal(1, translationRequestCount);

                editor.Undo();
                Assert.False(editor.HasTranslationOverlay);
                Assert.Equal(
                    "译",
                    Assert.IsType<TextBlock>(
                        overlay?.FindName("TranslateButtonText")).Text);

                editor.Redo();
                Assert.True(editor.HasTranslationOverlay);
                Assert.True(editor.IsTranslationOverlayVisible);
                Assert.Equal(
                    "你好",
                    Assert.Single(selectableTranslation.Children
                        .OfType<TextBox>()).Text);
                Assert.Equal(
                    "原",
                    Assert.IsType<TextBlock>(
                        overlay?.FindName("TranslateButtonText")).Text);

                editor.Undo();
                Assert.False(editor.HasTranslationOverlay);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(editor.HasTranslationOverlay);
                Assert.True(editor.IsTranslationOverlayVisible);
                Assert.Equal(1, translationRequestCount);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task OcrShortcutModeRecognizesInsideTheCaptureInsteadOfOpeningAResultWindow()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterAndRecognizeTask = null;
        var legacyOcrStarted = false;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        legacyOcrStarted = true;
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    RecognizeTextAfterSelection = true,
                    RecognizeTextAsync = _ => Task.FromResult(
                        new OcrRecognitionResult(true, "快捷键识别", ErrorMessage: null)
                        {
                            Regions =
                            [
                                new OcrTextRegion("快捷键识别", 10, 12, 90, 24),
                            ],
                        }),
                    CaptureClosed = pinnedImageManager.Dispose,
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorForCompletedSelectionAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 160, 100)]);
                enterAndRecognizeTask = Assert.IsAssignableFrom<Task>(
                    enterMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterAndRecognizeTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var textOverlay = Assert.IsType<Canvas>(
                    overlay?.FindName("OcrTextOverlay"));
                var selectableText = Assert.Single(
                    textOverlay.Children.OfType<TextBox>());

                Assert.True(editor.HasImage);
                Assert.Equal(Visibility.Visible, textOverlay.Visibility);
                Assert.Equal("快捷键识别", selectableText.Text);
                Assert.False(legacyOcrStarted);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task TranslationShortcutModeRecognizesAndTranslatesAfterSelection()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterAndTranslateTask = null;
        var translationRequestCount = 0;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    TranslateTextAfterSelection = true,
                    RecognizeTextAsync = _ => Task.FromResult(
                        new OcrRecognitionResult(true, "hello", ErrorMessage: null)
                        {
                            Regions =
                            [
                                new OcrTextRegion("hello", 10, 12, 90, 24),
                            ],
                        }),
                    TranslateTextAsync = _ =>
                    {
                        translationRequestCount++;
                        return Task.FromResult(new TranslationSegmentsResult(
                            true,
                            ["你好"],
                            ErrorMessage: null));
                    },
                    CaptureClosed = pinnedImageManager.Dispose,
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorForCompletedSelectionAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 160, 100)]);
                enterAndTranslateTask = Assert.IsAssignableFrom<Task>(
                    enterMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterAndTranslateTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var textOverlay = Assert.IsType<Canvas>(
                    overlay?.FindName("OcrTextOverlay"));
                var translatedText = Assert.Single(
                    textOverlay.Children.OfType<TextBox>());

                Assert.True(editor.HasTranslationOverlay);
                Assert.True(editor.IsTranslationOverlayVisible);
                Assert.Equal("你好", translatedText.Text);
                Assert.Equal(1, translationRequestCount);
                Assert.Equal(
                    "原",
                    Assert.IsType<TextBlock>(
                        overlay?.FindName("TranslateButtonText")).Text);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task FinalRightClickClosesOnlyAfterItsButtonUpIsConsumed()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        var captureClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = () =>
                    {
                        pinnedImageManager.Dispose();
                        captureClosed.TrySetResult();
                    },
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(enterEditorMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 140, 90)]);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var surface = Assert.IsType<Grid>(
                    overlay?.FindName("CaptureSurface"));
                var downMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "OnCaptureSurfacePreviewMouseRightButtonDown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var upMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "OnCaptureSurfacePreviewMouseRightButtonUp",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(downMethod);
                Assert.NotNull(upMethod);

                var down = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                };
                downMethod.Invoke(overlay, [surface, down]);

                Assert.True(down.Handled);
                Assert.True(overlay?.IsVisible);

                var up = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                };
                upMethod.Invoke(overlay, [surface, up]);
                Assert.True(up.Handled);
            });

            await captureClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task RightClickUndoesThenCancelsWithoutReturningToLegacyToolbar()
    {
        CaptureOverlayWindow? overlay = null;
        Task? enterEditorTask = null;
        var captureClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = () =>
                    {
                        pinnedImageManager.Dispose();
                        captureClosed.TrySetResult();
                    },
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                updateMethod.Invoke(overlay, [new Rect(30, 30, 120, 90)]);

                var enterEditorMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "EnterInlineEditorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(enterEditorMethod);
                enterEditorTask = Assert.IsAssignableFrom<Task>(
                    enterEditorMethod.Invoke(overlay, null));
            });

            await Assert.IsAssignableFrom<Task>(enterEditorTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var returnMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "ReturnToPreviousCaptureState",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(returnMethod);

                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var editorOutline = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay?.FindName("InlineEditorOutline"));
                Assert.Equal(Visibility.Visible, editorOutline.Visibility);
                Assert.Equal(3, editorOutline.StrokeThickness);
                var documentField = typeof(ImageEditorCanvas).GetField(
                    "_document",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var rebuildMethod = typeof(ImageEditorCanvas).GetMethod(
                    "RebuildCanvas",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(documentField);
                Assert.NotNull(rebuildMethod);
                var document = Assert.IsType<EditorDocument>(
                    documentField.GetValue(editor));
                document.Add(new RectangleAnnotation(
                    new Rect(4, 4, 20, 16),
                    System.Windows.Media.Colors.Red,
                    3));
                rebuildMethod.Invoke(editor, null);
                Assert.True(editor.CanUndo);

                returnMethod.Invoke(overlay, null);

                Assert.True(editor.HasImage);
                Assert.False(editor.CanUndo);
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Null(overlay?.FindName("EditButton"));

                returnMethod.Invoke(overlay, null);
            });

            await captureClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task ScrollSelectionKeepsItsOutlineAndCanHideItDuringFrameCapture()
    {
        CaptureOverlayWindow? overlay = null;
        Task<ScrollCaptureSelection?>? selectionTask = null;
        ScrollCaptureSelection? selection = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync();
                overlay = Application.Current.Windows
                    .OfType<CaptureOverlayWindow>()
                    .Last();
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var publishMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "PublishScrollCaptureSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(publishMethod);
                updateMethod.Invoke(overlay, [new Rect(24, 24, 160, 120)]);
                publishMethod.Invoke(overlay, null);
            });

            selection = await selectionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(selection);

            WpfTestHost.Invoke(() =>
            {
                var surface = Assert.IsType<Grid>(overlay!.FindName("CaptureSurface"));
                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("SelectionRectangle"));

                Assert.True(overlay.IsVisible);
                Assert.True(surface.IsHitTestVisible);
                Assert.Equal(Visibility.Visible, selectionRectangle.Visibility);
            });

            await selection.LockForScrollingAsync();
            WpfTestHost.Invoke(() =>
            {
                var surface = Assert.IsType<Grid>(overlay!.FindName("CaptureSurface"));
                var outline = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("ScrollCaptureOutline"));
                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("SelectionRectangle"));
                var topMask = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("TopMask"));
                var shade = Assert.IsType<Border>(
                    overlay.FindName("CaptureShade"));
                var frozenScreen = Assert.IsType<Image>(
                    overlay.FindName("FrozenScreenImage"));
                var selectionLeft = Canvas.GetLeft(selectionRectangle);
                var selectionTop = Canvas.GetTop(selectionRectangle);
                var outlineLeft = Canvas.GetLeft(outline);
                var outlineTop = Canvas.GetTop(outline);
                var windowHandle = new WindowInteropHelper(overlay).Handle;
                Assert.True(NativeMethods.GetWindowDisplayAffinity(
                    windowHandle,
                    out var displayAffinity));
                Assert.Equal(0u, displayAffinity);
                Assert.True(surface.IsHitTestVisible);
                Assert.Equal(Visibility.Visible, outline.Visibility);
                Assert.True(outline.Width > 0);
                Assert.True(outline.Height > 0);
                Assert.Equal(Visibility.Visible, topMask.Visibility);
                Assert.Equal(Visibility.Collapsed, shade.Visibility);
                Assert.Equal(Visibility.Collapsed, frozenScreen.Visibility);
                Assert.Null(frozenScreen.Source);
                Assert.True(outlineLeft + outline.StrokeThickness < selectionLeft);
                Assert.True(outlineTop + outline.StrokeThickness < selectionTop);
                Assert.True(
                    outlineLeft + outline.Width - outline.StrokeThickness >
                    selectionLeft + selectionRectangle.Width);
                Assert.True(
                    outlineTop + outline.Height - outline.StrokeThickness >
                    selectionTop + selectionRectangle.Height);
            });

            await selection.SetVisibleAsync(isVisible: false);
            WpfTestHost.Invoke(() =>
            {
                Assert.False(overlay!.IsVisible);
            });

            await selection.SetVisibleAsync(isVisible: true);
            WpfTestHost.Invoke(() =>
            {
                Assert.True(overlay!.IsVisible);
                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("SelectionRectangle"));
                var outline = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("ScrollCaptureOutline"));
                Assert.Equal(Visibility.Collapsed, selectionRectangle.Visibility);
                Assert.Equal(Visibility.Visible, outline.Visibility);
            });

            selection.Dispose();
            selection = null;
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            selection?.Dispose();
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task RightClickCancelsPublishedScrollSelectionOnButtonUp()
    {
        CaptureOverlayWindow? overlay = null;
        ScrollCaptureSelection? selection = null;
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Task<ScrollCaptureSelection?>? selectionTask = null;
            WpfTestHost.Invoke(() =>
            {
                selectionTask = CaptureOverlayWindow.SelectForScrollCaptureAsync();
                overlay = Application.Current.Windows
                    .OfType<CaptureOverlayWindow>()
                    .Last();
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var publishMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "PublishScrollCaptureSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(publishMethod);
                updateMethod.Invoke(overlay, [new Rect(24, 24, 160, 120)]);
                publishMethod.Invoke(overlay, null);
            });

            selection = await selectionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(selection);
            selection.CancelRequested += () => cancelled.TrySetResult();

            var downMethod = typeof(CaptureOverlayWindow).GetMethod(
                "OnCaptureSurfacePreviewMouseRightButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var upMethod = typeof(CaptureOverlayWindow).GetMethod(
                "OnCaptureSurfacePreviewMouseRightButtonUp",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(downMethod);
            Assert.NotNull(upMethod);

            WpfTestHost.Invoke(() =>
            {
                var surface = Assert.IsType<Grid>(overlay!.FindName("CaptureSurface"));
                var down = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                };
                downMethod.Invoke(overlay, [surface, down]);
                Assert.True(down.Handled);
                Assert.False(cancelled.Task.IsCompleted);

                var rectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("SelectionRectangle"));
                Assert.Equal(Visibility.Visible, rectangle.Visibility);
                Assert.True(rectangle.Width > 0);

                var up = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                };
                upMethod.Invoke(overlay, [surface, up]);
                Assert.True(up.Handled);
            });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            selection?.Dispose();
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    [Fact]
    public async Task ConfirmingAValidSelectionClosesTheInteractiveOverlay()
    {
        CaptureOverlayWindow? overlay = null;
        var captureClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            WpfTestHost.Invoke(() =>
            {
                var pinnedImageManager = new PinnedImageManager();
                overlay = CaptureOverlayWindow.ShowInteractive(new CaptureOverlayOptions
                {
                    SaveDirectory = Path.GetTempPath(),
                    KeepHistory = false,
                    HistoryLimit = 0,
                    HistoryService = new CaptureHistoryService(),
                    PinnedImageManager = pinnedImageManager,
                    StartOcrAsync = image =>
                    {
                        image.Dispose();
                        return Task.CompletedTask;
                    },
                    CaptureClosed = () =>
                    {
                        pinnedImageManager.Dispose();
                        captureClosed.TrySetResult();
                    },
                });
                overlay.UpdateLayout();

                var updateMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "UpdateSelectionBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var confirmMethod = typeof(CaptureOverlayWindow).GetMethod(
                    "ConfirmCurrentSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateMethod);
                Assert.NotNull(confirmMethod);
                updateMethod.Invoke(overlay, [new Rect(20, 20, 80, 60)]);

                // Emulates the WeChat-style double-click / Enter confirm shortcut.
                confirmMethod.Invoke(overlay, null);
            });

            await captureClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() => Assert.False(overlay?.IsVisible));
        }
        finally
        {
            WpfTestHost.Invoke(() => overlay?.Close());
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity);
    }
}
