using Screenshot.App.Text;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App.Tests;

public sealed class RealtimeParagraphTests
{
    [Theory]
    [InlineData(12, 70, "of developers", true)]
    [InlineData(220, 70, "of developers", false)]
    [InlineData(12, 180, "of developers", false)]
    [InlineData(12, 70, "Developer survey", false)]
    public void PercentageCalloutsKeepTheirAdjacentContinuationAndDoNotCrossColumns(double x, double y, string text, bool merged)
    {
        var result = RealtimeTranslationSession.GroupLines([new("75%", 10, 10, 55, 30), new(text, x, y, 180, 20)]);
        Assert.Equal(merged ? 1 : 2, result.Count);
        if (merged) Assert.Equal("75% of developers", result[0].Text);
    }

    [Fact]
    public void BrowserLanguageIconDoesNotReclassifyChineseNavigationAsJapanese()
    {
        var pending = TranslationTargetLanguageMatcher.GetPendingSegmentIndices(["aあ", "已经是中文", "Communities that last"], "auto", "zh-Hans");
        Assert.DoesNotContain(1, pending);
        Assert.Contains(2, pending);
    }
    [Fact]
    public void NeighboringColumnsDoNotSplitTheirOwnWrappedHeadings()
    {
        var paragraphs = RealtimeTranslationSession.GroupLines(
        [new("Communities that", 10, 10, 140, 24), new("Learning for the", 210, 10, 140, 24),
         new("Growth built on", 410, 10, 140, 24), new("last", 10, 39, 40, 24),
         new("future", 210, 39, 60, 24), new("knowledge", 410, 39, 90, 24)]);
        Assert.Equal(new[] { "Communities that last", "Learning for the future", "Growth built on knowledge" }, paragraphs.Select(r => r.Text));
    }
}
