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
}

public sealed record PrivacyCandidate(
    PrivacyDataKind Kind,
    string Value,
    Rect Bounds)
{
    public string KindLabel => Kind switch
    {
        PrivacyDataKind.PhoneNumber => "手机号",
        PrivacyDataKind.EmailAddress => "邮箱",
        PrivacyDataKind.IdentityNumber => "身份证号",
        PrivacyDataKind.ApiKey => "API Key",
        PrivacyDataKind.IpAddress => "IP 地址",
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
        var sources = recognition.Words.Count > 0
            ? recognition.Words.Select(word => new SourceText(
                word.Text,
                new Rect(word.X, word.Y, word.Width, word.Height)))
            : recognition.Regions.Select(region => new SourceText(
                region.Text,
                new Rect(region.X, region.Y, region.Width, region.Height)));

        foreach (var source in sources)
        {
            DetectInSource(source, candidates);
        }

        // OCR engines sometimes split e-mail addresses and keys into several
        // words. A line-level pass catches those cases while the overlap pass
        // below prevents duplicate masks.
        foreach (var region in recognition.Regions)
        {
            DetectInSource(
                new SourceText(
                    region.Text,
                    new Rect(region.X, region.Y, region.Width, region.Height)),
                candidates);
        }

        return candidates
            .Where(candidate => candidate.Bounds.Width > 0 &&
                                candidate.Bounds.Height > 0)
            .OrderBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .Aggregate(
                new List<PrivacyCandidate>(),
                (unique, candidate) =>
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
        ICollection<PrivacyCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(source.Text) || source.Bounds.IsEmpty)
        {
            return;
        }

        AddMatches(EmailRegex(), PrivacyDataKind.EmailAddress);
        AddMatches(PhoneRegex(), PrivacyDataKind.PhoneNumber);
        AddMatches(ApiKeyRegex(), PrivacyDataKind.ApiKey);
        AddMatches(Ipv4Regex(), PrivacyDataKind.IpAddress, IsValidIpv4);
        AddMatches(IdentityRegex(), PrivacyDataKind.IdentityNumber,
            IsValidIdentityNumber);
        return;

        void AddMatches(
            Regex regex,
            PrivacyDataKind kind,
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

                candidates.Add(new PrivacyCandidate(kind, value, source.Bounds));
            }
        }
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
            return normalized.All(char.IsDigit);
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
