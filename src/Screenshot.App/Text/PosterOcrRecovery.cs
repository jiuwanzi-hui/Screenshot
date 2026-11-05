using RapidOcrNet;
using SkiaSharp;

namespace Screenshot.App.Text;

// Only runs inside the cancellable, short-lived OCR worker. All images below
// are disposable recognition hypotheses; the captured pixels remain untouched.
internal static class PosterOcrRecovery
{
    private const int Cell = 64;

    internal static OcrRecognitionResult JoinColumns(OcrRecognitionResult result)
    {
        var columns = new List<OcrTextRegion>();
        foreach (var region in result.Regions.OrderBy(region => region.Y))
        {
            var above = region.RecoveredVerticalColumn ? columns.LastOrDefault(other => other.RecoveredVerticalColumn &&
                Math.Min(other.X + other.Width, region.X + region.Width) - Math.Max(other.X, region.X) >=
                    Math.Max(other.Width, region.Width) * .7 &&
                region.Y - (other.Y + other.Height) >= -Math.Min(other.Width, region.Width) * .5 &&
                region.Y - (other.Y + other.Height) <= Math.Min(other.Width, region.Width)) : null;
            if (above is null) columns.Add(region);
            else
            {
                columns.Remove(above);
                var left = Math.Min(above.X, region.X);
                columns.Add(above with { Text = above.Text + region.Text, X = left,
                    Width = Math.Max(above.X + above.Width, region.X + region.Width) - left,
                    Height = region.Y + region.Height - above.Y });
            }
        }
        // Separately detected display glyphs on the same baseline form one
        // heading. This does not alter word boxes used for text selection.
        foreach (var region in columns.Where(region => region.VerifiedArtisticText && region.Text.Length == 1).ToArray())
        {
            if (!columns.Contains(region)) continue;
            var right = columns.Where(other => other.VerifiedArtisticText && other.Text.Length == 1 && other.X > region.X &&
                other.X - (region.X + region.Width) < Math.Max(region.Height, other.Height) &&
                Math.Min(region.Y + region.Height, other.Y + other.Height) - Math.Max(region.Y, other.Y) >
                    Math.Min(region.Height, other.Height) * .65).OrderBy(other => other.X).FirstOrDefault();
            if (right is null) continue;
            columns.Remove(region); columns.Remove(right);
            var top = Math.Min(region.Y, right.Y);
            columns.Add(region with { Text = region.Text + right.Text, Y = top,
                Width = right.X + right.Width - region.X, Height = Math.Max(region.Y + region.Height, right.Y + right.Height) - top });
        }
        var sorted = OcrReadingOrder.Sort(columns);
        return result with { Regions = sorted, Text = string.Join(Environment.NewLine, sorted.Select(region => region.Text)) };
    }

    internal static bool NeedsRecovery(int width, int height, IReadOnlyList<TextBlock> blocks) =>
        width >= 160 && height >= 160 && (long)width * height <= 4_000_000 &&
        blocks.Any(block => TryColumn(block, width, height, out _) &&
            (VerticalOcrRecovery.Score(block) < .9f || block.Text.Trim().Length < 3));

    internal static bool NeedsRecovery(SKBitmap source, IReadOnlyList<TextBlock> blocks)
    {
        if (NeedsRecovery(source.Width, source.Height, blocks)) return true;
        if (source.Width < 160 || source.Height < 160 || (long)source.Width * source.Height > 4_000_000 || blocks.Count > 4)
            return false;
        var bounds = blocks.Select(block => Bounds(block.BoxPoints, source.Width, source.Height)).ToArray();
        if (bounds.Sum(box => (double)box.Width * box.Height) >= source.Width * (double)source.Height * .12) return false;
        // Sparse OCR alone also describes an ordinary page with a short black
        // caption. Only inspect colored display-letter candidates outside the
        // known text. This admits partial crops without loading Medium for every
        // sparse document. Original-color agreement still governs acceptance.
        var pixels = source.Pixels;
        var required = Math.Max(24, source.Width * source.Height / 16 / 50);
        var colored = 0;
        for (var y = 0; y < source.Height; y += 4)
        for (var x = 0; x < source.Width; x += 4)
        {
            if (bounds.Any(box => box.Contains(x, y))) continue;
            var p = pixels[y * source.Width + x];
            var high = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
            var low = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            if (high - low >= 80 && low < 160 && ++colored >= required) return true;
        }
        return false;
    }

    internal static OcrRecognitionResult Recover(RapidOcr engine, SKBitmap source, TextBlock[] raw, CancellationToken token)
    {
        var regions = new List<OcrTextRegion>();
        var words = new List<OcrWordRegion>();
        var options = RapidOcrOptions.PPOCRv6 with
        {
            LimitSideLen = 0, MaxSideLen = 1600, DoAngle = false,
            ReturnWordBox = true, ReturnSingleCharBox = true, TextScore = .45f,
        };
        RecoverColoredHeadings(engine, source, raw, options, regions, words, token);
        RecoverColoredHeadings(engine, source, raw, options, regions, words, token, 100);
        var remaining = 120;
        foreach (var block in raw.Where(block => TryColumn(block, source.Width, source.Height, out var bounds) &&
                     !IsReadableColumn(block, bounds)).Take(12))
        {
            token.ThrowIfCancellationRequested();
            TryColumn(block, source.Width, source.Height, out var column);
            var recovered = RecoverColumn(engine, source, column, options, ref remaining, token);
            if (recovered is null) continue;
            regions.Add(new(string.Concat(recovered.Select(word => word.Text)), column.Left, column.Top, column.Width, column.Height)
            {
                RecognitionConfidence = recovered.Average(word => word.Score), RecoveredVerticalColumn = true,
                EstimatedFontSize = Math.Clamp(column.Width / 1.12, 8, 64),
            });
            words.AddRange(recovered.Select(ToWord));
        }
        // A resized display image can make Small join two columns or omit one.
        // Ask Medium once for alternate geometry, skipping already covered
        // columns. Recovered text still comes from the original-color pixels.
        if (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var alternate = engine.Detect(source, options with
            { ImgResize = Math.Min(1600, Math.Max(source.Width, source.Height)), TextScore = 0 }).TextBlocks;
            foreach (var block in alternate.Where(block => TryColumn(block, source.Width, source.Height, out _)).Take(12))
            {
                TryColumn(block, source.Width, source.Height, out var column);
                // Correct v6 crop orientation often already reads the column in
                // the first pass. Don't enumerate tile counts for accepted text.
                if (raw.Any(prior => TryColumn(prior, source.Width, source.Height, out var known) &&
                    IsReadableColumn(prior, known) &&
                    Math.Abs(known.MidX - column.MidX) < column.Width * .45 &&
                    known.Top <= column.Top + column.Width && known.Bottom >= column.Bottom - column.Width)) continue;
                if (regions.Any(region => region.VerifiedArtisticText &&
                    region.X < column.Right && region.X + region.Width > column.Left &&
                    region.Y < column.Bottom && region.Y + region.Height > column.Top)) continue;
                if (regions.Any(region => Math.Abs(region.X + region.Width / 2 - column.MidX) < column.Width * .45 &&
                    region.Y <= column.Top + column.Width && region.Y + region.Height >= column.Bottom - column.Width)) continue;
                var recovered = RecoverColumn(engine, source, column, options, ref remaining, token);
                if (recovered is null) continue;
                regions.Add(new(string.Concat(recovered.Select(word => word.Text)), column.Left, column.Top, column.Width, column.Height)
                { RecognitionConfidence = recovered.Average(word => word.Score), RecoveredVerticalColumn = true,
                    EstimatedFontSize = Math.Clamp(column.Width / 1.12, 8, 64) });
                words.AddRange(recovered.Select(ToWord));
            }
        }
        var sorted = OcrReadingOrder.Sort(regions);
        return new(true, string.Join(Environment.NewLine, sorted.Select(region => region.Text)), null)
        { Regions = sorted, Words = words };
    }

    private static bool TryColumn(TextBlock block, int width, int height, out SKRectI bounds)
    {
        bounds = Bounds(block.BoxPoints, width, height);
        return block.BoxPoints.Length == 4 && bounds.Width >= 12 && bounds.Height >= bounds.Width * 2.5 &&
            bounds.Height <= bounds.Width * 20 && bounds.Width <= 240 &&
            Math.Abs(block.BoxPoints[0].X - block.BoxPoints[3].X) <= bounds.Width * .15 &&
            Math.Abs(block.BoxPoints[1].X - block.BoxPoints[2].X) <= bounds.Width * .15;
    }

    internal static bool IsReadableColumn(TextBlock block, SKRectI bounds)
    {
        if (bounds.Width <= 0) return false;
        var count = block.Text.Count(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character));
        var ratio = bounds.Height / (double)bounds.Width;
        var score = VerticalOcrRecovery.Score(block);
        // Match the ordinary vertical-recovery confidence/count gate. A full
        // plausible column doesn't become unreadable merely by entering Medium.
        return score >= .9f && count >= Math.Max(2, (int)(ratio * .55)) ||
            score >= .8f && count >= Math.Max(2, (int)Math.Round(ratio)) &&
            block.CharScores is { Length: > 0 } scores && scores.All(value => value >= .65f);
    }

    private static WordBox[]? RecoverColumn(RapidOcr engine, SKBitmap source, SKRectI column,
        RapidOcrOptions options, ref int remaining, CancellationToken token)
    {
        var first = RecoverColumnLayout(engine, source, column, options, ref remaining, token);
        if (first is not null) return first;
        // DBNet expands its box beyond the glyphs. On small/rescaled columns
        // this padding shifts every proposed cut through a stroke. One inset
        // hypothesis removes only detector context, with unchanged acceptance.
        var inset = (int)Math.Round(column.Width * .15);
        var inner = new SKRectI(column.Left, column.Top + inset, column.Right, column.Bottom - inset);
        return RecoverColumnLayout(engine, source, inner, options, ref remaining, token);
    }

    private static WordBox[]? RecoverColumnLayout(RapidOcr engine, SKBitmap source, SKRectI column,
        RapidOcrOptions options, ref int remaining, CancellationToken token)
    {
        WordBox[]? best = null;
        var bestScore = .94f;
        var ratio = column.Height / (double)column.Width;
        var min = Math.Max(2, (int)(ratio / 1.4));
        var max = Math.Min(24, (int)(ratio / .6));
        // Detector padding changes the width/count ratio. Test a finite set
        // of layouts and require complete, confident text, including punctuation.
        foreach (var count in Enumerable.Range(min, max - min + 1).OrderBy(count => Math.Abs(count - ratio * 1.3)))
        {
            if (remaining <= 0) break;
            token.ThrowIfCancellationRequested();
            remaining--;
            var cuts = FindCuts(source, column, count);
            using var strip = new SKBitmap(count * Cell, Cell);
            using (var canvas = new SKCanvas(strip))
                for (var index = 0; index < count; index++)
                    canvas.DrawBitmap(source, new SKRect(column.Left, cuts[index], column.Right, cuts[index + 1]),
                        new SKRect(index * Cell, 0, (index + 1) * Cell, Cell));
            var blocks = engine.Detect(strip, options with { ImgResize = strip.Width }).TextBlocks;
            token.ThrowIfCancellationRequested();
            var candidates = blocks.SelectMany(block => block.WordResults ?? [])
                .OrderBy(word => word.BoxPoints.Average(point => point.X)).ToArray();
            if (candidates.Length > 0 && candidates[^1].Text.Length == 1 &&
                char.IsPunctuation(candidates[^1].Text[0]) && candidates[^1].Score < .85f)
                candidates = candidates[..^1];
            var complete = IsCompleteLayout(candidates, count);
            // A comma can occupy its own final tile and be below threshold.
            // Keep the preceding glyphs if the tail is sparse or every retained
            // glyph is highly confident. Never invent the missing punctuation.
            if (!complete && !(IsCompleteLayout(candidates, count - 1) &&
                (IsSparseTail(source, column, cuts[^2], cuts[^1]) ||
                 candidates.Average(word => word.Score) >= .985f && candidates.All(word => word.Score >= .95f)))) continue;
            var score = candidates.Average(word => word.Score);
            if (score <= bestScore && !(best is not null && candidates.Length > best.Length && score >= bestScore - .01f)) continue;
            bestScore = score;
            foreach (var word in candidates) MapWord(word, column, cuts);
            best = candidates;
        }
        return best;
    }

    private static bool IsSparseTail(SKBitmap source, SKRectI column, int top, int bottom)
    {
        var activeRows = 0;
        for (var y = top; y < bottom; y++)
        {
            var edges = 0;
            for (var x = column.Left + 2; x < column.Right - 2; x++)
            {
                var a = source.GetPixel(x, y); var b = source.GetPixel(x + 1, y);
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 100) edges++;
            }
            if (edges >= 3) activeRows++;
        }
        return activeRows < (bottom - top) * .5;
    }

    internal static bool IsCompleteLayout(WordBox[] words, int count) =>
        words.Length == count && words.All(word => word.Text.Length == 1 &&
            (word.Text[0] is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' || char.IsPunctuation(word.Text[0])) &&
            word.Score >= .65f && word.BoxPoints.Length == 4 &&
            word.BoxPoints.Average(point => point.X) >= 0 && word.BoxPoints.Average(point => point.X) < count * Cell) &&
        words.Select(word => (int)(word.BoxPoints.Average(point => point.X) / Cell))
            .Distinct().Count() == count;

    private static int[] FindCuts(SKBitmap source, SKRectI column, int count)
    {
        var cuts = Enumerable.Range(0, count + 1).Select(index => column.Top + index * column.Height / count).ToArray();
        var radius = column.Height / count / 4;
        for (var index = 1; index < count; index++)
        {
            var expected = cuts[index];
            var best = double.MaxValue;
            for (var y = Math.Max(column.Top + 1, expected - radius); y <= Math.Min(column.Bottom - 1, expected + radius); y++)
            {
                double edge = Math.Abs(y - expected) * .5;
                for (var x = column.Left + 2; x < column.Right - 2; x++)
                {
                    var a = source.GetPixel(x, y); var b = source.GetPixel(x + 1, y);
                    edge += Math.Max(0, Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) - 60);
                }
                if (edge < best) { best = edge; cuts[index] = y; }
            }
        }
        return cuts;
    }

    internal static void MapWord(WordBox word, SKRectI column, int[] cuts)
    {
        var index = Math.Clamp((int)(word.BoxPoints.Average(point => point.X) / Cell), 0, cuts.Length - 2);
        for (var i = 0; i < word.BoxPoints.Length; i++)
            word.BoxPoints[i] = new(
                column.Left + (int)Math.Round(Math.Clamp(word.BoxPoints[i].X - index * Cell, 0, Cell) * column.Width / (double)Cell),
                cuts[index] + (int)Math.Round(Math.Clamp(word.BoxPoints[i].Y, 0, Cell) * (cuts[index + 1] - cuts[index]) / (double)Cell));
    }

    private static void RecoverColoredHeadings(RapidOcr engine, SKBitmap source, TextBlock[] raw, RapidOcrOptions options,
        List<OcrTextRegion> regions, List<OcrWordRegion> words, CancellationToken token, int chromaThreshold = 40)
    {
        token.ThrowIfCancellationRequested();
        using var mask = new SKBitmap(source.Width, source.Height);
        var pixels = source.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (i % source.Width == 0) token.ThrowIfCancellationRequested();
            var pixel = pixels[i];
            var maximum = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
            var minimum = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
            pixels[i] = maximum - minimum > chromaThreshold && minimum < 160 ? SKColors.Black : SKColors.White;
        }
        mask.Pixels = pixels;
        var proposals = engine.Detect(mask, options with { ImgResize = Math.Min(1600, Math.Max(source.Width, source.Height)) }).TextBlocks;
        foreach (var proposal in proposals.Where(block => VerticalOcrRecovery.Score(block) >= .93f).Take(16))
        {
            token.ThrowIfCancellationRequested();
            var box = Bounds(proposal.BoxPoints, source.Width, source.Height);
            if (box.Height < 48 || box.Height > source.Height * .3 || box.Width < box.Height * .7 ||
                proposal.Text.Length is < 1 or > 12 ||
                proposal.Text.Length == 1 && (VerticalOcrRecovery.Score(proposal) < .99f ||
                    proposal.Text[0] is not (>= '\u3400' and <= '\u9fff')) ||
                regions.Any(region => region.VerifiedArtisticText && region.Text.Contains(proposal.Text, StringComparison.Ordinal) &&
                    box.MidX >= region.X && box.MidX <= region.X + region.Width &&
                    box.MidY >= region.Y && box.MidY <= region.Y + region.Height) ||
                raw.Any(block => VerticalOcrRecovery.Score(block) > .9f && block.Text == proposal.Text)) continue;
            // Chroma can erase strokes or turn scenery into apparent text.
            // Accept it only when the original-color crop independently agrees.
            using var crop = new SKBitmap(box.Width, box.Height);
            using (var canvas = new SKCanvas(crop)) canvas.DrawBitmap(source, -box.Left, -box.Top);
            var check = engine.Detect(crop, options with { ImgResize = Math.Max(crop.Width, crop.Height) }).TextBlocks;
            var confirmed = check.FirstOrDefault(block => block.Text == proposal.Text && VerticalOcrRecovery.Score(block) >= .93f);
            if (confirmed is null || confirmed.WordResults is not { Length: > 0 }) continue;
            var confirmedBounds = Bounds(confirmed.BoxPoints, crop.Width, crop.Height);
            regions.Add(new(proposal.Text, box.Left + confirmedBounds.Left, box.Top + confirmedBounds.Top,
                confirmedBounds.Width, confirmedBounds.Height)
            {
                RecognitionConfidence = Math.Min(VerticalOcrRecovery.Score(proposal), VerticalOcrRecovery.Score(confirmed)),
                VerifiedArtisticText = true, EstimatedFontSize = Math.Clamp(box.Height / 1.12, 8, 64),
            });
            words.AddRange(confirmed.WordResults.Select(word =>
            {
                var mapped = ToWord(word);
                return mapped with { X = mapped.X + box.Left, Y = mapped.Y + box.Top };
            }));
        }
    }

    private static SKRectI Bounds(SKPointI[] points, int width, int height) => points.Length == 0 ? default :
        new(Math.Clamp(points.Min(point => point.X), 0, width), Math.Clamp(points.Min(point => point.Y), 0, height),
            Math.Clamp(points.Max(point => point.X), 0, width), Math.Clamp(points.Max(point => point.Y), 0, height));

    private static OcrWordRegion ToWord(WordBox word)
    {
        var x = word.BoxPoints.Min(point => point.X); var y = word.BoxPoints.Min(point => point.Y);
        return new(word.Text, x, y, Math.Max(1, word.BoxPoints.Max(point => point.X) - x),
            Math.Max(1, word.BoxPoints.Max(point => point.Y) - y));
    }
}
