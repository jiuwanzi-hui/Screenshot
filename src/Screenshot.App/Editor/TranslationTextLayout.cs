using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using System.Text;
using Screenshot.App.Text;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfRect = System.Windows.Rect;

namespace Screenshot.App.Editor;

internal static class TranslationTextLayout
{
    internal const double MinimumFontSize = 8;
    internal const double LineSpacingFactor = 1.24;
    private const double MaximumFontSize = 32;

    public static double FitFontSize(
        string text,
        double availableWidth,
        double availableHeight,
        double preferredFontSize)
    {
        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);
        var maximumForOneLine = availableHeight / LineSpacingFactor;
        var maximumAllowed = Math.Max(
            MinimumFontSize,
            Math.Min(MaximumFontSize, maximumForOneLine));
        var candidate = Math.Clamp(
            preferredFontSize,
            MinimumFontSize,
            maximumAllowed);
        if (MeasureWrappedHeight(text, availableWidth, candidate) <= availableHeight + 0.5)
        {
            return candidate;
        }

        var lowestStep = (int)Math.Ceiling(MinimumFontSize * 2);
        var highestStep = (int)Math.Floor(candidate * 2);
        var bestStep = lowestStep;
        while (lowestStep <= highestStep)
        {
            var middleStep = lowestStep + ((highestStep - lowestStep) / 2);
            var fontSize = middleStep / 2d;
            if (MeasureWrappedHeight(text, availableWidth, fontSize) <= availableHeight + 0.5)
            {
                bestStep = middleStep;
                lowestStep = middleStep + 1;
            }
            else
            {
                highestStep = middleStep - 1;
            }
        }

        return bestStep / 2d;
    }

    public static double FitSingleLineFontSize(
        string text,
        double availableWidth,
        double availableHeight,
        double preferredFontSize)
    {
        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);
        var upper = Math.Clamp(
            preferredFontSize,
            MinimumFontSize,
            Math.Min(MaximumFontSize, availableHeight / LineSpacingFactor));
        var low = (int)Math.Ceiling(MinimumFontSize * 2);
        var high = (int)Math.Floor(upper * 2);
        var best = low;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var size = middle / 2d;
            if (MeasureWidth(text, size) <= availableWidth + 0.5)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best / 2d;
    }

    public static TranslationParagraphLayout LayoutParagraph(
        TranslatedTextAnnotationRegion region)
    {
        var contentBounds = new WpfRect(
            region.Bounds.X + 4,
            region.Bounds.Y + 3,
            Math.Max(8, region.Bounds.Width - 8),
            Math.Max(8, region.Bounds.Height - 6));
        var fontSize = FitFontSize(
            region.Text,
            contentBounds.Width,
            contentBounds.Height,
            region.FontSize);
        var lines = WrapText(region.Text, contentBounds.Width, fontSize);
        var lineHeight = fontSize * LineSpacingFactor;
        var lineRegions = lines
            .Select((line, index) => new OcrTextRegion(
                line,
                contentBounds.X,
                contentBounds.Y + (index * lineHeight),
                Math.Min(
                    contentBounds.Width,
                    Math.Max(2, MeasureWidth(line, fontSize))),
                lineHeight)
            {
                EstimatedFontSize = fontSize,
            })
            .ToArray();
        return new TranslationParagraphLayout(fontSize, lineHeight, lineRegions);
    }

    private static List<string> WrapText(
        string text,
        double maximumWidth,
        double fontSize)
    {
        var normalized = text
            .Replace("\r", string.Empty)
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var character in normalized)
        {
            current.Append(character);
            if (current.Length <= 1 ||
                MeasureWidth(current.ToString(), fontSize) <= maximumWidth)
            {
                continue;
            }

            var overflowText = current.ToString();
            var breakIndex = overflowText.LastIndexOf(' ', overflowText.Length - 2);
            if (breakIndex <= 0)
            {
                breakIndex = overflowText.Length - 1;
            }

            var completedLine = overflowText[..breakIndex].TrimEnd();
            if (completedLine.Length > 0)
            {
                lines.Add(completedLine);
            }

            current.Clear();
            current.Append(overflowText[breakIndex..].TrimStart());
        }

        var finalLine = current.ToString().TrimEnd();
        if (finalLine.Length > 0)
        {
            lines.Add(finalLine);
        }

        return lines;
    }

    private static double MeasureWidth(string text, double fontSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(
                new WpfFontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            fontSize,
            System.Windows.Media.Brushes.Black,
            pixelsPerDip: 1);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static double MeasureWrappedHeight(
        string text,
        double availableWidth,
        double fontSize)
    {
        // Use the same character wrapping routine as LayoutParagraph. A
        // WPF TextBlock can make a different break decision for CJK/Latin
        // mixtures, which used to produce an extra visual line and overlap.
        return WrapText(text, availableWidth, fontSize).Count *
            fontSize * LineSpacingFactor;
    }
}

internal sealed record TranslationParagraphLayout(
    double FontSize,
    double LineHeight,
    IReadOnlyList<OcrTextRegion> Lines);
