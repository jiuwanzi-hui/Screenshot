namespace Screenshot.App.Text;

internal static class OcrColumnLayout
{
    // A detector can enclose two narrow newspaper columns in one line box.
    // Require a repeated gutter on another line before splitting its text;
    // a single word space or a lone full-width space is not a column boundary.
    internal static IReadOnlyList<OcrTextRegion> SplitJoinedColumns(
        IReadOnlyList<OcrTextRegion> regions, IReadOnlyList<OcrWordRegion> words)
    {
        var lines = regions.Select(region => new Line(region, words.Where(word =>
            word.X + word.Width / 2 >= region.X &&
            word.X + word.Width / 2 <= region.X + region.Width &&
            Math.Min(word.Y + word.Height, region.Y + region.Height) - Math.Max(word.Y, region.Y) >=
                Math.Min(word.Height, region.Height) * 0.7).OrderBy(word => word.X).ToArray())).ToArray();
        var gaps = new List<Gap>();
        foreach (var line in lines)
        {
            if (!HasCjk(line.Region.Text)) continue;
            for (var index = 1; index < line.Words.Length; index++)
            {
                var left = line.Words[index - 1];
                var right = line.Words[index];
                var start = left.X + left.Width;
                var width = right.X - start;
                if (width >= Math.Max(8, Math.Min(left.Height, right.Height) * 0.6) &&
                    index >= 3 && line.Words.Length - index >= 3)
                    gaps.Add(new Gap(line, index, start + width / 2));
            }
        }
        if (gaps.Count == 0) return regions;

        var result = new List<OcrTextRegion>();
        foreach (var line in lines)
        {
            var cuts = gaps.Where(gap => ReferenceEquals(gap.Line, line) && gaps.Any(other =>
                !ReferenceEquals(other.Line, line) &&
                Math.Abs(other.Center - gap.Center) <= Math.Max(3, line.Region.Height * 0.25) &&
                Math.Abs(other.Line.Region.Y - line.Region.Y) >= line.Region.Height * 0.7 &&
                Math.Abs(other.Line.Region.Y - line.Region.Y) <= line.Region.Height * 3))
                .Select(gap => gap.WordIndex).Distinct().Order().ToArray();
            if (cuts.Length == 0 || !TryMapWords(line, out var offsets))
            {
                result.Add(line.Region);
                continue;
            }
            var startWord = 0;
            var startText = 0;
            foreach (var endWord in cuts.Append(line.Words.Length))
            {
                var endText = endWord == line.Words.Length ? line.Region.Text.Length : offsets[endWord];
                var part = line.Words[startWord..endWord];
                var left = part.Min(word => word.X);
                var top = part.Min(word => word.Y);
                result.Add(new OcrTextRegion(line.Region.Text[startText..endText].Trim(), left, top,
                    part.Max(word => word.X + word.Width) - left,
                    part.Max(word => word.Y + word.Height) - top)
                    { EstimatedFontSize = line.Region.EstimatedFontSize });
                startWord = endWord;
                startText = endText;
            }
        }
        return result;
    }

    private static bool TryMapWords(Line line, out int[] offsets)
    {
        offsets = new int[line.Words.Length];
        var position = 0;
        for (var index = 0; index < line.Words.Length; index++)
        {
            var text = line.Words[index].Text.Trim();
            var found = line.Region.Text.IndexOf(text, position, StringComparison.Ordinal);
            if (text.Length == 0 || found < 0) return false;
            offsets[index] = found;
            position = found + text.Length;
        }
        return true;
    }

    private static bool HasCjk(string text) => text.Any(character =>
        character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');

    private sealed record Line(OcrTextRegion Region, OcrWordRegion[] Words);
    private sealed record Gap(Line Line, int WordIndex, double Center);
}
