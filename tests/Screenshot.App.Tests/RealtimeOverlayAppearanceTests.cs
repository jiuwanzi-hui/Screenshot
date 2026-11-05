using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Text;
using Screenshot.App.Text.Realtime;
using Color = System.Drawing.Color;

namespace Screenshot.App.Tests;

public sealed class RealtimeOverlayAppearanceTests
{
    [Theory]
    [InlineData(217, 227, 240)]
    [InlineData(25, 25, 25)]
    public void InlineCoverageMatchesSourceAndFitsItsBoundsWithoutTranslucentCards(byte red, byte green, byte blue)
    {
        using var bitmap = new Bitmap(400, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(red, green, blue));
            using var font = new Font("Arial", 18);
            graphics.DrawString("Trusted answers from your team", font, System.Drawing.Brushes.Gray, 12, 20);
        }
        var region = new OcrTextRegion("Trusted answers from your team", 10, 20, 320, 80) { EstimatedFontSize = 21 };
        var appearance = RealtimeTextAppearance.Read(bitmap, region);
        Assert.Equal(Color.FromArgb(red, green, blue).ToArgb(), appearance.Background);
        WpfTestHost.Invoke(() =>
        {
            var block = new RealtimeTranslationBlock(region, "来自团队的可信答案。工作所需的知识始终触手可及。") { Appearance = appearance };
            var bounds = new Rect(10, 20, 320, 80);
            var box = RealtimeOverlayWindow.CreateInlineBox(block, bounds, 1);
            box.Measure(bounds.Size); box.Arrange(new Rect(bounds.Size)); box.UpdateLayout();
            var background = Assert.IsType<SolidColorBrush>(box.Background);
            Assert.Equal(255, background.Color.A); Assert.Equal(1, background.Opacity);
            Assert.Equal(red, background.Color.R); Assert.Equal(green, background.Color.G); Assert.Equal(blue, background.Color.B);
            Assert.Equal(bounds.Width, box.ActualWidth); Assert.Equal(bounds.Height, box.ActualHeight);
            Assert.Equal(new Thickness(0), box.BorderThickness); Assert.Equal(new CornerRadius(0), box.CornerRadius);
            var text = Assert.IsType<TextBlock>(box.Child);
            Assert.Equal(block.Translation, text.Text.Replace(Environment.NewLine, ""));
            Assert.True(text.Text.Split(Environment.NewLine).Length * text.LineHeight <= bounds.Height - 2 + .5);
            var image = new RenderTargetBitmap(320, 80, 96, 96, PixelFormats.Pbgra32);
            image.Render(box);
            var pixels = new byte[320 * 80 * 4]; image.CopyPixels(pixels, 320 * 4, 0);
            Assert.All(Enumerable.Range(0, 320 * 80), index => Assert.Equal(255, pixels[index * 4 + 3]));
            if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(directory, $"inline-{red}-{green}-{blue}.png")); encoder.Save(file);
            }
        });
    }
}
