using RapidOcrNet;
using SkiaSharp;

namespace Screenshot.App.Text;

// The library rotates tall boxes as sideways lines. Upright CJK columns need
// a separate hypothesis; keep the original model, pixels and score threshold.
internal static class VerticalOcrRecovery
{
    internal const float TextThreshold = 0.45f;

    internal static TextBlock[] Recover(RapidOcr engine, SKBitmap source, RapidOcrOptions options,
        TextBlock[] raw, CancellationToken token)
    {
        var result = new List<TextBlock>(raw.Length);
        var attempts = 0;
        foreach (var original in raw)
        {
            token.ThrowIfCancellationRequested();
            var selected = original;
            if (attempts < 4 &&
                TryGetColumn(original.BoxPoints, source.Width, source.Height, out var column, out var count) &&
                (Score(original) < 0.8f || original.Text.Trim().Length < count && Score(original) < 0.9f))
            {
                attempts++;
                // Aspect ratio is a proposal, not a character count. Spacing,
                // narrow fonts and a tighter selection can change it by one.
                // Try at most two neighbouring layouts only if the first fails.
                foreach (var layoutCount in new[] { count, count - 1, count + 1 })
                {
                    if (layoutCount is < 2 or > 12 || column.Width * layoutCount > 1600) continue;
                    using var strip = Reflow(source, column, layoutCount);
                    var stripOptions = options with
                    {
                        ImgResize = Math.Max(strip.Width, strip.Height), TextScore = TextThreshold,
                        ReturnSingleCharBox = true,
                    };
                    var candidates = engine.Detect(strip, stripOptions).TextBlocks;
                    token.ThrowIfCancellationRequested();
                    // Large displayed glyphs can be split into separate detector
                    // boxes after reflow. One bounded normalized retry stabilizes
                    // that layout without changing the original screenshot or the
                    // confidence threshold. Accepted character boxes map back below.
                    if (strip.Height > 128 && (candidates.Length != 1 || !IsBetterColumn(original, candidates[0], layoutCount)))
                    {
                        using var normalized = strip.Resize(new SKImageInfo(
                            (int)Math.Round(strip.Width * 128d / strip.Height), 128), new SKSamplingOptions(SKFilterMode.Linear));
                        var retry = engine.Detect(normalized, stripOptions with { ImgResize = normalized.Width }).TextBlocks;
                        token.ThrowIfCancellationRequested();
                        if (retry.Length == 1 && IsBetterColumn(original, retry[0], layoutCount))
                        {
                            foreach (var word in retry[0].WordResults!)
                                for (var i = 0; i < word.BoxPoints.Length; i++)
                                    word.BoxPoints[i] = new(
                                        (int)Math.Round(word.BoxPoints[i].X * strip.Width / (double)normalized.Width),
                                        (int)Math.Round(word.BoxPoints[i].Y * strip.Height / (double)normalized.Height));
                            candidates = retry;
                        }
                    }
                    if (candidates.Length == 1 && IsBetterColumn(original, candidates[0], layoutCount))
                    {
                        selected = new TextBlock
                        {
                            Text = candidates[0].Text, Chars = candidates[0].Chars,
                            CharScores = candidates[0].CharScores, WordResults = candidates[0].WordResults,
                            BoxPoints = original.BoxPoints, BoxScore = original.BoxScore,
                        };
                        if (selected.WordResults is { } words)
                            foreach (var word in words)
                                MapCharacter(word.BoxPoints, column, layoutCount, strip.Height);
                        break;
                    }
                }
            }
            // Raw low-score boxes are retained only long enough to inspect
            // geometry. They never bypass the existing output threshold.
            if (!string.IsNullOrWhiteSpace(selected.Text) && Score(selected) >= TextThreshold)
                result.Add(selected);
        }
        return result.ToArray();
    }

    internal static bool TryGetColumn(SKPointI[] points, int width, int height, out SKRectI column, out int count)
    {
        column = default; count = 0;
        if (points.Length != 4) return false;
        column = new(Math.Clamp(points.Min(p => p.X), 0, width), Math.Clamp(points.Min(p => p.Y), 0, height),
            Math.Clamp(points.Max(p => p.X), 0, width), Math.Clamp(points.Max(p => p.Y), 0, height));
        if (column.Width < 12 || column.Height < column.Width * 2.2 || column.Height > column.Width * 12 ||
            Math.Abs(points[0].X - points[3].X) > column.Width * 0.15 ||
            Math.Abs(points[1].X - points[2].X) > column.Width * 0.15) return false;
        count = (int)Math.Round(column.Height / (double)column.Width);
        return count is >= 2 and <= 12 && column.Width * count <= 1600 &&
            (long)column.Width * column.Height <= 640_000;
    }

    private static SKBitmap Reflow(SKBitmap source, SKRectI column, int count)
    {
        var strip = new SKBitmap(column.Width * count, column.Height / count, source.ColorType, source.AlphaType);
        try
        {
            using var canvas = new SKCanvas(strip);
            canvas.Clear(SKColors.White);
            for (var i = 0; i < count; i++)
                canvas.DrawBitmap(source,
                    new SKRect(column.Left, column.Top + i * column.Height / (float)count,
                        column.Right, column.Top + (i + 1) * column.Height / (float)count),
                    new SKRect(i * column.Width, 0, (i + 1) * column.Width, strip.Height));
            return strip;
        }
        catch { strip.Dispose(); throw; }
    }

    internal static bool IsBetterColumn(TextBlock original, TextBlock candidate, int count)
    {
        var text = candidate.Text.Trim();
        // A rotated column can confidently produce just one wrong character.
        // Correct that incomplete hypothesis only with a nearly certain full
        // column, still requiring a material confidence improvement.
        var improves = Score(candidate) >= Score(original) + 0.15f ||
            original.Text.Trim().Length < count && Score(candidate) >= 0.98f &&
            Score(candidate) >= Score(original) + 0.1f;
        return text.Length == count && text.All(IsCjk) && Score(candidate) >= 0.8f && improves &&
            candidate.WordResults is { Length: > 0 } words && words.Length == count;
    }

    private static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
    internal static float Score(TextBlock block) => block.CharScores is { Length: > 0 } scores ? scores.Average() : 0;

    internal static void MapCharacter(SKPointI[] points, SKRectI column, int count, int stripHeight)
    {
        var index = Math.Clamp((int)(points.Average(p => p.X) / column.Width), 0, count - 1);
        for (var i = 0; i < points.Length; i++)
            points[i] = new(
                column.Left + Math.Clamp(points[i].X - index * column.Width, 0, column.Width),
                Math.Clamp((int)Math.Round(column.Top + (index + points[i].Y / (double)stripHeight) *
                    column.Height / count), column.Top, column.Bottom));
    }
}
