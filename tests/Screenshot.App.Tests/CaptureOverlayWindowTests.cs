using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Pin;

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
    public async Task InlineEditorStaysInTheOverlayAndCheckmarkCompletesCapture()
    {
        CaptureOverlayWindow? overlay = null;
        var editorReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay.FindName("InlineEditorCanvas"));
                editor.HistoryChanged += (_, _) =>
                {
                    if (editor.HasImage)
                    {
                        editorReady.TrySetResult();
                    }
                };

                var editButton = Assert.IsType<Button>(overlay.FindName("EditButton"));
                editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            await editorReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                Assert.True(overlay?.IsVisible);
                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay?.FindName("InlineEditorCanvas"));
                var tools = Assert.IsType<StackPanel>(
                    overlay?.FindName("InlineEditorTools"));
                var confirmButton = Assert.IsType<Button>(
                    overlay?.FindName("ConfirmButton"));

                Assert.True(editor.HasImage);
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Equal(Visibility.Visible, tools.Visibility);
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
    public async Task RightClickUndoesInlineEditBeforeLeavingEditor()
    {
        CaptureOverlayWindow? overlay = null;
        var editorReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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

                var editor = Assert.IsType<ImageEditorCanvas>(
                    overlay.FindName("InlineEditorCanvas"));
                editor.HistoryChanged += (_, _) =>
                {
                    if (editor.HasImage)
                    {
                        editorReady.TrySetResult();
                    }
                };

                var editButton = Assert.IsType<Button>(overlay.FindName("EditButton"));
                editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });

            await editorReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

                returnMethod.Invoke(overlay, null);

                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay?.FindName("SelectionRectangle"));
                var editorTools = Assert.IsType<StackPanel>(
                    overlay?.FindName("InlineEditorTools"));

                Assert.False(editor.HasImage);
                Assert.Equal(Visibility.Collapsed, editor.Visibility);
                Assert.Equal(Visibility.Visible, selectionRectangle.Visibility);
                Assert.Equal(Visibility.Collapsed, editorTools.Visibility);

                returnMethod.Invoke(overlay, null);
                Assert.Equal(Visibility.Collapsed, selectionRectangle.Visibility);

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
                var selectionLeft = Canvas.GetLeft(selectionRectangle);
                var selectionTop = Canvas.GetTop(selectionRectangle);
                var outlineLeft = Canvas.GetLeft(outline);
                var outlineTop = Canvas.GetTop(outline);
                Assert.True(surface.IsHitTestVisible);
                Assert.Equal(Visibility.Visible, outline.Visibility);
                Assert.True(outline.Width > 0);
                Assert.True(outline.Height > 0);
                Assert.Equal(Visibility.Visible, topMask.Visibility);
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
                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay!.FindName("SelectionRectangle"));
                var outline = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay.FindName("ScrollCaptureOutline"));
                Assert.Equal(Visibility.Collapsed, selectionRectangle.Visibility);
                Assert.Equal(Visibility.Visible, outline.Visibility);
            });

            await selection.SetVisibleAsync(isVisible: true);
            WpfTestHost.Invoke(() =>
            {
                var selectionRectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay!.FindName("SelectionRectangle"));
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
    public async Task RightClickCancelsPublishedScrollSelectionAfterClearingIt()
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

            var returnMethod = typeof(CaptureOverlayWindow).GetMethod(
                "ReturnToPreviousCaptureState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(returnMethod);

            WpfTestHost.Invoke(() => returnMethod.Invoke(overlay, null));
            WpfTestHost.Invoke(() =>
            {
                var rectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(
                    overlay!.FindName("SelectionRectangle"));
                Assert.Equal(Visibility.Collapsed, rectangle.Visibility);
                Assert.Equal(0, rectangle.Width);
                Assert.Equal(0, rectangle.Height);
            });

            WpfTestHost.Invoke(() => returnMethod.Invoke(overlay, null));
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
}
