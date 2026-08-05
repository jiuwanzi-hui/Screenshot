using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static partial class TranslationPresentationLayout
{
    public static IReadOnlyList<OcrTextRegion> TightenToWordBounds(
        IReadOnlyList<OcrTextRegion> regions,
        IReadOnlyList<OcrWordRegion> words)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return regions;
        }

        return regions.Select(region => TightenToWordBounds(region, words)).ToArray();
    }

    public static IReadOnlyList<OcrTextRegion> GroupParagraphs(
        IReadOnlyList<OcrTextRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var ordered = regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .Select(WithEstimatedFontSize)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
        if (ordered.Length <= 1)
        {
            return ordered;
        }

        var groups = new List<List<OcrTextRegion>>();
        foreach (var region in ordered)
        {
            if (groups.Count == 0 || !CanJoin(groups[^1], region))
            {
                groups.Add([region]);
            }
            else
            {
                groups[^1].Add(region);
            }
        }

        return groups.Select(MergeGroup).ToArray();
    }

    public static string NormalizeTranslatedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = CjkSpacingRegex().Replace(text.Trim(), string.Empty);
        normalized = SpacedAcronymRegex().Replace(
            normalized,
            match => match.Value.Replace(" ", string.Empty));
        return RepeatedHorizontalWhitespaceRegex().Replace(normalized, " ");
    }

    public static string NormalizeTranslatedText(
        string sourceText,
        string translatedText)
    {
        var normalized = NormalizeTranslatedText(translatedText);
        var source = sourceText?.TrimStart() ?? string.Empty;
        if (source.Length == 0 || normalized.Length == 0 ||
            !char.IsLetter(source[0]) || char.IsLetter(normalized[0]))
        {
            return normalized;
        }

        var firstLetterIndex = normalized.IndexOfAny(
            normalized.Where(char.IsLetter).Distinct().ToArray());
        if (firstLetterIndex is <= 0 or > 4)
        {
            return normalized;
        }

        var unexpectedPrefix = normalized[..firstLetterIndex];
        return unexpectedPrefix.Any(character =>
                !char.IsWhiteSpace(character) && !source.Contains(character))
            ? normalized[firstLetterIndex..].TrimStart()
            : normalized;
    }

    public static bool HasMeaningfulTranslation(
        string sourceText,
        string translatedText)
    {
        if (!HasTranslatableSourceText(sourceText))
        {
            return false;
        }

        var source = NormalizeForComparison(sourceText);
        var translated = NormalizeForComparison(translatedText);
        return translated.Length > 0 &&
               !string.Equals(source, translated, StringComparison.OrdinalIgnoreCase);
    }

    private static OcrTextRegion TightenToWordBounds(
        OcrTextRegion region,
        IReadOnlyList<OcrWordRegion> words)
    {
        var regionRight = region.X + region.Width;
        var regionBottom = region.Y + region.Height;
        var normalizedRegionText = NormalizeForWordMatch(region.Text);
        var matches = words
            .Where(word =>
            {
                var overlapTop = Math.Max(region.Y, word.Y);
                var overlapBottom = Math.Min(
                    regionBottom,
                    word.Y + word.Height);
                var verticalOverlap = Math.Max(0, overlapBottom - overlapTop);
                var minimumHeight = Math.Max(1, Math.Min(region.Height, word.Height));
                var wordCenterX = word.X + (word.Width / 2);
                var normalizedWordText = NormalizeForWordMatch(word.Text);
                return verticalOverlap >= minimumHeight * 0.5 &&
                       wordCenterX >= region.X - 2 &&
                       wordCenterX <= regionRight + 2 &&
                       normalizedWordText.Length > 0 &&
                       normalizedRegionText.Contains(
                           normalizedWordText,
                           StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(word => word.X)
            .ToArray();
        if (matches.Length == 0)
        {
            return region;
        }

        var contentWords = matches.ToList();
        var contentText = region.Text.Trim();
        while (contentWords.Count > 1 && IsLikelyLeadingIconWord(
                   contentWords[0],
                   contentWords[1],
                   contentWords.Skip(1)))
        {
            contentText = RemoveLeadingRecognizedWord(
                contentText,
                contentWords[0].Text);
            contentWords.RemoveAt(0);
        }

        var left = contentWords.Min(word => word.X);
        var top = contentWords.Min(word => word.Y);
        var right = contentWords.Max(word => word.X + word.Width);
        var bottom = contentWords.Max(word => word.Y + word.Height);
        return region with
        {
            Text = string.IsNullOrWhiteSpace(contentText)
                ? region.Text
                : contentText,
            X = left,
            Y = top,
            Width = Math.Max(1, right - left),
            Height = Math.Max(1, bottom - top),
        };
    }

    private static bool IsLikelyLeadingIconWord(
        OcrWordRegion first,
        OcrWordRegion second,
        IEnumerable<OcrWordRegion> remainingWords)
    {
        var firstText = first.Text.Trim();
        var firstLetters = firstText.Count(char.IsLetter);
        var remainingLetters = remainingWords.Sum(word =>
            word.Text.Count(char.IsLetter));
        var horizontalGap = second.X - (first.X + first.Width);
        var typicalHeight = Math.Max(first.Height, second.Height);
        var looksLikeSymbol = firstLetters == 0;
        var looksLikeSingleGlyphIcon = firstLetters == 1 &&
                                       firstText.Length <= 2 &&
                                       first.Width >= first.Height * 0.55;
        return remainingLetters >= 2 &&
               horizontalGap >= Math.Max(4, typicalHeight * 0.25) &&
               (looksLikeSymbol || looksLikeSingleGlyphIcon);
    }

    private static string RemoveLeadingRecognizedWord(
        string lineText,
        string wordText)
    {
        var line = lineText.TrimStart();
        var word = wordText.Trim();
        return word.Length > 0 && line.StartsWith(
                word,
                StringComparison.OrdinalIgnoreCase)
            ? line[word.Length..].TrimStart()
            : line;
    }

    private static bool HasTranslatableSourceText(string text)
    {
        var letters = (text ?? string.Empty)
            .Where(char.IsLetter)
            .ToArray();
        if (letters.Length <= 1)
        {
            return false;
        }

        return letters.Length > 3 ||
               letters.Any(letter => letter is not (>= 'A' and <= 'Z'));
    }

    private static bool CanJoin(
        IReadOnlyList<OcrTextRegion> currentGroup,
        OcrTextRegion next)
    {
        var first = currentGroup[0];
        var previous = currentGroup[^1];
        var previousBottom = previous.Y + previous.Height;
        var verticalGap = next.Y - previousBottom;
        var typicalHeight = Math.Max(previous.Height, next.Height);
        var heightRatio = Math.Max(previous.Height, next.Height) /
                          Math.Max(1, Math.Min(previous.Height, next.Height));
        var leftAligned = Math.Abs(next.X - first.X) <=
                          Math.Max(14, typicalHeight * 1.35);
        var isFollowingLine = next.Y >= previous.Y + (previous.Height * 0.5);
        return isFollowingLine &&
               verticalGap <= typicalHeight * 0.9 &&
               leftAligned &&
               heightRatio <= 1.35;
    }

    private static OcrTextRegion MergeGroup(IReadOnlyList<OcrTextRegion> group)
    {
        if (group.Count == 1)
        {
            return group[0];
        }

        var left = group.Min(region => region.X);
        var top = group.Min(region => region.Y);
        var right = group.Max(region => region.X + region.Width);
        var bottom = group.Max(region => region.Y + region.Height);
        return new OcrTextRegion(
            string.Join(" ", group.Select(region => region.Text.Trim())),
            left,
            top,
            right - left,
            bottom - top)
        {
            EstimatedFontSize = Median(group.Select(region =>
                region.EstimatedFontSize)),
        };
    }

    private static OcrTextRegion WithEstimatedFontSize(OcrTextRegion region)
    {
        return region.EstimatedFontSize > 0
            ? region
            : region with
            {
                EstimatedFontSize = Math.Clamp(region.Height / 1.12, 8, 64),
            };
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 16;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static string NormalizeForComparison(string text)
    {
        return NormalizeTranslatedText(text ?? string.Empty);
    }

    private static string NormalizeForWordMatch(string text)
    {
        return new string((text ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .ToArray());
    }

    [GeneratedRegex(@"(?<=[\p{IsCJKUnifiedIdeographs}])\s+(?=[\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex CjkSpacingRegex();

    [GeneratedRegex(@"\b(?:[A-Z]\s+){1,}[A-Z]\b")]
    private static partial Regex SpacedAcronymRegex();

    [GeneratedRegex(@"[\t ]{2,}")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();
}
