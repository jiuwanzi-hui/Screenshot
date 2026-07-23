using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;
using WpfScaleTransform = System.Windows.Media.ScaleTransform;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Tests;

public sealed class ImageEditorCanvasTests
{
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
            var addMosaic = typeof(ImageEditorCanvas).GetMethod(
                "AddMosaicVisual",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(addMosaic);
            addMosaic.Invoke(editor, [new MosaicAnnotation(
                [new WpfPoint(10, 10), new WpfPoint(54, 54)],
                StrokeWidth: 14,
                BlockSize: 6)]);
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
}
