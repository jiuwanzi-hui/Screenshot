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
            new[]
            {
                PrivacyDataKind.PhoneNumber,
                PrivacyDataKind.EmailAddress,
                PrivacyDataKind.IdentityNumber,
                PrivacyDataKind.ApiKey,
                PrivacyDataKind.IpAddress,
            }.Order(),
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
                Word("155662870643729", 60),
            ],
        };

        Assert.Empty(PrivacyDetectionService.Detect(result));
    }

    [Fact]
    public void DetectsConcatenatedEmailPhoneAndIdentityNumber()
    {
        const string text =
            "854074372@qq.com15566287064372925199706096351";
        var result = new OcrRecognitionResult(true, text, null)
        {
            Words = [Word(text, 0)],
            Regions = [Region(text, 0)],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.EmailAddress &&
            candidate.Value == "854074372@qq.com");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PhoneNumber &&
            candidate.Value == "15566287064");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.IdentityNumber &&
            candidate.Value == "372925199706096351");
    }

    [Fact]
    public void DoesNotDetectPhoneNumberInsideIdentityNumber()
    {
        const string identityNumber = "372925199706096351";
        var result = new OcrRecognitionResult(true, identityNumber, null)
        {
            Regions = [Region(identityNumber, 0)],
        };

        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));

        Assert.Equal(PrivacyDataKind.IdentityNumber, candidate.Kind);
    }

    [Fact]
    public void SplitsNumericChainBeforeEmail()
    {
        const string text =
            "15566287064372925199706096351854074372@qq.com";
        var result = new OcrRecognitionResult(true, text, null)
        {
            Regions = [Region(text, 0)],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.EmailAddress &&
            candidate.Value == "854074372@qq.com");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PhoneNumber &&
            candidate.Value == "15566287064");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.IdentityNumber &&
            candidate.Value == "372925199706096351");
    }

    [Fact]
    public void DetectsLabeledPersonalAndCredentialInformation()
    {
        var regions = new[]
        {
            Region("地址：北碚路 980 弄通协小区 76 号 601", 0),
            Region("姓名：王云辉", 30),
            Region("银行卡号：4532015112830366", 60),
            Region("微信号：jiuwanzi_2026", 90),
            Region("验证码：482931", 120),
            Region("护照号：E12345678", 150),
        };
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Regions = regions,
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(6, candidates.Count);
        Assert.Equal(
            new[]
            {
                PrivacyDataKind.PostalAddress,
                PrivacyDataKind.PersonName,
                PrivacyDataKind.BankCardNumber,
                PrivacyDataKind.AccountIdentifier,
                PrivacyDataKind.SecretValue,
                PrivacyDataKind.DocumentNumber,
            }.Order(),
            candidates.Select(candidate => candidate.Kind).Order());
        Assert.All(candidates, candidate =>
        {
            var region = regions.Single(item => item.Y == candidate.Bounds.Y);
            Assert.True(candidate.Bounds.Left > region.X);
            Assert.True(candidate.Bounds.Right <= region.X + region.Width);
        });
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PersonName &&
            candidate.Value == "王云辉");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress &&
            candidate.Value == "北碚路 980 弄通协小区 76 号 601");
    }

    [Fact]
    public void RejectsUnlabeledOrInvalidPersonalInformation()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Regions =
            [
                Region("王云辉住在北碚路", 0),
                Region("姓名：无", 30),
                Region("地址：未知", 60),
                Region("银行卡号：1234567890123456", 90),
                Region("验证码：12", 120),
            ],
        };

        Assert.Empty(PrivacyDetectionService.Detect(result));
    }

    [Fact]
    public void AddressStopsBeforeAnotherLabeledFieldOnTheSameLine()
    {
        const string text = "地址：北碚路 980 号 姓名：王云辉";
        var result = new OcrRecognitionResult(true, text, null)
        {
            Regions = [Region(text, 0)],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress &&
            candidate.Value == "北碚路 980 号");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PersonName &&
            candidate.Value == "王云辉");
    }

    [Fact]
    public void DetectsNaturalNameAndAddressExpressions()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Regions =
            [
                Region("名字是王云辉", 0),
                Region("住址：北碚路 980 号", 30),
            ],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PersonName &&
            candidate.Value == "王云辉");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress &&
            candidate.Value == "北碚路 980 号");
    }

    [Fact]
    public void DetectsPersonalFieldsSplitAcrossOcrWords()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("住址：", 10, 0, 48, 24),
                new OcrWordRegion("北碚路980号", 62, 0, 120, 24),
                new OcrWordRegion("名字是", 10, 30, 48, 24),
                new OcrWordRegion("王云辉", 62, 30, 54, 24),
            ],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress &&
            candidate.Value == "北碚路980号");
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PersonName &&
            candidate.Value == "王云辉");
    }

    [Fact]
    public void DetectsColloquialAddressAndStandaloneNameExpressions()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Regions =
            [
                Region("家住北碚路980弄通协小区 76号601", 0),
                Region("出租屋在北碚路980弄通协小区 76号601", 30),
                Region("名字", 60),
                Region("叫王云辉", 90),
            ],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(2, candidates.Count(candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress));
        Assert.Contains(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress &&
            candidate.Value == "北碚路980弄通协小区 76号601");
    }

    [Theory]
    [InlineData("我家在北碚路980弄通协小区76号601")]
    [InlineData("现住在北碚路980弄通协小区76号601")]
    [InlineData("租房在北碚路980弄通协小区76号601")]
    [InlineData("住处在北碚路980弄通协小区76号601")]
    [InlineData("公司位于北碚路980弄通协小区76号601")]
    [InlineData("办公地点：北碚路980弄通协小区76号601")]
    [InlineData("户口地址为北碚路980弄通协小区76号601")]
    public void DetectsAdditionalExplicitAddressExpressions(string text)
    {
        var result = new OcrRecognitionResult(true, text, null)
        {
            Regions = [Region(text, 0)],
        };

        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));

        Assert.Equal(PrivacyDataKind.PostalAddress, candidate.Kind);
        Assert.Equal("北碚路980弄通协小区76号601", candidate.Value);
    }

    [Theory]
    [InlineData("本人叫王云辉")]
    [InlineData("本人名叫王云辉")]
    [InlineData("法定代表人：王云辉")]
    [InlineData("负责人姓名为王云辉")]
    [InlineData("紧急联系人 王云辉")]
    [InlineData("收件人姓名是王云辉")]
    public void DetectsAdditionalExplicitNameExpressions(string text)
    {
        var result = new OcrRecognitionResult(true, text, null)
        {
            Regions = [Region(text, 0)],
        };

        var candidate = Assert.Single(PrivacyDetectionService.Detect(result));

        Assert.Equal(PrivacyDataKind.PersonName, candidate.Kind);
        Assert.Equal("王云辉", candidate.Value);
    }

    [Theory]
    [InlineData("叫王云辉")]
    [InlineData("这个功能叫做王云辉")]
    [InlineData("我是在这里工作的")]
    public void RejectsAmbiguousNameExpressions(string text)
    {
        var result = new OcrRecognitionResult(true, text, null)
        {
            Regions = [Region(text, 0)],
        };

        Assert.Empty(PrivacyDetectionService.Detect(result));
    }

    [Fact]
    public void DetectsAddressAndContextualNameFromReportedScreenshot()
    {
        var result = new OcrRecognitionResult(true, string.Empty, null)
        {
            Regions =
            [
                Region("家在北碚路 980 弄通协小区 76 号 601", 0),
                Region("出租屋在北碚路 980 弄通协小区 76 号 601", 30),
                Region("名字", 60),
                Region("叫王云辉", 90),
            ],
        };

        var candidates = PrivacyDetectionService.Detect(result);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(2, candidates.Count(candidate =>
            candidate.Kind == PrivacyDataKind.PostalAddress));
        Assert.DoesNotContain(candidates, candidate =>
            candidate.Kind == PrivacyDataKind.PersonName);
    }

    private static OcrWordRegion Word(string text, double y) =>
        new(text, 10, y, Math.Max(80, text.Length * 9), 24);

    private static OcrTextRegion Region(string text, double y) =>
        new(text, 10, y, Math.Max(120, text.Length * 12), 24);
}
