using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Screenshot.App.Tests;

public sealed class AnnotationToolExtensionsTests : IDisposable
{
    private readonly AnnotationToolSetting[] _originalPreferences = AnnotationToolPreferences.Snapshot();
    public void Dispose() => AnnotationToolPreferences.Configure(_originalPreferences, null);

    [Theory]
    [InlineData(EditorTool.DoubleArrow, false)]
    [InlineData(EditorTool.HollowDoubleArrow, true)]
    public void DoubleArrowPreviewAndExportKeepTwoSeparateOppositeArrows(EditorTool tool, bool hollow)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new ImageEditorCanvas();
            using var source = new CapturedImage(new Bitmap(240, 180, PixelFormat.Format32bppPArgb));
            editor.Initialize(source, 240, 180);
            editor.SelectTool(tool);
            editor.SelectColor(Colors.Red);
            editor.SetStrokeWidth(8);
            var start = new Point(40, 90);
            var end = new Point(200, 90);
            Set(editor, "_drawingStartPoint", start);
            Call(editor, "CreateDrawingPreview", start);
            var preview = editor.Children.OfType<System.Windows.Shapes.Path>().Single();
            Call(editor, "UpdateDoubleArrowPreview", preview, end);
            var expected = preview.Data.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var figures = preview.Data.GetFlattenedPathGeometry().Figures;
            Assert.Equal(2, figures.Count);
            Call(editor, "AddDrawingAnnotation", end);
            var annotation = Assert.IsType<ArrowAnnotation>(Assert.Single(Document(editor).Annotations));
            Assert.True(annotation.IsDoubleArrow);
            Assert.Equal(hollow ? ArrowStyle.Hollow : ArrowStyle.Filled, annotation.Style);
            Assert.Equal(expected, editor.Children.OfType<System.Windows.Shapes.Path>().Last().Data.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = editor.RenderEditedImage();
            var pixels = Pixels(result);
            // The entire baseline remains transparent: no connecting shaft or
            // seam can appear between the two independent arrows after export.
            for (var x = 40; x <= 200; x++) Assert.Equal(0, pixels[(90 * 240 + x) * 4 + 3]);
            foreach (var figure in figures)
            {
                var centre = new Point(120, figure.StartPoint.Y);
                Assert.Equal(hollow ? 0 : 255, pixels[((int)Math.Round(centre.Y) * 240 + 120) * 4 + 3]);
                Assert.True((bool)CallStatic("IsAnnotationHit", annotation, centre)!);
            }
            Assert.False((bool)CallStatic("IsAnnotationHit", annotation, new Point(120, 90))!);
            var bounds = (Rect)CallStatic("GetAnnotationBounds", annotation)!;
            Assert.True(bounds.Contains(preview.Data.Bounds));
            if (Environment.GetEnvironmentVariable("SNAPCUT_ANNOTATION_PREVIEW_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                using var stream = File.Create(Path.Combine(directory, $"parallel-arrow-{(hollow ? "hollow" : "filled")}.png"));
                encoder.Save(stream);
            }
        });
    }

    [Theory]
    [InlineData(2, 24, 0)]
    [InlineData(12, 24, 45)]
    [InlineData(200, 1, 90)]
    [InlineData(200, 24, 180)]
    [InlineData(200, 3, 0)]
    public void ParallelArrowsKeepOnlyOuterBarbsWithCompactSpacingWhenRotated(double length, double width, double angle)
    {
        WpfTestHost.Invoke(() =>
        {
            var radians = angle * Math.PI / 180;
            var direction = new Vector(Math.Cos(radians), Math.Sin(radians));
            var normal = new Vector(-direction.Y, direction.X);
            var start = new Point(250, 250);
            var end = start + direction * length;
            var geometry = DoubleArrowGeometry.Create(start, end, width);
            Assert.True(geometry.IsFrozen);
            var figures = geometry.GetFlattenedPathGeometry().Figures;
            Assert.Equal(2, figures.Count);
            var top = FigurePoints(figures[0]);
            var bottom = FigurePoints(figures[1]);
            Assert.Equal(7, top.Length);
            Assert.Equal(7, bottom.Length);
            var centreSpacing = Vector.Multiply(bottom[0] - top[0], normal);
            Assert.InRange(centreSpacing, 0, width * 4 + 3);
            Assert.True(Vector.Multiply(top[0] - start, normal) < 0);
            Assert.True(Vector.Multiply(bottom[0] - end, normal) > 0);
            for (var i = 0; i < top.Length; i++)
            {
                Assert.InRange(Vector.Multiply(top[i] - start, direction), -.001, length + .001);
                Assert.True((bottom[i] - new Point(start.X + end.X - top[i].X,
                    start.Y + end.Y - top[i].Y)).Length < .001);
            }
            var single = (PointCollection)CallStatic("CreateTaperedArrowPoints", start, end, width)!;
            var retained = single.Where((_, index) => index != 3).ToArray();
            Assert.All(retained.Select((point, i) => (top[i] - point) - (top[0] - retained[0])), delta =>
                Assert.True(delta.Length < .001));
            var metrics = ArrowGeometryMetrics.For(length, width);
            var shaftHalfWidth = Math.Max(metrics.BaseHalfWidth, metrics.TailHalfWidth);
            // Inside the pair, no head can protrude farther than the shaft.
            Assert.All(top, point => Assert.True(Vector.Multiply(point - top[0], normal) <= shaftHalfWidth + .001));
            Assert.All(bottom, point => Assert.True(Vector.Multiply(point - bottom[0], normal) >= -shaftHalfWidth - .001));
            Assert.All(top, point => Assert.True(Vector.Multiply(point - start, normal) < 0));
            Assert.All(bottom, point => Assert.True(Vector.Multiply(point - start, normal) > 0));
        });
    }

    private static Point[] FigurePoints(PathFigure figure)
    {
        var points = new[] { figure.StartPoint }.Concat(figure.Segments.SelectMany(segment => segment switch
        {
            PolyLineSegment line => line.Points.AsEnumerable(),
            LineSegment line => new[] { line.Point },
            _ => throw new InvalidOperationException("Expected a straight arrow outline."),
        })).ToArray();
        // WPF's flattened representation may repeat the first point to close a figure.
        return points.Length > 1 && (points[^1] - points[0]).Length < .001 ? points[..^1] : points;
    }

    [Fact]
    public void DoubleArrowMigrationPreservesCustomOrderShortcutsAndExplicitVisibility()
    {
        var old = AppSettings.CreateDefault() with
        {
            SettingsVersion = 16,
            CaptureToolbarFeatureOrder = [CaptureToolbarFeature.Save, CaptureToolbarFeature.Arrow, CaptureToolbarFeature.Number],
            VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Arrow],
            CaptureToolbarFeatureShortcuts = new() { [CaptureToolbarFeature.Arrow] = "3" },
        };
        var upgraded = old.Normalize();
        Assert.Equal(new[] { CaptureToolbarFeature.Save, CaptureToolbarFeature.Arrow, CaptureToolbarFeature.DoubleArrow, CaptureToolbarFeature.Number },
            upgraded.CaptureToolbarFeatureOrder.Take(4));
        Assert.Contains(CaptureToolbarFeature.DoubleArrow, upgraded.VisibleCaptureToolbarFeatures);
        Assert.Equal("3", upgraded.CaptureToolbarFeatureShortcuts[CaptureToolbarFeature.Arrow]);
        var hidden = (upgraded with { VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Arrow] }).Normalize();
        Assert.DoesNotContain(CaptureToolbarFeature.DoubleArrow, hidden.VisibleCaptureToolbarFeatures);
    }

    [Fact]
    public void CompletedTranslationRepairReplacesTheOverlayAndCanBeUndone()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new ImageEditorCanvas();
            editor.InitializeAnnotationOverlay(320, 240, 320, 240);
            var first = new TranslatedTextAnnotationRegion(new Rect(10, 20, 100, 30), "第一段", 14);
            var second = new TranslatedTextAnnotationRegion(new Rect(10, 80, 100, 30), "第二段", 14);
            editor.AddTranslationOverlay([first]);
            editor.AddTranslationOverlay([first, second], replaceExisting: true);
            Assert.Equal(2, Assert.IsType<TranslationOverlayAnnotation>(Assert.Single(Document(editor).Annotations)).Regions.Count);
            editor.Undo();
            Assert.Single(Assert.IsType<TranslationOverlayAnnotation>(Assert.Single(Document(editor).Annotations)).Regions);
            editor.Redo();
            Assert.Equal(2, Assert.IsType<TranslationOverlayAnnotation>(Assert.Single(Document(editor).Annotations)).Regions.Count);
        });
    }

    [Theory]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.Ellipse)]
    [InlineData(EditorTool.Arrow)]
    [InlineData(EditorTool.DoubleArrow)]
    [InlineData(EditorTool.HollowDoubleArrow)]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.Line)]
    [InlineData(EditorTool.WavyLine)]
    [InlineData(EditorTool.RectangleMosaic)]
    [InlineData(EditorTool.Mosaic)]
    public void DrawingCommitImmediatelyUpdatesTheVisibleCanvas(EditorTool tool)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = CreateImage();
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96, 72);
            editor.SelectTool(tool);
            editor.SelectColor(Colors.Red);
            byte[] LivePixels()
            {
                editor.Measure(new System.Windows.Size(96, 72));
                editor.Arrange(new Rect(0, 0, 96, 72));
                editor.UpdateLayout();
                var bitmap = new RenderTargetBitmap(96, 72, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(editor);
                return Pixels(bitmap);
            }
            var before = LivePixels();
            var oldVisuals = editor.Children.Cast<UIElement>().ToArray();
            Set(editor, "_drawingStartPoint", new Point(11, 13));
            if (tool is EditorTool.Brush or EditorTool.Mosaic)
                Set(editor, "_brushPoints", new List<Point> { new(11, 13), new(42, 35), new(79, 59) });
            Call(editor, "AddDrawingAnnotation", new Point(79, 59));
            // Do not switch tools or export: both rebuild and used to hide the
            // missing mouse-up invalidation in earlier tests.
            Assert.Single(Document(editor).Annotations);
            Assert.False(before.SequenceEqual(LivePixels()));
            Assert.All(oldVisuals, visual => Assert.Contains(visual, editor.Children.Cast<UIElement>()));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectingAMosaicOrAnotherAnnotationKeepsTheRealMosaicVisible(bool selectOther)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = CreateImage();
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96, 72);
            Draw(editor, EditorTool.RectangleMosaic, new Point(11, 13), new Point(49, 47));
            Draw(editor, EditorTool.Rectangle, new Point(55, 10), new Point(87, 54));
            var index = selectOther ? 1 : 0;
            Set(editor, "_selectedAnnotationIndex", index);
            Call(editor, "RemoveSelectionVisuals");
            var before = LivePixels(editor);
            var visuals = editor.Children.Cast<UIElement>().ToArray();
            var annotation = Document(editor).Annotations[index];
            Call(editor, "StartAnnotationEdit", annotation, new Point(25, 25), -1);
            // Inspect while the button is still down: exporting or committing
            // rebuilds the canvas and used to hide the checkerboard regression.
            Assert.Equal(before, LivePixels(editor));
            Assert.All(visuals, visual => Assert.Contains(visual, editor.Children.Cast<UIElement>()));
            Call(editor, "UpdateAnnotationEdit", new Point(29, 28));
            Assert.Contains(editor.Children.OfType<System.Windows.Controls.Image>(),
                visual => visual.Source is BitmapSource source && source.PixelWidth < 20);
            Call(editor, "CommitAnnotationEdit");
            Call(editor, "RemoveSelectionVisuals");
            Assert.Equal(Pixels(editor.RenderEditedImage()), LivePixels(editor));
        });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void ResizingMosaicShowsRealPixelsBeforeMouseUpAndClipsToItsBounds(double fraction)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = CreateImage();
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96, 72);
            var baseline = LivePixels(editor);
            Draw(editor, EditorTool.RectangleMosaic, new Point(11 + fraction, 13), new Point(49, 47));
            Set(editor, "_selectedAnnotationIndex", 0);
            Call(editor, "StartAnnotationEdit", Document(editor).Annotations[0], new Point(49, 47), 4);
            Call(editor, "UpdateAnnotationEdit", new Point(81, 61));
            var live = LivePixels(editor);
            Assert.Contains(editor.Children.OfType<System.Windows.Controls.Image>(),
                visual => visual.Source is BitmapSource source && source.PixelWidth < 30);
            var bounds = Assert.IsType<RectangleMosaicAnnotation>(Document(editor).Annotations[0]).Bounds;
            for (var y = 0; y < 72; y++)
            for (var x = 0; x < 96; x++)
            {
                if (x >= Math.Floor(bounds.Left) && x < Math.Ceiling(bounds.Right) &&
                    y >= bounds.Top && y < bounds.Bottom) continue;
                Assert.True(baseline.AsSpan((y * 96 + x) * 4, 4).SequenceEqual(live.AsSpan((y * 96 + x) * 4, 4)));
            }
            Call(editor, "CommitAnnotationEdit");
            Call(editor, "RemoveSelectionVisuals");
            Assert.Equal(live, LivePixels(editor));
            Assert.Equal(live, Pixels(editor.RenderEditedImage()));
            editor.Undo();
            Assert.Equal(new Rect(new Point(11 + fraction, 13), new Point(49, 47)),
                Assert.IsType<RectangleMosaicAnnotation>(Document(editor).Annotations[0]).Bounds);
        });
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppArgb)]
    [InlineData(PixelFormat.Format32bppPArgb)]
    public void CompactMosaicPreviewPreservesPartialBlockPixels(PixelFormat format)
    {
        using var bitmap = new Bitmap(39, 31, format);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x * 5, y * 7, (x + y) * 3));
        var bounds = new Int32Rect(3, 5, 33, 25);
        var compact = MosaicRenderer.CreateRectanglePreview(bitmap, bounds, 4);
        var full = MosaicRenderer.CreateRectangle(bitmap, bounds, 4);
        var pixels = Pixels(compact);
        var expected = Pixels(full);
        Assert.Equal(9, compact.PixelWidth);
        Assert.Equal(7, compact.PixelHeight);
        Assert.True(compact.IsFrozen);
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
            Assert.True(expected.AsSpan((y * bounds.Width + x) * 4, 4).SequenceEqual(
                pixels.AsSpan((y / 4 * compact.PixelWidth + x / 4) * 4, 4)));
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.5, true)]
    [InlineData(1.5, true)]
    public void RectangleMosaicCoversExactlyTheSelectedAreaAndSupportsUndo(double scale, bool reverse)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = CreateImage();
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96 * scale, 72 * scale);
            var before = Pixels(editor.RenderEditedImage());
            var start = new Point(11, 13);
            var end = new Point(79, 59);
            Draw(editor, EditorTool.RectangleMosaic, reverse ? end : start, reverse ? start : end);
            var annotation = Assert.IsType<RectangleMosaicAnnotation>(Assert.Single(Document(editor).Annotations));
            Assert.Equal(new Rect(start, end), annotation.Bounds);
            var after = Pixels(editor.RenderEditedImage());
            var changed = 0;
            for (var y = 0; y < 72; y++)
            for (var x = 0; x < 96; x++)
            {
                var offset = (y * 96 + x) * 4;
                if (before.AsSpan(offset, 4).SequenceEqual(after.AsSpan(offset, 4))) continue;
                Assert.True(x >= 11 && x < 79 && y >= 13 && y < 59, $"Changed outside selection: {x},{y}");
                changed++;
            }
            Assert.True(changed > 2500);
            editor.Undo();
            Assert.Equal(before, Pixels(editor.RenderEditedImage()));
            editor.Redo();
            Assert.Equal(after, Pixels(editor.RenderEditedImage()));
        });
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppArgb)]
    [InlineData(PixelFormat.Format32bppPArgb)]
    public void RectangularPixelBufferMatchesBlockSamplesAtClippedEdges(PixelFormat format)
    {
        using var bitmap = new Bitmap(13, 11, format);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x * 17, y * 19, (x + y) * 7));
        var result = MosaicRenderer.CreateRectangle(bitmap, new Int32Rect(-2, 1, 20, 16), 4);
        Assert.Equal(13, result.PixelWidth);
        Assert.Equal(10, result.PixelHeight);
        Assert.True(result.IsFrozen);
        var pixels = Pixels(result);
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 13; x++)
        {
            var expected = bitmap.GetPixel(Math.Min(x / 4 * 4 + 2, 12), Math.Min(y / 4 * 4 + 2, 9) + 1);
            var offset = (y * 13 + x) * 4;
            Assert.Equal(expected.B, pixels[offset]);
            Assert.Equal(expected.G, pixels[offset + 1]);
            Assert.Equal(expected.R, pixels[offset + 2]);
        }
    }

    [Theory]
    [InlineData(EditorTool.Line)]
    [InlineData(EditorTool.WavyLine)]
    [InlineData(EditorTool.RectangleMosaic)]
    public void NewToolsCoalescePointerPacketsAndCommitTheLastPosition(EditorTool tool)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new ImageEditorCanvas();
            editor.InitializeAnnotationOverlay(320, 240, 320, 240);
            editor.SelectTool(tool);
            Set(editor, "_drawingStartPoint", new Point(10, 20));
            Set(editor, "_isDrawing", true);
            Call(editor, "CreateDrawingPreview", new Point(10, 20));
            for (var i = 0; i < 2000; i++)
                Call(editor, "QueuePointerUpdate", new Point(100 + i % 200, 150));
            if (tool == EditorTool.RectangleMosaic)
                Assert.True(double.IsNaN(Get<System.Windows.Shapes.Rectangle>(editor, "_drawingPreview").Width));
            else
                Assert.Null(Get<System.Windows.Shapes.Path>(editor, "_drawingPreview").Data);
            Call(editor, "OnPointerRendering", null, EventArgs.Empty);
            var end = new Point(309, 180);
            Call(editor, "FlushPointerUpdate", end);
            Assert.False(Get<bool>(editor, "_isPointerRenderingSubscribed"));
            Call(editor, "AddDrawingAnnotation", end);
            var annotation = Assert.Single(Document(editor).Annotations);
            if (annotation is LineAnnotation line)
            {
                Assert.Equal(end, line.End);
                Assert.Equal(tool == EditorTool.WavyLine, line.IsWavy);
            }
            else
                Assert.Equal(end, Assert.IsType<RectangleMosaicAnnotation>(annotation).Bounds.BottomRight);
            editor.CancelActiveOperation();
        });
    }

    [Theory]
    [InlineData(false, 80, 0)]
    [InlineData(true, 80, 0)]
    [InlineData(true, 0, 80)]
    [InlineData(true, -60, -40)]
    [InlineData(true, 3, 0)]
    public void LinesHaveStableEndpointsAndEditableHandles(bool wavy, double dx, double dy)
    {
        WpfTestHost.Invoke(() =>
        {
            var start = new Point(100, 100);
            var end = start + new Vector(dx, dy);
            var geometry = LineAnnotationGeometry.Create(start, end, 3, wavy);
            var path = PathGeometry.CreateFromGeometry(geometry);
            Assert.Equal(start, path.Figures[0].StartPoint);
            var last = path.Figures[0].Segments[^1] switch
            {
                PolyLineSegment segment => segment.Points[^1],
                LineSegment segment => segment.Point,
                PolyBezierSegment segment => segment.Points[^1],
                BezierSegment segment => segment.Point3,
                _ => throw new InvalidOperationException("Unexpected line geometry"),
            };
            Assert.InRange((end - last).Length, 0, 0.001);
            Assert.True(geometry.IsFrozen);
            var annotation = new LineAnnotation(start, end, Colors.Red, 3, wavy);
            var handles = (IReadOnlyList<Point>)CallStatic("GetAnnotationHandlePoints", annotation)!;
            Assert.Equal(new[] { start, end }, handles);
            Assert.True((bool)CallStatic("IsAnnotationHit", annotation, start)!);
            Assert.False((bool)CallStatic("IsAnnotationHit", annotation, start + new Vector(50, 100))!);
        });
    }

    [Theory]
    [InlineData(EditorTool.Line)]
    [InlineData(EditorTool.WavyLine)]
    [InlineData(EditorTool.RectangleMosaic)]
    [InlineData(EditorTool.DoubleArrow)]
    [InlineData(EditorTool.HollowDoubleArrow)]
    public void NewAnnotationsMoveResizeAndExportAfterUndoRedo(EditorTool tool)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = CreateImage();
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96, 72);
            Draw(editor, tool, new Point(15, 15), new Point(60, 40));
            var original = Assert.Single(Document(editor).Annotations);
            Set(editor, "_selectedAnnotationIndex", 0);
            Call(editor, "StartAnnotationEdit", original, new Point(15, 15), -1);
            Call(editor, "UpdateAnnotationEdit", new Point(25, 25));
            Call(editor, "CommitAnnotationEdit");
            var moved = Assert.Single(Document(editor).Annotations);
            Assert.NotEqual(original, moved);
            editor.Undo();
            Assert.Equal(original, Assert.Single(Document(editor).Annotations));
            editor.Redo();
            Assert.Equal(moved, Assert.Single(Document(editor).Annotations));
            Set(editor, "_selectedAnnotationIndex", 0);
            var handles = (IReadOnlyList<Point>)CallStatic("GetAnnotationHandlePoints", moved)!;
            var handle = tool == EditorTool.RectangleMosaic ? 4 : 1;
            Call(editor, "StartAnnotationEdit", moved, handles[handle], handle);
            Call(editor, "UpdateAnnotationEdit", new Point(85, 60));
            Call(editor, "CommitAnnotationEdit");
            var resized = Assert.Single(Document(editor).Annotations);
            if (resized is LineAnnotation line) Assert.Equal(new Point(85, 60), line.End);
            else if (resized is ArrowAnnotation arrow)
            {
                Assert.Equal(new Point(85, 60), arrow.End);
                Assert.True(arrow.IsDoubleArrow);
                Assert.Equal(tool == EditorTool.HollowDoubleArrow ? ArrowStyle.Hollow : ArrowStyle.Filled, arrow.Style);
            }
            else Assert.Equal(new Point(85, 60), Assert.IsType<RectangleMosaicAnnotation>(resized).Bounds.BottomRight);
            Assert.NotEqual(Pixels(image.Preview), Pixels(editor.RenderEditedImage()));
            editor.Undo();
            Assert.Equal(moved, Assert.Single(Document(editor).Annotations));
        });
    }

    [Fact]
    public void VariantPreferencesSurviveSettingsSerializationWithoutLosingPerToolStyles()
    {
        var original = AnnotationToolPreferences.Snapshot();
        try
        {
            AnnotationToolSetting[] saved = [];
            AnnotationToolPreferences.Configure([], value => saved = value);
            AnnotationToolPreferences.SetVariant(EditorTool.Mosaic, EditorTool.RectangleMosaic);
            AnnotationToolPreferences.SetVariant(EditorTool.Line, EditorTool.WavyLine);
            AnnotationToolPreferences.SetVariant(EditorTool.DoubleArrow, EditorTool.HollowDoubleArrow);
            AnnotationToolPreferences.SetColor(EditorTool.WavyLine, Colors.Blue);
            AnnotationToolPreferences.SetWidth(EditorTool.RectangleMosaic, 7);
            var json = System.Text.Json.JsonSerializer.Serialize(AppSettings.CreateDefault() with { AnnotationToolSettings = saved });
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.Normalize();
            AnnotationToolPreferences.Configure(settings.AnnotationToolSettings, null);
            Assert.Equal(EditorTool.RectangleMosaic, AnnotationToolPreferences.GetVariant(EditorTool.Mosaic));
            Assert.Equal(EditorTool.WavyLine, AnnotationToolPreferences.GetVariant(EditorTool.Line));
            Assert.Equal(EditorTool.HollowDoubleArrow, AnnotationToolPreferences.GetVariant(EditorTool.DoubleArrow));
            Assert.Equal(Colors.Blue, AnnotationToolPreferences.GetColor(EditorTool.WavyLine, Colors.Red));
            Assert.Equal(7, AnnotationToolPreferences.GetWidth(EditorTool.RectangleMosaic, 3));
            AnnotationToolPreferences.Configure([new("Mosaic", "", 3, "WavyLine")], null);
            Assert.Equal(EditorTool.Mosaic, AnnotationToolPreferences.GetVariant(EditorTool.Mosaic));
        }
        finally { AnnotationToolPreferences.Configure(original, null); }
    }

    [Theory]
    [InlineData("capture", AppTheme.AuroraMist)]
    [InlineData("pin", AppTheme.AuroraMist)]
    [InlineData("editor", AppTheme.AuroraMist)]
    [InlineData("capture", AppTheme.GlassMist)]
    [InlineData("pin", AppTheme.GlassMist)]
    [InlineData("editor", AppTheme.GlassMist)]
    public void AllToolbarsHighlightTheActualColorHideMosaicColorsAndRestoreVariants(string surface, AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var original = AnnotationToolPreferences.Snapshot();
            AnnotationToolPreferences.Configure([], null);
            Window? window = null;
            var owner = new Window();
            using var canvasImage = CreateImage();
            try
            {
                AppThemeManager.ApplySettingsPalette(Application.Current.Resources, theme);
                _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
                AnnotationToolPreferences.SetVariant(EditorTool.Mosaic, EditorTool.RectangleMosaic);
                AnnotationToolPreferences.SetVariant(EditorTool.Line, EditorTool.WavyLine);
            AnnotationToolPreferences.SetVariant(EditorTool.DoubleArrow, EditorTool.HollowDoubleArrow);
                window = surface switch
                {
                    "capture" => new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true),
                    "pin" => new PinnedImageEditorToolbarWindow(owner, AppSettings.CreateDefault()),
                    _ => new ImageEditorWindow(CreateImage(), Path.GetTempPath()),
                };
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var prefix = surface == "capture" ? "Inline" : "";
                if (surface == "editor") Call(window, "InitializeEditorCanvas");
                if (surface == "capture")
                    ((ImageEditorCanvas)window.FindName("InlineEditorCanvas")).Initialize(canvasImage, 96, 72);
                var line = (RadioButton)window.FindName(prefix + "LineToolButton");
                var mosaic = (RadioButton)window.FindName(prefix + "MosaicToolButton");
                var doubleArrow = (RadioButton)window.FindName(prefix + "DoubleArrowToolButton");
                Assert.Equal("HollowDoubleArrow", doubleArrow.Tag);
                Assert.Equal(2, doubleArrow.ContextMenu.Items.Count);
                foreach (MenuItem item in doubleArrow.ContextMenu.Items)
                {
                    var row = Assert.IsType<StackPanel>(item.Header);
                    var icon = Assert.IsType<System.Windows.Shapes.Path>(row.Children[0]);
                    Assert.Equal(2, icon.Data.GetFlattenedPathGeometry().Figures.Count);
                    Assert.IsType<TextBlock>(row.Children[1]);
                    Assert.Null(item.Icon);
                }
                doubleArrow.IsChecked = true;
                ((MenuItem)doubleArrow.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("DoubleArrow", doubleArrow.Tag);
                Assert.True(doubleArrow.IsChecked);
                Assert.Equal(EditorTool.DoubleArrow, AnnotationToolPreferences.GetVariant(EditorTool.DoubleArrow));
                if (theme == AppTheme.GlassMist && Environment.GetEnvironmentVariable("SNAPCUT_ANNOTATION_PREVIEW_DIR") is { Length: > 0 } reviewDirectory)
                {
                    var menu = doubleArrow.ContextMenu;
                    menu.Resources.MergedDictionaries.Add(window.Resources);
                    menu.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/SnapCut;component/Themes/ThemedContextMenus.xaml"),
                    });
                    menu.SetResourceReference(FrameworkElement.StyleProperty, "ThemedContextMenuStyle");
                    menu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                    menu.Arrange(new Rect(new Point(), menu.DesiredSize));
                    menu.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),
                        (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(menu);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(reviewDirectory);
                    using var stream = File.Create(Path.Combine(reviewDirectory, surface + "-double-arrow-menu.png"));
                    encoder.Save(stream);
                    Assert.False(menu.IsOpen);
                }
                Assert.Equal("WavyLine", line.Tag);
                Assert.Equal("RectangleMosaic", mosaic.Tag);
                Assert.Equal(2, mosaic.ContextMenu.Items.Count);
                mosaic.IsChecked = true;
                var custom = (Button)window.FindName(prefix + "CustomColorButton");
                var red = (Button)window.FindName(prefix + "RedColorButton");
                Assert.Equal(Visibility.Collapsed, red.Visibility);
                Assert.Equal(Visibility.Collapsed, custom.Visibility);
                line.IsChecked = true;
                Assert.Equal(Visibility.Visible, red.Visibility);
                red.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(AnnotationColorSwatch.GetIsSelected(red));
                Assert.False(AnnotationColorSwatch.GetIsSelected(custom));
                red.ApplyTemplate();
                Assert.Equal(Visibility.Visible, ((FrameworkElement)red.Template.FindName("SelectedMark", red)).Visibility);
                var brush = (RadioButton)window.FindName(prefix + "BrushToolButton");
                brush.IsChecked = true;
                var cyan = (Button)window.FindName(prefix + "CyanColorButton");
                cyan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(AnnotationColorSwatch.GetIsSelected(cyan));
                line.IsChecked = true;
                Assert.True(AnnotationColorSwatch.GetIsSelected(red));
                SaveToolbarPreview(window, surface, "line-" + theme);
                var straight = (MenuItem)line.ContextMenu.Items[0];
                straight.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("Line", line.Tag);
                Assert.True(line.IsChecked);
                Assert.True(straight.IsChecked);
                Assert.Equal(EditorTool.Line, AnnotationToolPreferences.GetVariant(EditorTool.Line));
                var paintMosaic = (MenuItem)mosaic.ContextMenu.Items[0];
                paintMosaic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal("Mosaic", mosaic.Tag);
                Assert.Equal(Visibility.Collapsed, red.Visibility);
                SaveToolbarPreview(window, surface, "mosaic-" + theme);
            }
            finally
            {
                window?.Close();
                owner.Close();
                AnnotationToolPreferences.Configure(original, null);
            }
        });
    }

    private static CapturedImage CreateImage()
    {
        var bitmap = new Bitmap(96, 72, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, x * 37 % 256, y * 29 % 256, (x + y) * 13 % 256));
        return new CapturedImage(bitmap);
    }

    private static void SaveToolbarPreview(Window window, string surface, string variant)
    {
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_ANNOTATION_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var panel = (FrameworkElement)window.FindName(surface switch
        {
            "capture" => "CaptureToolbar",
            "pin" => "ToolbarSurface",
            _ => "EditorToolbarPanel",
        });
        panel.Visibility = Visibility.Visible;
        Screenshot.App.Presentation.UiTransitions.Finish(panel);
        if (surface == "pin") ((FrameworkElement)window.FindName("EditPanel")).Visibility = Visibility.Visible;
        if (surface == "capture")
        {
            var options = (FrameworkElement)window.FindName("InlineStrokeOptions");
            for (var parent = VisualTreeHelper.GetParent(options) as FrameworkElement;
                 parent is not null && parent != panel; parent = VisualTreeHelper.GetParent(parent) as FrameworkElement)
                parent.Visibility = Visibility.Visible;
        }
        panel.Measure(new System.Windows.Size(surface == "editor" ? 1060 : double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(new Point(), panel.DesiredSize));
        panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),
            (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"{surface}-{variant}.png"));
        encoder.Save(stream);
    }

    private static void Draw(ImageEditorCanvas editor, EditorTool tool, Point start, Point end)
    {
        editor.SelectTool(tool);
        editor.SelectColor(Colors.Red);
        editor.SetStrokeWidth(3);
        Set(editor, "_drawingStartPoint", start);
        Call(editor, "AddDrawingAnnotation", end);
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte[] LivePixels(ImageEditorCanvas editor)
    {
        editor.Measure(new System.Windows.Size(96, 72));
        editor.Arrange(new Rect(0, 0, 96, 72));
        editor.UpdateLayout();
        var bitmap = new RenderTargetBitmap(96, 72, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);
        return Pixels(bitmap);
    }
    private static EditorDocument Document(ImageEditorCanvas editor) => Get<EditorDocument>(editor, "_document");
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static object? CallStatic(string name, params object?[] args) => typeof(ImageEditorCanvas).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
}
