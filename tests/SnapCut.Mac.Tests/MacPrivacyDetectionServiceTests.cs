using Avalonia;
using SnapCut.Mac.Text;

namespace SnapCut.Mac.Tests;

public sealed class MacPrivacyDetectionServiceTests
{
    [Fact]
    public void DetectsSupportedSensitiveValuesAndRejectsInvalidIp()
    {
        var result = new MacOcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new MacOcrWordRegion("13800138000", new Rect(0, 0, 100, 20)),
                new MacOcrWordRegion("dev@example.com", new Rect(0, 30, 140, 20)),
                new MacOcrWordRegion("192.168.1.8", new Rect(0, 60, 120, 20)),
                new MacOcrWordRegion("999.168.1.8", new Rect(0, 90, 120, 20)),
                new MacOcrWordRegion(
                    "sk-abcdefghijklmnop1234",
                    new Rect(0, 120, 180, 20)),
            ],
        };

        var candidates = MacPrivacyDetectionService.Detect(result);

        Assert.Contains(candidates, item => item.Kind == MacPrivacyDataKind.PhoneNumber);
        Assert.Contains(candidates, item => item.Kind == MacPrivacyDataKind.EmailAddress);
        Assert.Contains(candidates, item =>
            item.Kind == MacPrivacyDataKind.IpAddress && item.Value == "192.168.1.8");
        Assert.DoesNotContain(candidates, item => item.Value == "999.168.1.8");
        Assert.Contains(candidates, item => item.Kind == MacPrivacyDataKind.ApiKey);
    }

    [Fact]
    public void RedactionChangesOnlyConfirmedRegion()
    {
        var source = new SnapCut.Core.PixelImage(40, 20);
        for (var x = 0; x < 40; x++)
        {
            var value = (byte)((x & 1) == 0 ? 0 : 255);
            source.FillRect(x, 0, 1, 20, value, value, value);
        }
        var candidate = new MacPrivacyCandidate(
            MacPrivacyDataKind.EmailAddress,
            "dev@example.com",
            new Rect(4, 4, 14, 10));

        var result = MacPrivacyRedactionRenderer.Apply(source, [candidate]);

        Assert.Equal(source.Pixels[0], result.Pixels[0]);
        Assert.True(result.Pixels
            .Zip(source.Pixels, (actual, original) => actual != original)
            .Count(changed => changed) > 20);
    }
}
