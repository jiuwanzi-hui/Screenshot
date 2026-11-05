using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

namespace Screenshot.App.Text;

public enum PrivacyDataKind
{
    PhoneNumber,
    EmailAddress,
    IdentityNumber,
    ApiKey,
    IpAddress,
    PersonName,
    PostalAddress,
    BankCardNumber,
    AccountIdentifier,
    SecretValue,
    DocumentNumber,
    BirthDate,
    VehicleRegistration,
    DeviceIdentifier,
    TrackingNumber,
    OrganizationIdentifier,
}

public sealed record PrivacyCandidate(
    PrivacyDataKind Kind,
    string Value,
    Rect Bounds)
{
    internal bool HasWordBounds { get; init; }
    internal Rect SourceBounds { get; init; }
    public string KindLabel => Kind switch
    {
        PrivacyDataKind.PhoneNumber => "电话号码",
        PrivacyDataKind.EmailAddress => "邮箱",
        PrivacyDataKind.IdentityNumber => "身份证号",
        PrivacyDataKind.ApiKey => "API Key",
        PrivacyDataKind.IpAddress => "IP 地址",
        PrivacyDataKind.PersonName => "姓名",
        PrivacyDataKind.PostalAddress => "地址",
        PrivacyDataKind.BankCardNumber => "银行卡号",
        PrivacyDataKind.AccountIdentifier => "账号",
        PrivacyDataKind.SecretValue => "密码/验证码",
        PrivacyDataKind.DocumentNumber => "证件号",
        PrivacyDataKind.BirthDate => "出生日期",
        PrivacyDataKind.VehicleRegistration => "车牌号",
        PrivacyDataKind.DeviceIdentifier => "设备标识",
        PrivacyDataKind.TrackingNumber => "订单/快递单号",
        PrivacyDataKind.OrganizationIdentifier => "税号/信用代码",
        _ => "敏感信息",
    };

    public string MaskedValue
    {
        get
        {
            var value = Value.Trim();
            if (value.Length <= 4)
            {
                return new string('*', value.Length);
            }

            var visible = Math.Min(4, Math.Max(1, value.Length / 5));
            return string.Concat(
                value.AsSpan(0, visible),
                new string('*', Math.Min(12, value.Length - (visible * 2))),
                value.AsSpan(value.Length - visible));
        }
    }
}

public static partial class PrivacyDetectionService
{
    public static IReadOnlyList<PrivacyCandidate> Detect(
        OcrRecognitionResult recognition)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        if (!recognition.IsSuccess)
        {
            return [];
        }

        var candidates = new List<PrivacyCandidate>();
        foreach (var word in recognition.Words)
        {
            DetectInSource(CreateWordSource(word), candidates);
        }

        foreach (var source in BuildWordLineSources(recognition.Words))
        {
            DetectInSource(source, candidates);
        }

        // OCR engines sometimes split e-mail addresses and keys into several
        // words. A line-level pass catches those cases while the overlap pass
        // below prevents duplicate masks.
        foreach (var region in recognition.Regions)
        {
            DetectInSource(
                CreateRegionSource(region, recognition.Words),
                candidates);
        }

        return candidates
            .Where(candidate => candidate.Bounds.Width > 0 &&
                                candidate.Bounds.Height > 0)
            .OrderByDescending(candidate => candidate.HasWordBounds)
            .ThenBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .Aggregate(
                new List<PrivacyCandidate>(),
                (unique, candidate) =>
                {
                    if (!unique.Any(existing =>
                            string.Equals(
                                Normalize(existing.Value),
                                Normalize(candidate.Value),
                                StringComparison.OrdinalIgnoreCase) &&
                            (OverlapRatio(existing.Bounds, candidate.Bounds) > 0.55 ||
                             (!candidate.HasWordBounds && existing.HasWordBounds &&
                              candidate.SourceBounds.Contains(existing.Bounds)))))
                    {
                        unique.Add(candidate);
                    }
                    return unique;
                })
            .OrderBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .ToArray();
    }

    private static void DetectInSource(
        SourceText source,
        List<PrivacyCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(source.Text) || source.Bounds.IsEmpty)
        {
            return;
        }

        var protectedNumericRanges = new List<TextRange>();
        foreach (Match match in Identity18Regex().Matches(source.Text))
        {
            var group = match.Groups["value"];
            protectedNumericRanges.Add(new TextRange(group.Index, group.Length));
        }

        AddEmailMatches();
        AddMatches(Identity18Regex(), PrivacyDataKind.IdentityNumber,
            IsValidIdentityNumber, protectNumericRange: true);
        AddMatches(Identity15Regex(), PrivacyDataKind.IdentityNumber,
            IsValidIdentityNumber, protectNumericRange: true);
        AddMatches(BankCardRegex(), PrivacyDataKind.BankCardNumber,
            IsValidBankCardNumber, protectNumericRange: true);
        AddMatches(PhoneRegex(), PrivacyDataKind.PhoneNumber,
            skipProtectedNumericRanges: true,
            rangeValidator: range => IsValidPhoneRange(
                source.Text,
                range,
                protectedNumericRanges));
        AddMatches(ApiKeyRegex(), PrivacyDataKind.ApiKey);
        AddMatches(Ipv4Regex(), PrivacyDataKind.IpAddress, IsValidIpv4);
        AddMatches(PersonNameRegex(), PrivacyDataKind.PersonName,
            IsUsefulLabeledValue);
        AddMatches(AddressRegex(), PrivacyDataKind.PostalAddress,
            IsUsefulAddress);
        AddMatches(AccountRegex(), PrivacyDataKind.AccountIdentifier,
            IsUsefulLabeledValue);
        AddMatches(SecretRegex(), PrivacyDataKind.SecretValue,
            IsUsefulLabeledValue);
        AddMatches(DocumentRegex(), PrivacyDataKind.DocumentNumber,
            IsUsefulLabeledValue);
        AddMatches(GroupedPhoneRegex(), PrivacyDataKind.PhoneNumber,
            skipProtectedNumericRanges: true);
        AddMatches(LandlineRegex(), PrivacyDataKind.PhoneNumber);
        AddMatches(LabeledPhoneRegex(), PrivacyDataKind.PhoneNumber, IsUsefulPhoneNumber);
        AddMatches(CredentialRegex(), PrivacyDataKind.ApiKey);
        AddMatches(JwtRegex(), PrivacyDataKind.ApiKey);
        AddMatches(Ipv6Regex(), PrivacyDataKind.IpAddress, IsValidIpv6);
        AddMatches(MacAddressRegex(), PrivacyDataKind.DeviceIdentifier);
        AddMatches(DeviceIdentifierRegex(), PrivacyDataKind.DeviceIdentifier);
        AddMatches(BirthDateRegex(), PrivacyDataKind.BirthDate, IsValidBirthDate);
        AddMatches(VehicleRegistrationRegex(), PrivacyDataKind.VehicleRegistration);
        AddMatches(TrackingNumberRegex(), PrivacyDataKind.TrackingNumber);
        AddMatches(OrganizationIdentifierRegex(), PrivacyDataKind.OrganizationIdentifier);
        return;

        void AddEmailMatches()
        {
            foreach (Match match in EmailRegex().Matches(source.Text))
            {
                var group = match.Groups["value"];
                var (value, valueIndex) = SplitConcatenatedEmail(
                    group.Value,
                    group.Index);
                AddCandidate(PrivacyDataKind.EmailAddress, value, valueIndex);
            }
        }

        void AddMatches(
            Regex regex,
            PrivacyDataKind kind,
            Func<string, bool>? validator = null,
            bool protectNumericRange = false,
            bool skipProtectedNumericRanges = false,
            Func<TextRange, bool>? rangeValidator = null)
        {
            foreach (Match match in regex.Matches(source.Text))
            {
                var valueGroup = match.Groups["value"].Success
                    ? match.Groups["value"]
                    : match.Groups[0];
                var rawValue = valueGroup.Value;
                var leadingWhitespace = rawValue.Length - rawValue.TrimStart().Length;
                var value = rawValue.Trim();
                var valueIndex = valueGroup.Index + leadingWhitespace;
                if (kind is PrivacyDataKind.PersonName or PrivacyDataKind.PostalAddress or
                    PrivacyDataKind.AccountIdentifier or PrivacyDataKind.SecretValue or
                    PrivacyDataKind.DocumentNumber)
                {
                    var following = FollowingFieldRegex().Match(source.Text, valueIndex);
                    if (following.Success && following.Index < valueIndex + value.Length)
                        value = value[..(following.Index - valueIndex)].TrimEnd(' ', '\t', ',', '，', ';', '；');
                    value = TrimValueAtColumnGap(source, valueIndex, value);
                }
                var valueRange = new TextRange(valueIndex, value.Length);
                if (value.Length == 0) continue;
                if (validator?.Invoke(value) == false)
                {
                    continue;
                }
                if (rangeValidator?.Invoke(valueRange) == false)
                {
                    continue;
                }
                if (skipProtectedNumericRanges && protectedNumericRanges.Any(
                        range => range.Contains(valueRange)))
                {
                    continue;
                }

                if (protectNumericRange)
                {
                    protectedNumericRanges.Add(valueRange);
                }

                AddCandidate(kind, value, valueIndex);
            }
        }

        void AddCandidate(PrivacyDataKind kind, string value, int index)
        {
            var bounds = EstimateValueBounds(source, index, value.Length, out var hasWordBounds);
            candidates.Add(new PrivacyCandidate(kind, value, bounds)
            {
                HasWordBounds = hasWordBounds,
                SourceBounds = source.Bounds,
            });
        }
    }

    private static (string Value, int Index) SplitConcatenatedEmail(
        string email,
        int sourceIndex)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex < 12 ||
            !email[..atIndex].All(char.IsDigit))
        {
            return (email, sourceIndex);
        }

        for (var split = atIndex - 1; split >= 11; split--)
        {
            var prefix = email[..split];
            var suffix = email[split..atIndex];
            if (suffix.Length >= 3 &&
                suffix.All(char.IsDigit) &&
                CanDecomposeNumericChain(prefix))
            {
                return (email[split..], sourceIndex + split);
            }
        }

        return (email, sourceIndex);
    }

    private static bool CanDecomposeNumericChain(string digits)
    {
        if (digits.Length < 22 || !digits.All(char.IsDigit))
        {
            return false;
        }

        var reachable = new bool[digits.Length + 1];
        var tokenCounts = new int[digits.Length + 1];
        reachable[0] = true;
        for (var index = 0; index < digits.Length; index++)
        {
            if (!reachable[index])
            {
                continue;
            }

            TryToken(11, IsValidPhoneDigits);
            TryToken(15, length => IsValidIdentityNumber(digits.Substring(index, length)));
            TryToken(18, length => IsValidIdentityNumber(digits.Substring(index, length)));

            void TryToken(int length, Func<int, bool> validator)
            {
                if (index + length > digits.Length || !validator(length))
                {
                    return;
                }

                var next = index + length;
                var count = tokenCounts[index] + 1;
                if (!reachable[next] || count > tokenCounts[next])
                {
                    reachable[next] = true;
                    tokenCounts[next] = count;
                }
            }

            bool IsValidPhoneDigits(int length) =>
                length == 11 &&
                digits[index] == '1' &&
                digits[index + 1] is >= '3' and <= '9';
        }

        return reachable[^1] && tokenCounts[^1] >= 2;
    }

    private static bool IsValidIpv4(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 4 && parts.All(part =>
            byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture,
                out _));
    }

    private static bool IsValidIdentityNumber(string value)
    {
        var normalized = Normalize(value).ToUpperInvariant();
        if (normalized.Length == 15)
        {
            return normalized.All(char.IsDigit) &&
                   DateTime.TryParseExact(
                       string.Concat("19", normalized.AsSpan(6, 6)),
                       "yyyyMMdd",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _);
        }
        if (normalized.Length != 18 ||
            !normalized[..17].All(char.IsDigit) ||
            !(char.IsDigit(normalized[17]) || normalized[17] == 'X'))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                normalized.Substring(6, 8),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        ReadOnlySpan<int> weights =
            [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        const string checks = "10X98765432";
        var sum = 0;
        for (var index = 0; index < 17; index++)
        {
            sum += (normalized[index] - '0') * weights[index];
        }
        return checks[sum % 11] == normalized[17];
    }

    private static bool IsValidBankCardNumber(string value)
    {
        var digits = Normalize(value);
        if (digits.Length is < 13 or > 19 || !digits.All(char.IsDigit))
        {
            return false;
        }

        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    private static bool IsValidPhoneRange(
        string text,
        TextRange phoneRange,
        IReadOnlyList<TextRange> protectedRanges)
    {
        var digitStart = phoneRange.Start;
        while (digitStart > 0 && char.IsDigit(text[digitStart - 1]))
        {
            digitStart--;
        }

        var digitEnd = phoneRange.End;
        while (digitEnd < text.Length && char.IsDigit(text[digitEnd]))
        {
            digitEnd++;
        }

        if (phoneRange.Start == digitStart && phoneRange.End == digitEnd)
        {
            return true;
        }

        return protectedRanges.Any(range =>
            (phoneRange.Start == digitStart && phoneRange.End == range.Start) ||
            (phoneRange.Start == range.End && phoneRange.End == digitEnd));
    }

    private static bool IsUsefulAddress(string value)
    {
        var normalized = value.Trim().Trim('。', '.', ',', '，', ';', '；');
        return normalized.Length >= 4 && IsUsefulLabeledValue(normalized);
    }

    private static bool IsUsefulLabeledValue(string value)
    {
        var normalized = value.Trim();
        return normalized.Length >= 2 &&
               normalized is not ("无" or "暂无" or "未知" or "未设置" or "保密");
    }

    private static string Normalize(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

    private static double OverlapRatio(Rect left, Rect right)
    {
        var intersection = Rect.Intersect(left, right);
        if (intersection.IsEmpty)
        {
            return 0;
        }
        return (intersection.Width * intersection.Height) /
               Math.Max(1, Math.Min(
                   left.Width * left.Height,
                   right.Width * right.Height));
    }

    private sealed record SourceText(string Text, Rect Bounds, SourceSpan[]? Spans = null);
    private sealed record SourceSpan(int Start, string Text, Rect Bounds);

    private readonly record struct TextRange(int Start, int Length)
    {
        public int End => Start + Length;

        public bool Contains(TextRange other) =>
            other.Start >= Start && other.End <= End;
    }

    [GeneratedRegex(@"(?<![\w.+-])(?<value>[\w.!#$%&'*+/=?^`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,24})(?![A-Za-z.-])", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<value>(?:\+?86[\s-]?)?1[3-9]\d{9})")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<value>(?:sk|rk|pk)-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{24,})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?<![\d.])(?<value>\d{1,3}(?:\.\d{1,3}){3})(?![\d.])")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?<value>\d{6}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx])")]
    private static partial Regex Identity18Regex();

    [GeneratedRegex(@"(?<!\d)(?<value>\d{6}\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3})(?!\d)")]
    private static partial Regex Identity15Regex();

    [GeneratedRegex(@"(?<!\d)(?<value>\d(?:[\s-]?\d){12,18})(?!\d)")]
    private static partial Regex BankCardRegex();

    [GeneratedRegex(@"(?:(?:真实姓名|联系人姓名|本人姓名|法定代表人|负责人姓名|紧急联系人|收货人姓名|收件人姓名|开户姓名|姓名|名字|联系人|收货人|收件人|开户名|户名)(?:\s*(?:是|为|叫|叫做|就是)\s*|\s*[:：=]\s*|\s+)|(?:本人名叫|本人叫|我叫|名叫)\s*)(?<value>(?:[\u3400-\u4DBF\u4E00-\u9FFF·]{2,12}|[A-Za-z][A-Za-z .'-]{1,39}))", RegexOptions.IgnoreCase)]
    private static partial Regex PersonNameRegex();

    [GeneratedRegex(@"(?:(?:家庭住址|户籍地址|户口地址|常住地址|常住地|现住址|现居地址|现居地|居住地址|居住地点|住宅地址|住所|住处|通讯地址|通信地址|收货地址|配送地址|寄送地址|取件地址|联系地址|详细地址|单位地址|公司地址|办公地址|办公地点|宿舍地址|出租屋地址|租房地址|开户地址|所在地址|所在地|地址|住址)(?:\s*(?:是|为|在|位于)\s*|\s*[:：=]\s*|\s+)|(?:我家住在|我家住|我家在|本人住在|本人居住在|现在住在|目前住在|现住在|现居于|住在|居住在|居住于|居所位于|住所位于|住处在|租住在|租住于|租房在|家住|家在|出租屋在|宿舍在|公司位于|单位位于|办公地点在)\s*)(?<value>[^\r\n]{4,100}?)(?=\s+(?:真实姓名|姓名|名字|联系人|收件人|手机号|电话|身份证号|邮箱|银行卡号|账号|账户|微信号|QQ号|密码|验证码|护照号|证件号)\s*(?:[:：=]|是|为)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"(?:登录账号|支付宝账号|账号|账户|用户名|用户ID|微信号|微信ID|QQ号|抖音号|快手号|小红书号|微博账号|学号|工号|员工编号|username|user[ _-]?id|telegram)\s*[:：=]\s*(?<value>[A-Za-z0-9\u3400-\u4DBF\u4E00-\u9FFF_.@-]{3,64})", RegexOptions.IgnoreCase)]
    private static partial Regex AccountRegex();

    [GeneratedRegex(@"(?:支付密码|登录密码|密码|口令|验证码|校验码|动态码|提取码|恢复码|安全码|password|passwd|pwd|otp|verification[ _-]?code)\s*[:：=]\s*(?<value>[^\s,，;；]{4,64})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?:护照号码|护照号|证件号码|证件号|驾驶证号|驾驶证档案编号|社保号|社保卡号|医保卡号|居住证号|港澳通行证号|台湾通行证号|passport(?:[ _-]?(?:no|number))?)\s*[:：=]\s*(?<value>[A-Za-z0-9]{5,24})", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentRegex();

}
