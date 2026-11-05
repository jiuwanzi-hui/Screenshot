using System.Drawing;
using System.Windows;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class PrivacyRedactionBoundsTests
{
    [Theory]
    [InlineData(1.0, 24.0, 16.0, 40.0, 20.0)]
    [InlineData(0.5, 24.5, 16.5, 40.0, 20.0)]
    [InlineData(1.5, -4.0, 16.0, 40.0, 20.0)]
    public void RedactionDoesNotChangePixelsOutsidePrivacyBoundsAndCanBeUndone(
        double scale, double x, double y, double width, double height)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new Bitmap(96, 72));
            for (var row = 0; row < 72; row++)
            for (var column = 0; column < 96; column++)
                image.Bitmap.SetPixel(column, row, Color.FromArgb(column * 2, row * 3, (column + row) % 256));
            var editor = new ImageEditorCanvas();
            editor.Initialize(image, 96 * scale, 72 * scale);
            var before = Pixels(editor.RenderEditedImage());
            var bounds = new Rect(x, y, width, height);
            editor.AddMosaicRegions([bounds]);
            var after = Pixels(editor.RenderEditedImage());
            var changed = 0;
            for (var row = 0; row < 72; row++)
            for (var column = 0; column < 96; column++)
            {
                var offset = (row * 96 + column) * 4;
                if (before.AsSpan(offset, 4).SequenceEqual(after.AsSpan(offset, 4))) continue;
                Assert.True(column >= Math.Floor(bounds.Left) && column < Math.Ceiling(bounds.Right) &&
                    row >= Math.Floor(bounds.Top) && row < Math.Ceiling(bounds.Bottom),
                    $"Changed adjacent pixel: {column},{row}");
                changed++;
            }
            Assert.True(changed > 200);
            editor.Undo();
            Assert.Equal(before, Pixels(editor.RenderEditedImage()));
            editor.Redo();
            Assert.Equal(after, Pixels(editor.RenderEditedImage()));
        });
    }

    [Fact]
    public void EmailUsesActualWordBoundsDespiteLargeGapsAndOtherColumns()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("邮箱：", 10, 10, 50, 20), new("a@example.com", 160, 11, 130, 18),
                new("状态：正常", 350, 10, 100, 20)],
            Regions = [new("邮箱： a@example.com 状态：正常", 10, 10, 440, 20)],
        };
        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));
        Assert.Equal("a@example.com", candidate.Value);
        Assert.Equal(new Rect(160, 11, 130, 18), candidate.Bounds);
    }

    [Fact]
    public void SplitNameUsesValueWordBoundsWithoutCoveringAdjacentField()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("姓名：", 10, 10, 50, 20), new("王云辉", 75, 11, 54, 18),
                new("状态：正常", 150, 10, 100, 20)],
            Regions = [new("姓名：王云辉状态：正常", 10, 10, 240, 20)],
        };
        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));
        Assert.Equal("王云辉", candidate.Value);
        Assert.Equal(new Rect(75, 11, 54, 18), candidate.Bounds);
    }

    [Fact]
    public void IdenticalValuesInSeparateColumnsBothRemainRedactable()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("a@example.com", 10, 10, 130, 20), new("a@example.com", 350, 10, 130, 20)],
            Regions = [new("a@example.com a@example.com", 10, 10, 470, 20)],
        };
        var candidates = PrivacyDetectionService.Detect(result);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(new Rect(10, 10, 130, 20), candidates[0].Bounds);
        Assert.Equal(new Rect(350, 10, 130, 20), candidates[1].Bounds);
    }

    [Fact]
    public void EmailSplitIntoOcrWordsUsesTheirCombinedGeometry()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("邮箱：", 10, 10, 50, 20), new("alice@", 70, 10, 48, 20),
                new("example.com", 124, 10, 100, 20)],
        };
        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));
        Assert.Equal("alice@example.com", candidate.Value);
        Assert.Equal(new Rect(70, 10, 154, 20), candidate.Bounds);
    }

    [Fact]
    public void RegionGeometryHandlesDifferentWordBaselinesAcrossAFieldGap()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("姓名：", 10, 11, 50, 18), new("王云辉", 160, 10, 54, 20),
                new("状态：正常", 350, 10, 100, 20)],
            Regions = [new("姓名：王云辉状态：正常", 10, 10, 440, 20)],
        };
        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));
        Assert.Equal(new Rect(160, 10, 54, 20), candidate.Bounds);
    }

    [Fact]
    public void AddressDoesNotAbsorbAnUnlabeledNeighboringColumn()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = [new("地址：", 10, 10, 50, 20), new("北碚路980号", 70, 11, 100, 18),
                new("今日正常营业", 350, 10, 120, 20)],
            Regions = [new("地址：北碚路980号 今日正常营业", 10, 10, 460, 20)],
        };
        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));
        Assert.Equal("北碚路980号", candidate.Value);
        Assert.Equal(new Rect(70, 11, 100, 18), candidate.Bounds);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
