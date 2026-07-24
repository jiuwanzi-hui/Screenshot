using System.Windows;
using System.Windows.Media;
using Screenshot.App.Editor;

namespace Screenshot.App.Tests;

public sealed class EditorDocumentTests
{
    [Fact]
    public void SupportsUndoAndRedoForAddedAnnotations()
    {
        var document = new EditorDocument();
        var annotation = new RectangleAnnotation(
            new Rect(10, 20, 30, 40),
            Colors.Teal,
            StrokeWidth: 3);

        document.Add(annotation);

        Assert.Single(document.Annotations);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        document.Undo();

        Assert.Empty(document.Annotations);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);

        document.Redo();

        Assert.Single(document.Annotations);
        Assert.Same(annotation, document.Annotations[0]);
    }

    [Fact]
    public void TransformAnnotationsPreservesUndoForCurrentAnnotations()
    {
        var document = new EditorDocument();
        document.Add(new RectangleAnnotation(
            new Rect(10, 20, 30, 40),
            Colors.Teal,
            StrokeWidth: 3));

        document.TransformAnnotations(annotation =>
        {
            var rectangle = Assert.IsType<RectangleAnnotation>(annotation);
            return rectangle with
            {
                Bounds = new Rect(
                    rectangle.Bounds.X + 8,
                    rectangle.Bounds.Y + 6,
                    rectangle.Bounds.Width,
                    rectangle.Bounds.Height),
            };
        });

        var transformed = Assert.IsType<RectangleAnnotation>(
            Assert.Single(document.Annotations));
        Assert.Equal(new Rect(18, 26, 30, 40), transformed.Bounds);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        document.Undo();

        Assert.Empty(document.Annotations);
        Assert.True(document.CanRedo);
    }
}
