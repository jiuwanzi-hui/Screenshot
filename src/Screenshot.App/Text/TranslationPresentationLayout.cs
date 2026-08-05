using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static partial class TranslationPresentationLayout
{
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

    [GeneratedRegex(@"(?<=[\p{IsCJKUnifiedIdeographs}])\s+(?=[\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex CjkSpacingRegex();

    [GeneratedRegex(@"\b(?:[A-Z]\s+){1,}[A-Z]\b")]
    private static partial Regex SpacedAcronymRegex();

    [GeneratedRegex(@"[\t ]{2,}")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();
}
