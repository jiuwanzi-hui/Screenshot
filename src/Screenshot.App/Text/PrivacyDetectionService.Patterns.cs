using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

public static partial class PrivacyDetectionService
{
    private static bool IsUsefulPhoneNumber(string value) =>
        value.Count(char.IsDigit) is >= 7 and <= 15;

    private static bool IsValidIpv6(string value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool IsValidBirthDate(string value) =>
        DateTime.TryParseExact(value, ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy年M月d日"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
        date.Year >= 1900 && date <= DateTime.Today;

    [GeneratedRegex(@"(?<!\d)(?<value>(?:\+?86[ -]?)?1[3-9]\d[ -]\d{4}[ -]\d{4})(?!\d)")]
    private static partial Regex GroupedPhoneRegex();

    [GeneratedRegex(@"(?<!\d)(?<value>0\d{2,3}[ -]\d{7,8}(?:[ -](?:转|ext\.?)[ ]?\d{1,6})?)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex LandlineRegex();

    [GeneratedRegex(@"(?:联系电话|固定电话|座机|电话|phone|mobile|tel)\s*[:：=]\s*(?<value>\+?[\d(][\d() -]{5,22}\d)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledPhoneRegex();

    [GeneratedRegex("(?:api[_ -]?key|access[_ -]?token|refresh[_ -]?token|client[_ -]?secret|session[_ -]?id|authorization|cookie|token|secret|密钥|访问令牌|会话ID)\\s*[:：=]\\s*[\"']?(?:Bearer[ \\t]+)?(?<value>[A-Za-z0-9_./+~=%-]{8,512})", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(?<![\w.-])(?<value>eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})(?![\w.-])")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9:])(?<value>(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[A-Za-z0-9]+)?)(?![A-Za-z0-9:])")]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"(?<![\w:-])(?<value>[0-9A-F]{2}(?::[0-9A-F]{2}){5}|[0-9A-F]{2}(?:-[0-9A-F]{2}){5})(?![\w:-])", RegexOptions.IgnoreCase)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"(?:IMEI|IMSI|ICCID|设备序列号|设备ID|序列号|serial[ _-]?(?:number|no)|S/N)\s*[:：=]\s*(?<value>[A-Za-z0-9][A-Za-z0-9_-]{5,39})(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdentifierRegex();

    [GeneratedRegex(@"(?:出生日期|出生年月|生日|date[ ]of[ ]birth|DOB)\s*[:：=]\s*(?<value>\d{4}(?:[-/.]\d{1,2}[-/.]\d{1,2}|年\d{1,2}月\d{1,2}日))(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex BirthDateRegex();

    [GeneratedRegex(@"(?<![\w])(?<value>[京津沪渝冀豫云辽黑湘皖鲁新苏浙赣鄂桂甘晋蒙陕吉闽贵粤青藏川宁琼][A-HJ-NP-Z][A-HJ-NP-Z0-9]{5,6})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex VehicleRegistrationRegex();

    [GeneratedRegex(@"(?:快递单号|物流单号|运单号|订单编号|订单号|tracking[ _-]?(?:number|no)|order[ _-]?(?:id|number))\s*[:：=]\s*(?<value>[A-Za-z0-9][A-Za-z0-9-]{5,39})(?![A-Za-z0-9-])", RegexOptions.IgnoreCase)]
    private static partial Regex TrackingNumberRegex();

    [GeneratedRegex(@"(?:统一社会信用代码|纳税人识别号|税号)\s*[:：=]\s*(?<value>[A-Za-z0-9]{18}|[A-Za-z0-9]{15})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex OrganizationIdentifierRegex();

    // Stop greedy field values before the next labeled cell, including
    // non-private fields whose content must remain readable.
    [GeneratedRegex(@"(?:真实姓名|姓名|名字|联系人|收件人|手机号|联系电话|电话|身份证号|邮箱|银行卡号|账号|账户|微信号|QQ号|密码|验证码|护照号|证件号|出生日期|车牌号|快递单号|订单号|备注|状态|金额|余额|性别|年龄|日期|数量|价格|邮编|商品名称|有效期|说明|name|address|status|note|amount)\s*[:：=]", RegexOptions.IgnoreCase)]
    private static partial Regex FollowingFieldRegex();
}
