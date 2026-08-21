using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;

namespace SnapCut.Mac.Text;

internal enum MacPrivacyDataKind
{
    PhoneNumber,
    EmailAddress,
    IdentityNumber,
    ApiKey,
    IpAddress,
}

internal sealed record MacPrivacyCandidate(
    MacPrivacyDataKind Kind,
    string Value,
    Rect Bounds)
{
    public string KindLabel => Kind switch
    {
        MacPrivacyDataKind.PhoneNumber => "手机号",
        MacPrivacyDataKind.EmailAddress => "邮箱",
        MacPrivacyDataKind.IdentityNumber => "身份证号",
        MacPrivacyDataKind.ApiKey => "API Key",
        MacPrivacyDataKind.IpAddress => "IP 地址",
        _ => "敏感信息",
    };
}

internal static partial class MacPrivacyDetectionService
{
    public static IReadOnlyList<MacPrivacyCandidate> Detect(
        MacOcrRecognitionResult recognition)
    {
        if (!recognition.IsSuccess)
        {
            return [];
        }

        var candidates = new List<MacPrivacyCandidate>();
        var sources = recognition.Words.Count > 0
            ? recognition.Words.Select(word => new SourceText(word.Text, word.Bounds))
            : recognition.Regions.Select(region => new SourceText(region.Text, region.Bounds));
        foreach (var source in sources)
        {
            DetectInSource(source, candidates);
        }

        foreach (var region in recognition.Regions)
        {
            DetectInSource(new SourceText(region.Text, region.Bounds), candidates);
        }

        return candidates
            .Where(candidate => candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0)
            .OrderBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .Aggregate(new List<MacPrivacyCandidate>(), (unique, candidate) =>
            {
                if (!unique.Any(existing =>
                        existing.Kind == candidate.Kind &&
                        string.Equals(
                            Normalize(existing.Value),
                            Normalize(candidate.Value),
                            StringComparison.OrdinalIgnoreCase) &&
                        OverlapRatio(existing.Bounds, candidate.Bounds) > 0.55))
                {
                    unique.Add(candidate);
                }

                return unique;
            });
    }

    private static void DetectInSource(
        SourceText source,
        List<MacPrivacyCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(source.Text) ||
            source.Bounds.Width <= 0 || source.Bounds.Height <= 0)
        {
            return;
        }

        AddMatches(EmailRegex(), MacPrivacyDataKind.EmailAddress);
        AddMatches(PhoneRegex(), MacPrivacyDataKind.PhoneNumber);
        AddMatches(ApiKeyRegex(), MacPrivacyDataKind.ApiKey);
        AddMatches(Ipv4Regex(), MacPrivacyDataKind.IpAddress, IsValidIpv4);
        AddMatches(IdentityRegex(), MacPrivacyDataKind.IdentityNumber, IsValidIdentityNumber);
        return;

        void AddMatches(
            Regex regex,
            MacPrivacyDataKind kind,
            Func<string, bool>? validator = null)
        {
            foreach (Match match in regex.Matches(source.Text))
            {
                var value = match.Groups["value"].Success
                    ? match.Groups["value"].Value
                    : match.Value;
                if (validator?.Invoke(value) == false)
                {
                    continue;
                }

                candidates.Add(new MacPrivacyCandidate(kind, value, source.Bounds));
            }
        }
    }

    private static bool IsValidIpv4(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 4 && parts.All(part =>
            byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static bool IsValidIdentityNumber(string value)
    {
        var normalized = Normalize(value).ToUpperInvariant();
        if (normalized.Length == 15)
        {
            return normalized.All(char.IsDigit);
        }

        if (normalized.Length != 18 ||
            !normalized[..17].All(char.IsDigit) ||
            !(char.IsDigit(normalized[17]) || normalized[17] == 'X') ||
            !DateTime.TryParseExact(
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

    private static string Normalize(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

    private static double OverlapRatio(Rect left, Rect right)
    {
        var intersection = left.Intersect(right);
        return intersection.Width <= 0 || intersection.Height <= 0
            ? 0
            : (intersection.Width * intersection.Height) /
              Math.Max(1, Math.Min(left.Width * left.Height, right.Width * right.Height));
    }

    private sealed record SourceText(string Text, Rect Bounds);

    [GeneratedRegex(@"(?<![\w.+-])(?<value>[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+)(?![\w.-])", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?<value>(?:\+?86[\s-]?)?1[3-9]\d{9})(?!\d)")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<value>(?:sk|rk|pk)-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{24,}|(?:api[_-]?key|token|secret)\s*[:=]\s*[A-Za-z0-9_./+-]{12,})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?<![\d.])(?<value>\d{1,3}(?:\.\d{1,3}){3})(?![\d.])")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?<!\d)(?<value>(?:\d{6}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx]|\d{6}\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}))(?!\d)")]
    private static partial Regex IdentityRegex();
}
