using System.Text;
using System.Windows;
using Point = System.Windows.Point;

namespace Screenshot.App.Text;

public static partial class PrivacyDetectionService
{
    private static SourceText CreateWordSource(OcrWordRegion word)
    {
        var bounds = new Rect(word.X, word.Y, word.Width, word.Height);
        return new SourceText(word.Text, bounds, [new SourceSpan(0, word.Text, bounds)]);
    }

    private static SourceText CreateRegionSource(OcrTextRegion region, IReadOnlyList<OcrWordRegion> words)
    {
        var bounds = new Rect(region.X, region.Y, region.Width, region.Height);
        var spans = new List<SourceSpan>();
        var offset = 0;
        var regionWords = words.Where(word => bounds.Contains(
            new Point(word.X + word.Width / 2, word.Y + word.Height / 2))).ToArray();
        foreach (var word in GroupWordLines(regionWords).SelectMany(line => line.OrderBy(word => word.X)))
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            var index = region.Text.IndexOf(word.Text, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            spans.Add(new SourceSpan(index, word.Text, new Rect(word.X, word.Y, word.Width, word.Height)));
            offset = index + word.Text.Length;
        }
        return new SourceText(region.Text, bounds, spans.ToArray());
    }

    private static string TrimValueAtColumnGap(SourceText source, int start, string value)
    {
        SourceSpan? previous = null;
        foreach (var span in source.Spans ?? [])
        {
            if (span.Start >= start + value.Length) break;
            if (span.Start + span.Text.Length <= start) continue;
            if (previous is not null && span.Bounds.Left - previous.Bounds.Right >
                Math.Max(48, Math.Max(span.Bounds.Height, previous.Bounds.Height) * 2))
                return value[..(span.Start - start)].TrimEnd();
            previous = span;
        }
        return value;
    }

    private static Rect EstimateValueBounds(SourceText source, int start, int length, out bool hasWordBounds)
    {
        hasWordBounds = false;
        var end = Math.Min(source.Text.Length, start + length);
        var bounds = Rect.Empty;
        var coveredCharacters = 0;
        foreach (var span in source.Spans ?? [])
        {
            var left = Math.Max(start, span.Start);
            var right = Math.Min(end, span.Start + span.Text.Length);
            if (right <= left) continue;
            bounds.Union(EstimateTextBounds(span.Text, span.Bounds, left - span.Start, right - left));
            for (var index = left; index < right; index++)
                if (!char.IsWhiteSpace(source.Text[index])) coveredCharacters++;
        }
        var requiredCharacters = 0;
        for (var index = start; index < end; index++)
            if (!char.IsWhiteSpace(source.Text[index])) requiredCharacters++;
        if (!bounds.IsEmpty && coveredCharacters == requiredCharacters)
        {
            hasWordBounds = true;
            return bounds;
        }
        return EstimateTextBounds(source.Text, source.Bounds, start, length);
    }

    private static Rect EstimateTextBounds(string text, Rect bounds, int start, int length)
    {
        if (text.Length == 0 || length <= 0) return bounds;
        double before = 0, value = 0, total = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var width = character >= '\u2E80' ? 2d : char.IsWhiteSpace(character) ? .5 : 1;
            total += width;
            if (index < start) before += width;
            else if (index < start + length) value += width;
        }
        var left = bounds.Left + bounds.Width * before / total;
        var right = Math.Min(bounds.Right, left + bounds.Width * value / total);
        return new Rect(left, bounds.Top, Math.Max(0, right - left), bounds.Height);
    }

    private static List<List<OcrWordRegion>> GroupWordLines(IReadOnlyList<OcrWordRegion> words)
    {
        var lines = new List<List<OcrWordRegion>>();
        foreach (var word in words.Where(word => !string.IsNullOrWhiteSpace(word.Text))
                     .OrderBy(word => word.Y).ThenBy(word => word.X))
        {
            var centerY = word.Y + word.Height / 2;
            var line = lines.FirstOrDefault(candidate =>
            {
                var top = candidate.Min(item => item.Y);
                var bottom = candidate.Max(item => item.Y + item.Height);
                return Math.Abs(centerY - (top + bottom) / 2) <=
                    Math.Max(4, Math.Min(word.Height, bottom - top) * .65);
            });
            if (line is null) { line = []; lines.Add(line); }
            line.Add(word);
        }
        return lines;
    }

    private static IEnumerable<SourceText> BuildWordLineSources(IReadOnlyList<OcrWordRegion> words)
    {
        foreach (var line in GroupWordLines(words).Where(line => line.Count > 1))
        {
            var text = new StringBuilder();
            var spans = new List<SourceSpan>();
            var bounds = Rect.Empty;
            double previousRight = double.NaN;
            foreach (var word in line.OrderBy(word => word.X))
            {
                // Wide whitespace often separates unrelated columns. Do not
                // turn their text into one continuous account/address value.
                if (word.X - previousRight > Math.Max(48, word.Height * 2))
                {
                    if (spans.Count > 1) yield return new SourceText(text.ToString(), bounds, spans.ToArray());
                    text.Clear(); spans.Clear(); bounds = Rect.Empty;
                }
                var wordBounds = new Rect(word.X, word.Y, word.Width, word.Height);
                spans.Add(new SourceSpan(text.Length, word.Text, wordBounds));
                text.Append(word.Text);
                bounds.Union(wordBounds);
                previousRight = wordBounds.Right;
            }
            if (spans.Count > 1) yield return new SourceText(text.ToString(), bounds, spans.ToArray());
        }
    }
}
