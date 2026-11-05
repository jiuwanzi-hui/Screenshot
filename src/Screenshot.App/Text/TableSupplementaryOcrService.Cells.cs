using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

public static partial class TableSupplementaryOcrService
{
    private static async Task<IReadOnlyList<OcrWordRegion>> RecognizeSeparatedCellsAsync(
        CapturedImage image, IReadOnlyList<OcrWordRegion> primary, IReadOnlyList<OcrWordRegion> supplementary,
        HighQualityOcrModelManager? modelManager, string? language, CancellationToken cancellationToken)
    {
        var bounds = TableRecognitionService.FindCellBounds(image.Bitmap, primary.Concat(supplementary).ToArray(), cancellationToken);
        var memory = GC.GetGCMemoryInfo();
        var available = Math.Max(64L * 1024 * 1024, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes);
        // Stay below the detector's 1600-pixel side limit so batching never
        // shrinks cell text. Smaller memory budgets use more, smaller batches.
        var pixelBudget = (int)Math.Clamp(available / 128, 256_000, 1_960_000);
        var batches = PlanCellBatches(bounds, primary, pixelBudget);
        var result = new List<OcrWordRegion>();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var atlas = new CapturedImage(new Bitmap(batch.Max(cell => cell.Destination.Right) + 12,
                batch.Max(cell => cell.Destination.Bottom) + 12, PixelFormat.Format32bppPArgb));
            using (var graphics = Graphics.FromImage(atlas.Bitmap))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                foreach (var cell in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    graphics.DrawImage(image.Bitmap, cell.Destination, cell.Source, GraphicsUnit.Pixel);
                }
            }
            var recognized = await HighQualityOcrService.RecognizeAsync(atlas, modelManager, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!recognized.IsSuccess && language is not null)
                recognized = await OcrService.RecognizeAsync(atlas, language, cancellationToken);
            if (!recognized.IsSuccess) continue;
            foreach (var cell in batch)
            {
                var words = recognized.Words.Where(word => ContainsCenter(cell.Destination, word)).ToArray();
                var text = TableRecognitionService.JoinCellLines(words);
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(new OcrWordRegion(text, cell.Source.X, cell.Source.Y, cell.Source.Width, cell.Source.Height)
                    { IsTableCellText = true });
            }
        }
        return result;
    }

    internal static IReadOnlyList<List<SeparatedCell>> PlanCellBatches(IReadOnlyList<Rectangle> bounds,
        IReadOnlyList<OcrWordRegion> words, int pixelBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelBudget);
        var batches = new List<List<SeparatedCell>>();
        var batch = new List<SeparatedCell>();
        const int sideLimit = 1400;
        const int padding = 12;
        var left = padding;
        var top = padding;
        var shelfHeight = 0;
        var batchRight = 0;
        foreach (var boundsItem in bounds)
        {
            var source = Rectangle.Inflate(boundsItem, -3, -3);
            // Vertical text already has its own rotation pass. Preserve merged
            // cells here instead of cutting them along absent internal rules.
            if (source.Width < 8 || source.Height < 8 || source.Height > source.Width * 1.2) continue;
            var localWords = words.Where(word => ContainsCenter(source, word)).ToArray();
            var typicalHeight = localWords.Length == 0 ? 16 : localWords.Select(word => word.Height).Order().ElementAt(localWords.Length / 2);
            var scale = Math.Clamp(32 / Math.Max(1, typicalHeight), 1, 2);
            if (source.Width * scale + padding * 2 > sideLimit || source.Height * scale + padding * 2 > sideLimit)
                scale = 1;
            if ((source.Width * scale + padding * 2) * (source.Height * scale + padding * 2) > pixelBudget)
                scale = 1;
            var width = (int)Math.Ceiling(source.Width * scale);
            var height = (int)Math.Ceiling(source.Height * scale);
            // Keep the original OCR for cells that cannot fit without reduction.
            if (width + padding * 2 > sideLimit || height + padding * 2 > sideLimit ||
                (long)(width + padding * 2) * (height + padding * 2) > pixelBudget) continue;
            if (left + width + padding > sideLimit)
            {
                left = padding;
                top += shelfHeight + padding * 2;
                shelfHeight = 0;
            }
            if (batch.Count > 0 && (top + height + padding > sideLimit ||
                (long)Math.Max(batchRight, left + width + padding) * (top + Math.Max(shelfHeight, height) + padding) > pixelBudget))
            {
                batches.Add(batch);
                batch = [];
                left = top = padding;
                shelfHeight = batchRight = 0;
            }
            batch.Add(new SeparatedCell(source, new Rectangle(left, top, width, height)));
            left += width + padding * 2;
            shelfHeight = Math.Max(shelfHeight, height);
            batchRight = Math.Max(batchRight, left);
        }
        if (batch.Count > 0) batches.Add(batch);
        return batches;
    }

    internal sealed record SeparatedCell(Rectangle Source, Rectangle Destination);
}
