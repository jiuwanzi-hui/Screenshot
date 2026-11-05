using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class AdditionalPrivacyDetectionTests
{
    [Theory]
    [InlineData("电话：138 0013 8000", PrivacyDataKind.PhoneNumber, "138 0013 8000")]
    [InlineData("联系电话：+86 138-0013-8000", PrivacyDataKind.PhoneNumber, "+86 138-0013-8000")]
    [InlineData("座机：010-87654321", PrivacyDataKind.PhoneNumber, "010-87654321")]
    [InlineData("电话：(010) 87654321", PrivacyDataKind.PhoneNumber, "(010) 87654321")]
    [InlineData("出生日期：1995-08-21", PrivacyDataKind.BirthDate, "1995-08-21")]
    [InlineData("生日：1995年8月21日", PrivacyDataKind.BirthDate, "1995年8月21日")]
    [InlineData("DOB: 1995/8/21", PrivacyDataKind.BirthDate, "1995/8/21")]
    [InlineData("车牌号：京A12345", PrivacyDataKind.VehicleRegistration, "京A12345")]
    [InlineData("粤BD12345", PrivacyDataKind.VehicleRegistration, "粤BD12345")]
    [InlineData("IMEI: 867123045678901", PrivacyDataKind.DeviceIdentifier, "867123045678901")]
    [InlineData("设备序列号：SN-ABCD123456", PrivacyDataKind.DeviceIdentifier, "SN-ABCD123456")]
    [InlineData("MAC: AA:BB:CC:DD:EE:FF", PrivacyDataKind.DeviceIdentifier, "AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF", PrivacyDataKind.DeviceIdentifier, "AA-BB-CC-DD-EE-FF")]
    [InlineData("IP: 2001:db8::1234", PrivacyDataKind.IpAddress, "2001:db8::1234")]
    [InlineData("快递单号：SF1234567890", PrivacyDataKind.TrackingNumber, "SF1234567890")]
    [InlineData("订单号：ORD-20260908-001", PrivacyDataKind.TrackingNumber, "ORD-20260908-001")]
    [InlineData("税号：91310000ABC1234567", PrivacyDataKind.OrganizationIdentifier, "91310000ABC1234567")]
    [InlineData("工号：EMP-00628", PrivacyDataKind.AccountIdentifier, "EMP-00628")]
    [InlineData("学号：STU20260908", PrivacyDataKind.AccountIdentifier, "STU20260908")]
    [InlineData("提取码：aB39", PrivacyDataKind.SecretValue, "aB39")]
    [InlineData("password: abc12345", PrivacyDataKind.SecretValue, "abc12345")]
    [InlineData("社保号：A123456789", PrivacyDataKind.DocumentNumber, "A123456789")]
    [InlineData("API_KEY=\"abcdef1234567890\"", PrivacyDataKind.ApiKey, "abcdef1234567890")]
    [InlineData("Authorization: Bearer abcdef1234567890", PrivacyDataKind.ApiKey, "abcdef1234567890")]
    [InlineData("access_token: abcdef1234567890", PrivacyDataKind.ApiKey, "abcdef1234567890")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk12345", PrivacyDataKind.ApiKey,
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk12345")]
    public void DetectsAdditionalPrivacyWithValueOnly(string text, PrivacyDataKind kind, string value)
    {
        var candidate = Assert.Single(Detect(text));
        Assert.Equal(kind, candidate.Kind);
        Assert.Equal(value, candidate.Value);
    }

    [Theory]
    [InlineData("日期：1995-08-21")]
    [InlineData("生日：1995-02-30")]
    [InlineData("生日：2099-01-01")]
    [InlineData("订单数量：123456")]
    [InlineData("普通产品 ABC123456")]
    [InlineData("设备温度：123456")]
    [InlineData("IP: 2001:db8:::1234")]
    [InlineData("时间 12:30:45")]
    [InlineData("MAC: GG:HH:II:JJ:KK:LL")]
    public void RejectsInvalidFormatsAndUnlabeledAmbiguousValues(string text) => Assert.Empty(Detect(text));

    [Theory]
    [InlineData("地址：北碚路980号 备注：请留门口", PrivacyDataKind.PostalAddress, "北碚路980号")]
    [InlineData("地址：北碚路980号备注：请留门口", PrivacyDataKind.PostalAddress, "北碚路980号")]
    [InlineData("姓名：王云辉状态：正常", PrivacyDataKind.PersonName, "王云辉")]
    [InlineData("姓名：王云辉金额：100", PrivacyDataKind.PersonName, "王云辉")]
    public void KeepsAdjacentOrdinaryFieldsReadable(string text, PrivacyDataKind kind, string value)
    {
        var candidate = Assert.Single(Detect(text));
        Assert.Equal(kind, candidate.Kind);
        Assert.Equal(value, candidate.Value);
    }

    private static IReadOnlyList<PrivacyCandidate> Detect(string text) =>
        PrivacyDetectionService.Detect(new OcrRecognitionResult(true, text, null)
        {
            Regions = [new(text, 10, 10, text.Length * 12, 24)],
        });
}
