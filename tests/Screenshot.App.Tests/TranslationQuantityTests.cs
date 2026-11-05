using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationQuantityTests
{
    [Theory]
    [InlineData("83 million questions", "8300 万个问题", "zh")]
    [InlineData("113 billion uses", "1130 亿次使用", "zh")]
    [InlineData("8300 万个问题", "83 million questions", "en")]
    [InlineData("0.83 亿个问题", "83 million questions", "en")]
    [InlineData("1.5 million participants", "150 万名参与者", "zh")]
    [InlineData("1,500 thousand users", "150 万用户", "zh")]
    [InlineData("２００万名用户", "2 million users", "en")]
    [InlineData("8300万个问题", "83 Millionen Fragen", "de")]
    [InlineData("8300万个问题", "83 millones de preguntas", "es")]
    [InlineData("8300万个问题", "83 миллиона вопросов", "ru")]
    [InlineData("8300万个问题", "83.000.000 Fragen", "de")]
    [InlineData("8300万个问题", "83 000 000 questions", "fr")]
    [InlineData("1亿2000万个问题", "120 million questions", "en")]
    [InlineData("8千3百个问题", "8300 questions", "en")]
    public void EquivalentScaledQuantitiesPassInBothDirections(string source, string output, string target) =>
        Assert.Null(TranslationCompletenessValidator.Validate(source, output, target));

    [Theory]
    [InlineData("83 million questions", "83 亿个问题")]
    [InlineData("113 billion uses", "113 亿次使用")]
    [InlineData("1.5 million participants", "15 万名参与者")]
    [InlineData("83 million questions and 21 seconds", "8300 万个问题")]
    [InlineData("Version 1.2.3", "版本 1.3.2.4")]
    public void ChangedOrMissingQuantitiesStillFail(string source, string output) =>
        Assert.Contains("数字", TranslationCompletenessValidator.Validate(source, output, "zh"));

    [Theory]
    [InlineData("请在下午3点提交报告。", "Please submit the report by 3:00 PM.", "en")]
    [InlineData("请在下午3点提交报告。", "Veuillez remettre le rapport avant 15h.", "fr")]
    [InlineData("Please submit the report by 3:00 PM.", "请在下午3点提交报告。", "zh")]
    public void EquivalentClockNotationsDoNotTriggerRepairs(string source, string output, string target) =>
        Assert.Null(TranslationCompletenessValidator.Validate(source, output, target));

    [Fact]
    public void ChangedTimeOfDayIsRejected() => Assert.Contains("数字",
        TranslationCompletenessValidator.Validate("请在下午3点提交报告。", "Please submit the report by 3:00 AM.", "en"));

    [Fact]
    public void PromptNormalizesLargeQuantitiesWithoutAskingTheModelToDoArithmetic() =>
        Assert.Equal("过去18年解答了83000000个问题。", TranslationTextNormalizer.Normalize("过去18年解答了8300万个问题。"));
}
