using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screenshot.App.Capture;
using Screenshot.App.Editor;

namespace Screenshot.App.Tests;

public sealed class CaptureSelectionMoveTests
{
    [Fact]
    public void QrMarkerOwnsItsClickBeforeSelectionMoveOrFocusHandoff()
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = CreateSelection(out _);
            try
            {
                var recognition = Assert.IsType<Canvas>(overlay.FindName("ContentRecognitionOverlay"));
                var marker = new Button { Content = "●", Width = 22, Height = 22 };
                recognition.Children.Add(marker);
                var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = marker };
                Invoke(overlay, "OnCaptureSurfacePreviewMouseLeftButtonDown", overlay, mouse);
                Assert.False(mouse.Handled);
                Assert.False(marker.IsMouseCaptured);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_isMovingSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!);
            }
            finally { overlay.Close(); }
        });
    }

    [Theory]
    [InlineData("InlineShapeToolButton")]
    [InlineData("InlineBrushToolButton")]
    [InlineData("InlineTextToolButton")]
    public void SelectingAToolReservesTheFirstInteriorDragForAnnotation(string toolName)
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = CreateSelection(out var editor);
            try
            {
                foreach (var point in new[] { new Point(110, 90), new Point(300, 200), new Point(480, 320) })
                    Assert.True(overlay.CanMoveSelectionFromPoint(point));
                Assert.False(overlay.CanMoveSelectionFromPoint(new Point(90, 200)));
                Assert.False(editor.CanUndo);

                var tool = Assert.IsType<RadioButton>(overlay.FindName(toolName));
                tool.IsChecked = true;

                Assert.True(editor.IsAnnotationCreationEnabled);
                Assert.False(editor.CanUndo);
                Assert.False(overlay.CanMoveSelectionFromPoint(new Point(300, 200)));
                Assert.Equal(Visibility.Collapsed,
                    Assert.IsType<Border>(overlay.FindName("SelectionMoveTopLeftZone")).Visibility);
                Invoke(overlay, "UpdateSelectionInteractionCursor", new Point(300, 200));
                Invoke(editor, "UpdateInteractionCursor", new Point(200, 120));
                Invoke(overlay, "UpdateSelectionInteractionCursor", new Point(300, 200));
                Assert.Equal(toolName == "InlineTextToolButton" ? Cursors.IBeam : Cursors.Cross,
                    GetDisplayedCursor(editor));
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void ExistingAnnotationsKeepTheSelectionFixedWithNoDrawingToolSelected()
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = CreateSelection(out var editor);
            try
            {
                editor.AddMosaicRegions([new Rect(10, 10, 60, 40)]);
                Assert.False(editor.IsAnnotationCreationEnabled);
                Assert.True(editor.CanUndo);
                Assert.False(overlay.CanMoveSelectionFromPoint(new Point(300, 200)));
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void InteriorShowsMoveCursorWhileEdgesStillResize()
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = CreateSelection(out var editor);
            try
            {
                Invoke(overlay, "UpdateSelectionInteractionCursor", new Point(300, 200));
                Assert.Equal(Cursors.SizeAll, GetDisplayedCursor(editor));
                var surface = Assert.IsType<Grid>(overlay.FindName("CaptureSurface"));
                Assert.Equal(Cursors.SizeAll, surface.Cursor);
                Invoke(overlay, "UpdateSelectionInteractionCursor", new Point(100, 80));
                Assert.Equal(Cursors.SizeNWSE, surface.Cursor);
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void RepeatedInteriorMouseMovesKeepTheCursorStableThroughoutEventRouting()
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = CreateSelection(out var editor);
            try
            {
                for (var index = 0; index < 20; index++)
                {
                    var point = new Point(200 + index * 5, 150 + index * 3);
                    // A mouse move runs the parent's preview handler, the
                    // canvas handler, then bubbles back through the parent.
                    // Check each step so a transient arrow cannot be hidden
                    // by the final parent handler restoring the move cursor.
                    Invoke(overlay, "UpdateSelectionInteractionCursor", point);
                    Assert.Equal(Cursors.SizeAll, GetDisplayedCursor(editor));
                    Invoke(editor, "UpdateInteractionCursor", new Point(point.X - 100, point.Y - 80));
                    Assert.Equal(Cursors.SizeAll, GetDisplayedCursor(editor));
                    Invoke(overlay, "UpdateSelectionInteractionCursor", point);
                    Assert.Equal(Cursors.SizeAll, GetDisplayedCursor(editor));
                }
            }
            finally { overlay.Close(); }
        });
    }

    private static Cursor GetDisplayedCursor(FrameworkElement source)
    {
        var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.QueryCursorEvent,
            Cursor = Cursors.Arrow,
        };
        source.RaiseEvent(query);
        return query.Cursor;
    }

    private static CaptureOverlayWindow CreateSelection(out ImageEditorCanvas editor)
    {
        var overlay = new CaptureOverlayWindow(null,
            initialScreenSnapshot: new CapturedImage(new System.Drawing.Bitmap(800, 600)),
            deferInitialColorPickerActivation: true);
        var selection = Assert.IsType<System.Windows.Shapes.Rectangle>(overlay.FindName("SelectionRectangle"));
        Canvas.SetLeft(selection, 100);
        Canvas.SetTop(selection, 80);
        selection.Width = 400;
        selection.Height = 260;
        editor = Assert.IsType<ImageEditorCanvas>(overlay.FindName("InlineEditorCanvas"));
        var image = new CapturedImage(new System.Drawing.Bitmap(400, 260));
        typeof(CaptureOverlayWindow).GetField("_inlineEditorImage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(overlay, image);
        editor.Initialize(image, 400, 260);
        Invoke(overlay, "ClearInlineToolSelection");
        return overlay;
    }

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);
}
