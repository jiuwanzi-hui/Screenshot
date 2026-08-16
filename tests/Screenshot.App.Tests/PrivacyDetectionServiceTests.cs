using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class PrivacyDetectionServiceTests
{
    [Fact]
    public void DetectsSupportedSensitiveDataWithoutApplyingMasksAutomatically()
    {
        var words = new[]
        {
            Word("13800138000", 0),
            Word("name@example.com", 30),
            Word("11010519491231002X", 60),
            Word("sk-1234567890abcdefghijkl", 90),
            Word("192.168.10.24", 120),
        };
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words = words,
            Regions = words.Select(word => new OcrTextRegion(
                word.Text,
                word.X,
                word.Y,
                word.Width,
                word.Height)).ToArray(),
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(5, candidates.Count);
        Assert.Equal(
            Enum.GetValues<PrivacyDataKind>().Order(),
            candidates.Select(candidate => candidate.Kind).Order());
        Assert.All(candidates, candidate =>
        {
            Assert.False(candidate.Bounds.IsEmpty);
            Assert.Contains('*', candidate.MaskedValue);
        });
    }

    [Fact]
    public void RejectsInvalidIpAndIdentityNumbers()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                Word("999.168.1.1", 0),
                Word("110105194912310021", 30),
            ],
        };

        Assert.Empty(PrivacyDetectionService.Detect(result));
    }

    private static OcrWordRegion Word(string text, double y) =>
        new(text, 10, y, Math.Max(80, text.Length * 9), 24);
}
