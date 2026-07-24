using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfSize = System.Windows.Size;

namespace Screenshot.App.Editor;

internal static class TranslationTextLayout
{
    internal const double MinimumFontSize = 8;
    private const double MaximumFontSize = 32;
    private const double FontSizeStep = 0.5;

    public static double FitFontSize(
        string text,
        double availableWidth,
        double availableHeight,
        double preferredFontSize)
    {
        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);
        var maximumForOneLine = availableHeight / 1.12;
        var maximumAllowed = Math.Max(
            MinimumFontSize,
            Math.Min(MaximumFontSize, maximumForOneLine));
        var candidate = Math.Clamp(
            preferredFontSize,
            MinimumFontSize,
            maximumAllowed);

        while (candidate > MinimumFontSize)
        {
            if (MeasureHeight(text, availableWidth, candidate) <=
                availableHeight + 0.5)
            {
                return candidate;
            }

            candidate = Math.Max(MinimumFontSize, candidate - FontSizeStep);
        }

        return MinimumFontSize;
    }

    private static double MeasureHeight(
        string text,
        double availableWidth,
        double fontSize)
    {
        var measurement = new TextBlock
        {
            Text = text,
            FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Width = availableWidth,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.12,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        measurement.Measure(new WpfSize(availableWidth, double.PositiveInfinity));
        return measurement.DesiredSize.Height;
    }
}
