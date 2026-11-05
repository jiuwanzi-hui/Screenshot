using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class GeneralOcrBoundaryTests
{
    [LocalModelsFact]
    public void MediumReadsUnrelatedOutlinedColumnsWithoutPosterRecovery()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var engine = HighQualityOcrService.CreateEngine(RapidOcrNet.RapidOcrModelSet.PPOCRv6Medium with {
            DetModelPath = manager.ArtisticDetectionModelPath, RecModelPath = manager.ArtisticRecognitionModelPath,
            ClsModelPath = manager.ClassificationModelPath, KeysPath = manager.DictionaryPath }, 6);
        foreach (var font in new[] { "Microsoft YaHei", "KaiTi", "SimSun" })
        foreach (var text in new[] { "江山如画", "春风得意", "山川湖海" })
        {
            using var image = CreateText(font, true, 8, text);
            using var pixels = HighQualityOcrService.CopyBitmapPixels(image.Bitmap);
            var result = engine.Detect(pixels, RapidOcrNet.RapidOcrOptions.PPOCRv6 with {
                ImgResize = pixels.Height, LimitSideLen = 0, MaxSideLen = 1600,
                DoAngle = false, ReturnWordBox = true, ReturnSingleCharBox = true });
            var recognized = string.Concat(result.TextBlocks.Select(b => b.Text));
            Assert.True(text == recognized, $"{font}: expected {text}; recognized {recognized}");
            var characters = result.TextBlocks.SelectMany(b => b.WordResults ?? []).ToArray();
            Assert.Equal(text.Length, characters.Length);
            Assert.True(characters[0].BoxPoints.Average(point => point.Y) < pixels.Height / 2d);
            Assert.True(characters[^1].BoxPoints.Average(point => point.Y) > pixels.Height / 2d);
            Assert.All(result.TextBlocks.SelectMany(b => b.WordResults ?? []), word =>
                Assert.All(word.BoxPoints, point => {
                    Assert.InRange(point.X, 0, pixels.Width); Assert.InRange(point.Y, 0, pixels.Height);
                }));
        }
    }

    [LocalModelsFact]
    public async Task UnrelatedTextLayoutsSurviveTightBoundsAndDifferentSpacing()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var failures = new List<string>();
        foreach (var font in new[] { "Microsoft YaHei", "KaiTi", "SimSun" })
        foreach (var vertical in new[] { true, false })
        foreach (var gap in new[] { 8, 24 })
        {
            using var sample = CreateText(font, vertical, gap);
            foreach (var margin in new[] { 0, 12 })
            {
                using var padded = new CapturedImage(new Bitmap(sample.Bitmap.Width + margin * 2, sample.Bitmap.Height + margin * 2));
                using (var g = Graphics.FromImage(padded.Bitmap))
                {
                    g.Clear(Color.FromArgb(38, 47, 61));
                    g.DrawImage(sample.Bitmap, new Rectangle(margin, margin, sample.Bitmap.Width, sample.Bitmap.Height),
                        0, 0, sample.Bitmap.Width, sample.Bitmap.Height, GraphicsUnit.Pixel);
                }
                var result = await HighQualityOcrService.RecognizeAsync(padded, manager);
                Console.WriteLine(JsonSerializer.Serialize(new { font, vertical, gap, margin, result.Text }));
                var compact = string.Concat(result.Text.Where(c => !char.IsWhiteSpace(c)));
                if (!compact.Contains("春夏秋冬")) failures.Add($"{font}, vertical={vertical}, gap={gap}, margin={margin}: {result.Text}");
                Assert.All(result.Words, w => { Assert.InRange(w.X, 0, padded.PixelWidth); Assert.InRange(w.Y, 0, padded.PixelHeight); });
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [LocalBoundarySampleFact]
    public async Task SubmittedTightSelectionKeepsVisibleTextAcrossSmallBoundaryChanges()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var original = new CapturedImage(new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_OCR_BOUNDARY_SAMPLE")!));
        var failures = new List<string>();
        foreach (var inset in new[] { 0, 2, 4 })
        {
            using var crop = new CapturedImage(original.Bitmap.Clone(new Rectangle(inset, inset,
                original.PixelWidth - inset * 2, original.PixelHeight - inset * 2), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
            var result = await HighQualityOcrService.RecognizeAsync(crop, manager);
            Console.WriteLine(JsonSerializer.Serialize(new { inset, result.Text }));
            // The supplied top row is already clipped. Check the intact lower
            // glyphs; do not demand reconstruction of strokes outside the crop.
            if (!result.Text.Contains('虎') || !result.Text.Contains('猴') || !result.Text.Contains('兔'))
                failures.Add($"inset={inset}: {result.Text}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [LocalModelsFact]
    public async Task DifferentCharacterCountsUseTheSameBoundaryRecovery()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        foreach (var text in new[] { "天地", "春夏秋冬山水" })
        foreach (var vertical in new[] { false, true })
        {
            using var image = CreateText("KaiTi", vertical, 15, text);
            var result = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.Equal(text, string.Concat(result.Text.Where(c => !char.IsWhiteSpace(c))));
        }
    }

    private static CapturedImage CreateText(string fontName, bool vertical, int gap, string text = "春夏秋冬")
    {
        const int cell = 60;
        var length = cell * text.Length + gap * (text.Length - 1);
        var bitmap = new Bitmap(vertical ? cell + 8 : length + 8, vertical ? length + 8 : cell + 8);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(38, 47, 61));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var family = new FontFamily(fontName);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var outline = new Pen(Color.White, 3) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(232, 88, 45));
        for (var i = 0; i < text.Length; i++)
        {
            using var path = new GraphicsPath();
            path.AddString(text[i].ToString(), family, (int)FontStyle.Regular, 48,
                new RectangleF(4 + (vertical ? 0 : i * (cell + gap)), 4 + (vertical ? i * (cell + gap) : 0), cell, cell), format);
            graphics.DrawPath(outline, path);
            graphics.FillPath(fill, path);
        }
        return new CapturedImage(bitmap);
    }

    private class LocalModelsFactAttribute : FactAttribute
    {
        public LocalModelsFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Set the installed OCR model directory for offscreen native regression.";
        }
    }

    private sealed class LocalBoundarySampleFactAttribute : LocalModelsFactAttribute
    {
        public LocalBoundarySampleFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_BOUNDARY_SAMPLE")))
                Skip = "Supply the local tight-selection sample; no private image is bundled.";
        }
    }
}
