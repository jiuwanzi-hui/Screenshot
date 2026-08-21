using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;
using WpfScaleTransform = System.Windows.Media.ScaleTransform;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;

namespace Screenshot.App.Tests;

public sealed class ImageEditorCanvasTests
{
    [Fact]
    public void NumberAnnotationsAreRenumberedAfterDeletingAnEarlierMarker()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(140, 90, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 140, displayHeight: 90);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var labelMethod = typeof(ImageEditorCanvas).GetMethod(
                "GetNumberLabel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            Assert.NotNull(labelMethod);

            var document = Assert.IsType<EditorDocument>(documentField.GetValue(editor));
            var first = new NumberAnnotation(new WpfPoint(10, 10), 28, System.Windows.Media.Colors.Red);
            var second = new NumberAnnotation(new WpfPoint(50, 10), 28, System.Windows.Media.Colors.Red);
            var third = new NumberAnnotation(new WpfPoint(90, 10), 28, System.Windows.Media.Colors.Red);
            document.Add(first);
            document.Add(second);
            document.Add(third);

            Assert.Equal("1", Assert.IsType<string>(labelMethod.Invoke(editor, [first])));
            Assert.Equal("2", Assert.IsType<string>(labelMethod.Invoke(editor, [second])));
            Assert.Equal("3", Assert.IsType<string>(labelMethod.Invoke(editor, [third])));

            selectedField.SetValue(editor, 1);
            Assert.True(editor.DeleteSelectedAnnotation());
            Assert.Equal("1", Assert.IsType<string>(labelMethod.Invoke(editor, [first])));
            Assert.Equal("2", Assert.IsType<string>(labelMethod.Invoke(editor, [third])));
        });
    }

    [Fact]
    public void NumberAnnotationCanMoveAndResize()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(140, 90, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 140, displayHeight: 90);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var originalField = typeof(ImageEditorCanvas).GetField(
                "_annotationEditOriginal",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var startField = typeof(ImageEditorCanvas).GetField(
                "_annotationEditStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var editingField = typeof(ImageEditorCanvas).GetField(
                "_isEditingAnnotation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var handleField = typeof(ImageEditorCanvas).GetField(
                "_activeAnnotationHandle",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var updateMethod = typeof(ImageEditorCanvas).GetMethod(
                "UpdateAnnotationEdit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            Assert.NotNull(originalField);
            Assert.NotNull(startField);
            Assert.NotNull(editingField);
            Assert.NotNull(handleField);
            Assert.NotNull(updateMethod);

            var number = new NumberAnnotation(
                new WpfPoint(20, 20),
                28,
                System.Windows.Media.Colors.Red);
            var document = Assert.IsType<EditorDocument>(documentField.GetValue(editor));
            document.Add(number);
            selectedField.SetValue(editor, 0);
            originalField.SetValue(editor, number);
            startField.SetValue(editor, new WpfPoint(30, 30));
            editingField.SetValue(editor, true);
            handleField.SetValue(editor, -1);

            updateMethod.Invoke(editor, [new WpfPoint(40, 45)]);
            var moved = Assert.IsType<NumberAnnotation>(Assert.Single(document.Annotations));
            Assert.Equal(new WpfPoint(30, 35), moved.Position);
            Assert.Equal(28, moved.Size);

            originalField.SetValue(editor, moved);
            startField.SetValue(editor, new WpfPoint(40, 45));
            handleField.SetValue(editor, 8);
            updateMethod.Invoke(editor, [new WpfPoint(56, 61)]);
            var resized = Assert.IsType<NumberAnnotation>(Assert.Single(document.Annotations));
            Assert.Equal(new WpfPoint(30, 35), resized.Position);
            Assert.Equal(44, resized.Size);
        });
    }

    [Fact]
    public void EllipseCanBeSelectedDeletedAndRestoredByUndo()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(100, 80, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 100, displayHeight: 80);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var rebuildMethod = typeof(ImageEditorCanvas).GetMethod(
                "RebuildCanvas",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hitTestMethod = typeof(ImageEditorCanvas).GetMethod(
                "HitTestAnnotation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            Assert.NotNull(rebuildMethod);
            Assert.NotNull(hitTestMethod);
            var document = Assert.IsType<EditorDocument>(documentField.GetValue(editor));
            document.Add(new EllipseAnnotation(
                new System.Windows.Rect(10, 10, 60, 40),
                System.Windows.Media.Colors.Red,
                4));
            rebuildMethod.Invoke(editor, null);

            Assert.Contains(editor.Children.Cast<System.Windows.UIElement>(), child =>
                child is System.Windows.Shapes.Ellipse ellipse &&
                ellipse.Width == 60 && ellipse.Height == 40);
            Assert.Equal(-1, Assert.IsType<int>(hitTestMethod.Invoke(
                editor,
                [new WpfPoint(40, 30)])));
            Assert.Equal(0, Assert.IsType<int>(hitTestMethod.Invoke(
                editor,
                [new WpfPoint(10, 30)])));

            selectedField.SetValue(editor, 0);
            Assert.True(editor.HasSelectedAnnotation);
            Assert.True(editor.DeleteSelectedAnnotation());
            Assert.False(editor.HasSelectedAnnotation);
            Assert.Null(editor.GetAnnotationBounds());

            editor.Undo();
            Assert.NotNull(editor.GetAnnotationBounds());
        });
    }

    [Fact]
    public void ReframeKeepsAnnotationsAndOffsetsThemForTheNewCaptureOrigin()
    {
        WpfTestHost.Invoke(() =>
        {
            using var firstBitmap = new Bitmap(100, 80, PixelFormat.Format32bppPArgb);
            using var secondBitmap = new Bitmap(140, 110, PixelFormat.Format32bppPArgb);
            using var firstImage = new CapturedImage((Bitmap)firstBitmap.Clone());
            using var secondImage = new CapturedImage((Bitmap)secondBitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(firstImage, displayWidth: 100, displayHeight: 80);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            var document = Assert.IsType<EditorDocument>(documentField.GetValue(editor));
            document.Add(new RectangleAnnotation(
                new System.Windows.Rect(20, 15, 30, 25),
                System.Windows.Media.Colors.Red,
                4));
            var before = Assert.IsType<System.Windows.Rect>(
                editor.GetAnnotationBounds());

            editor.Reframe(
                secondImage,
                displayWidth: 140,
                displayHeight: 110,
                new System.Windows.Vector(12, 9));

            var after = Assert.IsType<System.Windows.Rect>(
                editor.GetAnnotationBounds());
            Assert.Equal(before.X + 12, after.X);
            Assert.Equal(before.Y + 9, after.Y);
            Assert.Equal(140, editor.RenderEditedImage().PixelWidth);
            Assert.Equal(110, editor.RenderEditedImage().PixelHeight);
            Assert.True(editor.CanUndo);
            editor.Undo();
            Assert.Null(editor.GetAnnotationBounds());
        });
    }

    [Fact]
    public void RectangleHitTestingTargetsItsBorderInsteadOfItsInterior()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(100, 80, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 100, displayHeight: 80);

            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hitTestMethod = typeof(ImageEditorCanvas).GetMethod(
                "HitTestAnnotation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(hitTestMethod);

            var document = Assert.IsType<EditorDocument>(
                documentField.GetValue(editor));
            document.Add(new RectangleAnnotation(
                new System.Windows.Rect(10, 10, 60, 40),
                System.Windows.Media.Colors.Red,
                4));

            Assert.Equal(
                -1,
                Assert.IsType<int>(hitTestMethod.Invoke(
                    editor,
                    [new WpfPoint(40, 30)])));
            Assert.Equal(
                0,
                Assert.IsType<int>(hitTestMethod.Invoke(
                    editor,
                    [new WpfPoint(10, 30)])));
        });
    }

    [Fact]
    public void RendersTheCapturedImageAtItsOriginalPixelSize()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(48, 36, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 37, 91, 143));
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 96, displayHeight: 72);

            var rendered = editor.RenderEditedImage();

            Assert.True(editor.HasImage);
            Assert.Equal(48, rendered.PixelWidth);
            Assert.Equal(36, rendered.PixelHeight);
            Assert.IsType<WpfScaleTransform>(editor.RenderTransform);
        });
    }

    [Fact]
    public void RenderingDoesNotRearrangeTheVisibleEditorCanvas()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(80, 60, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var parent = new System.Windows.Controls.Canvas
            {
                Width = 500,
                Height = 400,
            };
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 160, displayHeight: 120);
            System.Windows.Controls.Canvas.SetLeft(editor, 137);
            System.Windows.Controls.Canvas.SetTop(editor, 91);
            parent.Children.Add(editor);
            parent.Measure(new System.Windows.Size(500, 400));
            parent.Arrange(new System.Windows.Rect(0, 0, 500, 400));
            var offsetBefore = System.Windows.Media.VisualTreeHelper.GetOffset(editor);
            var transformBefore = editor.RenderTransform;
            var renderSizeBefore = editor.RenderSize;

            var rendered = editor.RenderEditedImage();

            Assert.Equal(80, rendered.PixelWidth);
            Assert.Equal(60, rendered.PixelHeight);
            Assert.Equal(offsetBefore, System.Windows.Media.VisualTreeHelper.GetOffset(editor));
            Assert.Same(transformBefore, editor.RenderTransform);
            Assert.Equal(renderSizeBefore, editor.RenderSize);
        });
    }

    [Fact]
    public void ZoomChangesDisplayExtentWithoutChangingOutputPixels()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(48, 36, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 37, 91, 143));
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 96, displayHeight: 72);
            editor.SetZoom(2);

            Assert.Equal(192, editor.DisplayWidth);
            Assert.Equal(144, editor.DisplayHeight);
            var rendered = editor.RenderEditedImage();
            Assert.Equal(48, rendered.PixelWidth);
            Assert.Equal(36, rendered.PixelHeight);
        });
    }

    [Fact]
    public void MosaicBrushChangesOnlyPixelsAlongItsStroke()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    bitmap.SetPixel(
                        x,
                        y,
                        Color.FromArgb(
                            255,
                            (x * 37 + y * 11) & 0xff,
                            (x * 13 + y * 29) & 0xff,
                            (x * 19 + y * 7) & 0xff));
                }
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 64, displayHeight: 64);
            var baseline = editor.RenderEditedImage();
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
            document.Add(new MosaicAnnotation(
                [new WpfPoint(10, 10), new WpfPoint(54, 54)],
                StrokeWidth: 14,
                BlockSize: 6));
            rebuildMethod.Invoke(editor, null);
            var rendered = editor.RenderEditedImage();

            var stride = rendered.PixelWidth * 4;
            var before = new byte[stride * rendered.PixelHeight];
            var after = new byte[before.Length];
            baseline.CopyPixels(before, stride, 0);
            rendered.CopyPixels(after, stride, 0);
            var changedOnStroke = 0;
            var changedAwayFromStroke = 0;

            for (var y = 0; y < rendered.PixelHeight; y++)
            {
                for (var x = 0; x < rendered.PixelWidth; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    var changed = before[offset] != after[offset] ||
                                  before[offset + 1] != after[offset + 1] ||
                                  before[offset + 2] != after[offset + 2];
                    if (!changed)
                    {
                        continue;
                    }

                    // A round 14px stroke on a 45-degree path projects to a
                    // little over 10px on |x-y|, plus one antialiasing pixel.
                    if (Math.Abs(x - y) <= 12)
                    {
                        changedOnStroke++;
                    }
                    else
                    {
                        changedAwayFromStroke++;
                    }
                }
            }

            Assert.True(changedOnStroke > 100);
            Assert.Equal(0, changedAwayFromStroke);
        });
    }

    [Fact]
    public void TextInputUsesTransparentBackgroundAndVisibleBorder()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(80, 60, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 80, displayHeight: 60);
            var startText = typeof(ImageEditorCanvas).GetMethod(
                "StartTextInput",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(startText);
            startText.Invoke(editor, [new WpfPoint(8, 8)]);

            var input = Assert.Single(editor.Children.OfType<WpfTextBox>());
            Assert.Same(WpfBrushes.Transparent, input.Background);
            Assert.Equal(new System.Windows.Thickness(1), input.BorderThickness);
        });
    }

    [Fact]
    public void SharedRightClickActionUndoesThePreviousAnnotation()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(48, 36, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 48, displayHeight: 36);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            var document = Assert.IsType<EditorDocument>(
                documentField.GetValue(editor));
            document.Add(new RectangleAnnotation(
                new System.Windows.Rect(2, 2, 12, 10),
                System.Windows.Media.Colors.Red,
                2));

            Assert.True(editor.CanUndo);
            Assert.True(editor.TryUndoPreviousOperation());
            Assert.False(editor.CanUndo);
            Assert.False(editor.TryUndoPreviousOperation());
        });
    }

    [Fact]
    public void EmojiAnnotationIsRenderedAndCanBeUndone()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(80, 60, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 80, displayHeight: 60);
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
            document.Add(new EmojiAnnotation(
                new WpfPoint(30, 24),
                EmojiStickerCatalog.Default,
                28));
            rebuildMethod.Invoke(editor, null);

            // Placed stickers rasterize at twice their font size for crispness.
            var placedImage = EmojiStickerRenderer.GetImage(
                EmojiStickerCatalog.Default,
                56);
            Assert.Contains(
                editor.Children.OfType<System.Windows.Controls.Image>(),
                sticker => sticker.Source == placedImage);
            Assert.True(editor.CanUndo);
            editor.Undo();
            Assert.DoesNotContain(
                editor.Children.OfType<System.Windows.Controls.Image>(),
                sticker => sticker.Source == placedImage);
        });
    }

    [Fact]
    public void TaperedArrowSurvivesEveryDragLength()
    {
        // Regression: while an arrow drag is still short, the length-derived
        // head maximum sits below the preferred head minimum. The original
        // Math.Clamp call threw on that inverted range and took the whole
        // application down on the first mouse move.
        var method = typeof(ImageEditorCanvas).GetMethod(
            "CreateTaperedArrowPoints",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        foreach (var strokeWidth in new[] { 1.0, 3.0, 8.0, 24.0 })
        {
            for (var length = 0; length <= 120; length++)
            {
                var points = Assert.IsType<System.Windows.Media.PointCollection>(
                    method.Invoke(
                        null,
                        [
                            new WpfPoint(50, 50),
                            new WpfPoint(50 + length, 50),
                            strokeWidth,
                        ]));
                Assert.True(points.Count >= 2);
            }
        }
    }

    [Fact]
    public void ArrowDefaultsToFilledAndHollowStyleUsesAnOutlinedPolygon()
    {
        var annotation = new ArrowAnnotation(
            new WpfPoint(4, 8),
            new WpfPoint(48, 24),
            System.Windows.Media.Colors.Red,
            4);
        Assert.Equal(ArrowStyle.Filled, annotation.Style);

        WpfTestHost.Invoke(() =>
        {
            var createPolygon = typeof(ImageEditorCanvas).GetMethod(
                "CreateArrowPolygon",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(createPolygon);

            var polygon = Assert.IsType<System.Windows.Shapes.Polygon>(
                createPolygon.Invoke(
                    null,
                    [
                        annotation.Start,
                        annotation.End,
                        annotation.StrokeColor,
                        annotation.StrokeWidth,
                        ArrowStyle.Hollow,
                    ]));

            Assert.Same(WpfBrushes.Transparent, polygon.Fill);
            var stroke = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                polygon.Stroke);
            Assert.Equal(annotation.StrokeColor, stroke.Color);
            Assert.True(polygon.StrokeThickness > 0);
        });
    }

    [Fact]
    public void CurvedArrowFollowsTheDrawnPathAndEndsAtTheMouseTip()
    {
        var createPoints = typeof(ImageEditorCanvas).GetMethod(
            "CreateCurvedArrowPoints",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(createPoints);
        WpfPoint[] path =
        [
            new(10, 70),
            new(24, 42),
            new(52, 20),
            new(88, 18),
            new(122, 34),
            new(142, 62),
        ];

        var polygon = Assert.IsType<System.Windows.Media.PointCollection>(
            createPoints.Invoke(null, [path, 4d]));

        Assert.True(polygon.Count > path.Length * 2);
        Assert.Contains(path[^1], polygon);
        Assert.True(polygon.Min(point => point.Y) < 25);
        Assert.True(polygon.Max(point => point.X) >= path[^1].X);
    }

    [Fact]
    public void CurvedArrowUsesTheSameFilledAndHollowStylesAsStraightArrow()
    {
        WpfTestHost.Invoke(() =>
        {
            var createPolygon = typeof(ImageEditorCanvas).GetMethod(
                "CreateCurvedArrowPolygon",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(createPolygon);
            WpfPoint[] path = [new(8, 60), new(40, 18), new(92, 44)];

            var filled = Assert.IsType<System.Windows.Shapes.Polygon>(
                createPolygon.Invoke(
                    null,
                    [path, System.Windows.Media.Colors.Red, 4d, ArrowStyle.Filled]));
            var hollow = Assert.IsType<System.Windows.Shapes.Polygon>(
                createPolygon.Invoke(
                    null,
                    [path, System.Windows.Media.Colors.Red, 4d, ArrowStyle.Hollow]));

            Assert.IsType<System.Windows.Media.SolidColorBrush>(filled.Fill);
            Assert.Null(filled.Stroke);
            Assert.Same(WpfBrushes.Transparent, hollow.Fill);
            Assert.IsType<System.Windows.Media.SolidColorBrush>(hollow.Stroke);
            Assert.True(hollow.StrokeThickness > 0);
        });
    }

    [Fact]
    public void ChangingSelectedCurvedArrowStyleCanBeUndone()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(120, 90, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 120, displayHeight: 90);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            var document = Assert.IsType<EditorDocument>(documentField.GetValue(editor));
            document.Add(new CurvedArrowAnnotation(
                [new WpfPoint(10, 70), new WpfPoint(55, 18), new WpfPoint(105, 58)],
                System.Windows.Media.Colors.Red,
                4));
            selectedField.SetValue(editor, 0);

            editor.SelectArrowStyle(ArrowStyle.Hollow);

            var changed = Assert.IsType<CurvedArrowAnnotation>(
                Assert.Single(document.Annotations));
            Assert.Equal(ArrowStyle.Hollow, changed.Style);
            editor.Undo();
            var restored = Assert.IsType<CurvedArrowAnnotation>(
                Assert.Single(document.Annotations));
            Assert.Equal(ArrowStyle.Filled, restored.Style);
        });
    }

    [Fact]
    public void ChangingSelectedArrowStyleCanBeUndone()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(100, 80, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 100, displayHeight: 80);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            var document = Assert.IsType<EditorDocument>(
                documentField.GetValue(editor));
            document.Add(new ArrowAnnotation(
                new WpfPoint(10, 12),
                new WpfPoint(70, 44),
                System.Windows.Media.Colors.Red,
                4));
            selectedField.SetValue(editor, 0);

            editor.SelectArrowStyle(ArrowStyle.Hollow);

            var changed = Assert.IsType<ArrowAnnotation>(
                Assert.Single(document.Annotations));
            Assert.Equal(ArrowStyle.Hollow, changed.Style);
            Assert.True(editor.CanUndo);

            editor.Undo();

            var restored = Assert.IsType<ArrowAnnotation>(
                Assert.Single(document.Annotations));
            Assert.Equal(ArrowStyle.Filled, restored.Style);
        });
    }

    [Fact]
    public void MovingAnArrowPreservesItsHollowStyle()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(100, 80, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 100, displayHeight: 80);
            var documentField = typeof(ImageEditorCanvas).GetField(
                "_document",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var selectedField = typeof(ImageEditorCanvas).GetField(
                "_selectedAnnotationIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var editOriginalField = typeof(ImageEditorCanvas).GetField(
                "_annotationEditOriginal",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var editStartField = typeof(ImageEditorCanvas).GetField(
                "_annotationEditStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var editingField = typeof(ImageEditorCanvas).GetField(
                "_isEditingAnnotation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var activeHandleField = typeof(ImageEditorCanvas).GetField(
                "_activeAnnotationHandle",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var updateEdit = typeof(ImageEditorCanvas).GetMethod(
                "UpdateAnnotationEdit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(documentField);
            Assert.NotNull(selectedField);
            Assert.NotNull(editOriginalField);
            Assert.NotNull(editStartField);
            Assert.NotNull(editingField);
            Assert.NotNull(activeHandleField);
            Assert.NotNull(updateEdit);

            var arrow = new ArrowAnnotation(
                new WpfPoint(10, 12),
                new WpfPoint(70, 44),
                System.Windows.Media.Colors.Red,
                4,
                ArrowStyle.Hollow);
            var document = Assert.IsType<EditorDocument>(
                documentField.GetValue(editor));
            document.Add(arrow);
            selectedField.SetValue(editor, 0);
            editOriginalField.SetValue(editor, arrow);
            editStartField.SetValue(editor, new WpfPoint(30, 25));
            editingField.SetValue(editor, true);
            activeHandleField.SetValue(editor, -1);

            updateEdit.Invoke(editor, [new WpfPoint(37, 30)]);

            var moved = Assert.IsType<ArrowAnnotation>(
                Assert.Single(document.Annotations));
            Assert.Equal(ArrowStyle.Hollow, moved.Style);
            Assert.Equal(new WpfPoint(17, 17), moved.Start);
            Assert.Equal(new WpfPoint(77, 49), moved.End);
        });
    }

    [Fact]
    public void EmojiStickersRenderAsColoredImages()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var sticker in EmojiStickerCatalog.All)
            {
                var image = Assert.IsAssignableFrom<WpfBitmapSource>(
                    EmojiStickerRenderer.GetImage(sticker));
                var stride = image.PixelWidth * 4;
                var pixels = new byte[stride * image.PixelHeight];
                image.CopyPixels(pixels, stride, 0);

                var hasOpaquePixel = false;
                var hasColoredPixel = false;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var alpha = pixels[offset + 3];
                    if (alpha < 180)
                    {
                        continue;
                    }

                    hasOpaquePixel = true;
                    if (Math.Max(red, Math.Max(green, blue)) -
                        Math.Min(red, Math.Min(green, blue)) >= 40)
                    {
                        hasColoredPixel = true;
                        break;
                    }
                }

                Assert.True(hasOpaquePixel, $"{sticker} should not be blank.");
                Assert.True(hasColoredPixel, $"{sticker} should contain color.");
            }
        });
    }

    [Fact]
    public void TranslationOverlayIsRenderedIntoTheImageAndCanBeUndoneAsAGroup()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(100, 60, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 20, 24, 28));
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 100, displayHeight: 60);
            editor.AddTranslationOverlay(
            [
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(8, 8, 38, 16),
                    "第一行",
                    16),
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(8, 30, 46, 16),
                    "第二行",
                    16),
            ]);

            Assert.True(editor.HasTranslationOverlay);
            var overlays = editor.Children
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            Assert.Equal(2, overlays.Length);
            var background = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                overlays[0].Background);
            var translatedText = Assert.IsType<System.Windows.Controls.TextBlock>(
                overlays[0].Child);
            var foreground = Assert.IsType<System.Windows.Media.SolidColorBrush>(
                translatedText.Foreground);
            Assert.True(background.Color.R < 80);
            Assert.Equal(byte.MaxValue, background.Color.A);
            Assert.True(foreground.Color.R > 220);
            Assert.InRange(
                translatedText.FontSize,
                TranslationTextLayout.MinimumFontSize,
                12);
            Assert.Equal(System.Windows.TextWrapping.NoWrap, translatedText.TextWrapping);
            Assert.Equal(System.Windows.TextTrimming.None, translatedText.TextTrimming);
            Assert.True(overlays[0].ClipToBounds);
            Assert.Equal(16, overlays[0].Height);

            editor.SetTranslationOverlayVisible(isVisible: false);
            Assert.True(editor.HasTranslationOverlay);
            Assert.False(editor.IsTranslationOverlayVisible);
            Assert.Empty(editor.Children.OfType<System.Windows.Controls.Border>());

            editor.SetTranslationOverlayVisible(isVisible: true);
            Assert.True(editor.IsTranslationOverlayVisible);
            Assert.Equal(
                2,
                editor.Children.OfType<System.Windows.Controls.Border>().Count());

            editor.Undo();

            Assert.False(editor.HasTranslationOverlay);
            Assert.Empty(editor.Children.OfType<System.Windows.Controls.Border>());
        });
    }

    [Fact]
    public void LongTranslationShrinksAndStaysInsideItsOwnRegion()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(260, 120, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 20, 24, 28));
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 260, displayHeight: 120);
            editor.AddTranslationOverlay(
            [
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(8, 8, 150, 36),
                    "This translated sentence is much longer than its source text.",
                    28),
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(8, 48, 150, 30),
                    "The next translated line remains separate.",
                    24),
            ]);

            var overlays = editor.Children
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            Assert.Equal(2, overlays.Length);
            var firstText = Assert.IsType<System.Windows.Controls.TextBlock>(
                overlays[0].Child);

            Assert.InRange(
                firstText.FontSize,
                TranslationTextLayout.MinimumFontSize,
                14);
            Assert.Equal(36, overlays[0].Height);
            Assert.Equal(30, overlays[1].Height);
            Assert.True(overlays[0].ClipToBounds);
            Assert.True(firstText.ClipToBounds);
            Assert.True(
                System.Windows.Controls.Canvas.GetTop(overlays[0]) +
                overlays[0].Height <=
                System.Windows.Controls.Canvas.GetTop(overlays[1]));
        });
    }

    [Fact]
    public void OverlappingTranslationRegionsAreRenderedAsOneStableParagraph()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(320, 140, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(255, 245, 245, 245));
            }

            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 320, displayHeight: 140);
            editor.AddTranslationOverlay(
            [
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(20, 20, 180, 28),
                    "第一行翻译",
                    20),
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(20, 38, 180, 28),
                    "第二行翻译",
                    18),
            ]);

            var overlays = editor.Children
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            Assert.Single(overlays);
            Assert.Contains("第一行翻译", Assert.IsType<System.Windows.Controls.TextBlock>(
                overlays[0].Child).Text);
            Assert.Contains("第二行翻译", Assert.IsType<System.Windows.Controls.TextBlock>(
                overlays[0].Child).Text);
        });
    }

    [Fact]
    public void OverlappingDuplicateTranslationTextIsRenderedOnlyOnce()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new Bitmap(260, 100, PixelFormat.Format32bppPArgb);
            using var image = new CapturedImage((Bitmap)bitmap.Clone());
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, displayWidth: 260, displayHeight: 100);
            editor.AddTranslationOverlay(
            [
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(20, 20, 160, 26),
                    "集体",
                    18),
                new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(20, 42, 160, 14),
                    "集体",
                    12),
            ]);

            var text = Assert.IsType<System.Windows.Controls.TextBlock>(
                Assert.Single(editor.Children.OfType<System.Windows.Controls.Border>()).Child);
            Assert.Equal("集体", text.Text);
        });
    }
}
