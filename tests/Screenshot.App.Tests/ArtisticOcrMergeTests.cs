using System.Text.Json;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ArtisticOcrMergeTests
{
    [Fact]
    public void CorrectsWrongColumnWhileKeepingUnrelatedTextAndWordPositions()
    {
        var wrong = Region("房", 0, .55f, false);
        var unrelated = Region("标题", 200, .96f, false);
        var corrected = Region("春夏秋", 2, .99f, true);
        var result = ArtisticOcrMerge.Merge(Result(wrong, unrelated), Result(corrected));
        Assert.DoesNotContain("房", result.Text);
        Assert.Equal(new[] { corrected, unrelated }, result.Regions);
        Assert.Collection(result.Words, word => Assert.Equal("春夏秋", word.Text), word => Assert.Equal("标题", word.Text));
        Assert.Equal(2, result.Words[0].X);
        Assert.Equal(200, result.Words[1].X);
    }

    [Fact]
    public void AddsMissingColumnDespiteDifferentGlyphInExistingColumn()
    {
        var existing = Region("牛兔鷄", 100, .9f, true);
        var weakerVariant = Region("牛兔鶏", 101, .85f, true);
        var missing = Region("鼠虎猴", 0, .99f, true);
        var result = ArtisticOcrMerge.Merge(Result(existing), Result(weakerVariant, missing));
        Assert.Equal(new[] { missing, existing }, result.Regions);
        Assert.Equal(2, result.Words.Count);
    }

    [Theory]
    [InlineData(.99f, .9f, true)]
    [InlineData(.6f, .99f, false)]
    [InlineData(.6f, .79f, true)]
    public void DoesNotReplaceWithWeakerUnverifiedOrLowConfidenceText(float fastScore, float candidateScore, bool verified)
    {
        var fast = Result(Region("春夏秋", 0, fastScore, true));
        Assert.Same(fast, ArtisticOcrMerge.Merge(fast, Result(Region("房", 0, candidateScore, verified))));
    }

    [Fact]
    public void AHeadingCannotEraseASentenceItOnlyPartiallyCovers()
    {
        var sentence = new OcrTextRegion("完整句子应当保留", 10, 10, 300, 40) { RecognitionConfidence = .6f };
        var partial = new OcrTextRegion("句子", 100, 10, 70, 40) { RecognitionConfidence = .99f, VerifiedArtisticText = true };
        var fast = Result(sentence);
        Assert.Same(fast, ArtisticOcrMerge.Merge(fast, Result(partial)));
    }

    [Fact]
    public void FailedEnhancementRetainsFastResultAndInternalEvidenceStaysOffThePipe()
    {
        var fast = Result(Region("文字", 0, .99f, true));
        Assert.Same(fast, ArtisticOcrMerge.Merge(fast, OcrRecognitionResult.Failure("failed")));
        var serialized = JsonSerializer.Serialize(fast);
        Assert.DoesNotContain("RecognitionConfidence", serialized);
        Assert.DoesNotContain("RecoveredVerticalColumn", serialized);
        Assert.DoesNotContain("VerifiedArtisticText", serialized);
    }

    [Theory]
    [InlineData(235, 321, true)]
    [InlineData(470, 642, true)]
    [InlineData(900, 800, false)]
    [InlineData(1658, 1115, false)]
    public void EnhancementHasAPixelBudget(int width, int height, bool expected)
    {
        Assert.Equal(expected, HighQualityOcrService.NeedsArtisticEnhancement(width, height, Result()));
        // A readable horizontal label does not load Medium at any of these sizes.
        Assert.False(HighQualityOcrService.NeedsArtisticEnhancement(width, height,
            Result(new OcrTextRegion("普通文字", 0, 0, 180, 25))));
    }

    private static OcrTextRegion Region(string text, double x, float confidence, bool recovered) =>
        new(text, x, 0, 40, 150) { RecognitionConfidence = confidence, RecoveredVerticalColumn = recovered };

    private static OcrRecognitionResult Result(params OcrTextRegion[] regions) =>
        new(true, string.Join(Environment.NewLine, regions.Select(region => region.Text)), null)
        {
            Regions = regions,
            Words = regions.Select(region => new OcrWordRegion(region.Text, region.X, region.Y, region.Width, region.Height)).ToArray(),
        };
}
