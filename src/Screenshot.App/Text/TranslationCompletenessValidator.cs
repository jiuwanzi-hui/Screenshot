using System.Globalization;
using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static partial class TranslationCompletenessValidator
{
    internal static string? Validate(string source, string translated, string targetLanguage)
    {
        if (Realtime.RealtimeTranslationContext.PrepareValidation(ref source, ref translated) is { } termFailure) return termFailure;
        if (string.IsNullOrWhiteSpace(translated)) return "翻译返回了空内容。";
        var target = TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage);
        if (target is "zh" or "zh_hant" &&
            (source.Count(char.IsLetter) >= 8 || TranslationTextNormalizer.ExpandCompoundLabel(source) is not null) &&
            !TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(source) &&
            !translated.Any(c => c is >= '\u3400' and <= '\u9fff'))
            return "模型未按要求返回中文译文，返回了其他语言";
        if (target is "zh" or "zh_hant" &&
            WithoutQuotedExamples(source, translated).Any(c => c is >= '\u3040' and <= '\u30ff'))
            return "译文仍含未翻译的日语，请重试或更换离线模型。";
        if (target is "zh" or "zh_hant" &&
            WithoutQuotedExamples(source, translated).Any(c => c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff'))
            return "译文混入韩语，未按要求完整返回中文，请重试或更换离线模型。";
        if (source.Trim() == translated.Trim() &&
            !TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(source, targetLanguage) &&
            !TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(source))
            return "模型原样返回了未翻译的内容。";
        if (source.Count(char.IsLetter) >= 8)
        {
            var hasTargetScript = target switch
            {
                "ru" => translated.Any(c => c is >= '\u0400' and <= '\u04ff'),
                "ar" => translated.Any(c => c is >= '\u0600' and <= '\u06ff'),
                "en" or "fr" or "es" => translated.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'),
                _ => true,
            };
            if (!hasTargetScript) return "模型返回的内容不是所选目标语言。";
        }
        // English, French and Spanish share a script. Reject confidently
        // identified wrong-language prose, while allowing ambiguous short labels.
        if (target is "en" or "fr" or "es" && translated.Count(char.IsLetter) >= 32)
        {
            var detection = Cld3OfflineLanguageDetector.Shared.Detect(translated);
            if (detection.IsSuccess && detection.ErrorMessage is null && detection.IsReliable &&
                detection.Confidence >= 0.95 && detection.LanguageCode != target)
                return "模型返回的内容不是所选目标语言。";
        }
        if (NumericDatePattern().IsMatch(source) || NumericDatePattern().IsMatch(translated))
        {
            source = NormalizeMonthNames(source);
            translated = NormalizeMonthNames(translated);
        }
        var sourceClock = NormalizeClockTimes(source);
        var translatedClock = NormalizeClockTimes(translated);
        if (sourceClock != source && translatedClock != translated)
        {
            source = sourceClock;
            translated = translatedClock;
        }
        if (!Numbers(NormalizeQuantities(source)).SequenceEqual(Numbers(NormalizeQuantities(translated))))
            return "模型改变或遗漏了原文数字，未将该结果作为完整译文，请重试或更换模型。";
        return null;
    }

    // Permit digit shaping, thousands separators, and zero-padded dates. Check
    // the multiset rather than order because valid translations reorder clauses.
    private static IEnumerable<string> Numbers(string text) => NumberPattern().Matches(text)
        .Select(match => string.Concat(match.Value.Where(char.IsDigit)
            .Select(c => ((int)char.GetNumericValue(c)).ToString(CultureInfo.InvariantCulture))).TrimStart('0'))
        .Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\p{Nd}{1,3}(?:\.\p{Nd}{3}){2,}(?!\p{Nd})|\p{Nd}+(?:[, \u00a0\u066c\u202f]\p{Nd}{3}(?!\p{Nd}))*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    // Compare quantities, not their locale-specific scale notation: 83 million,
    // 8300 万 and 0.83 亿 all describe the same number. Keep this separate from
    // bare numbers/dates so version numbers and identifiers are not reinterpreted.
    internal static string NormalizeQuantities(string text)
    {
        var matches = QuantityPattern().Matches(text);
        if (matches.Count == 0) return text;
        var output = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            output.Append(text.AsSpan(cursor, match.Index - cursor));
            if (!TryReadQuantity(match, out var value, out var scale))
            {
                output.Append(match.Value);
                cursor = match.Index + match.Length;
                continue;
            }
            var end = match.Index + match.Length;
            // Compound Chinese quantities are sums, not concatenated numbers:
            // 1亿2000万 = 120000000, 8千3百 = 8300.
            if (match.Groups[2].Value.Any(c => c is >= '\u3400' and <= '\u9fff'))
            {
                while (index + 1 < matches.Count)
                {
                    var next = matches[index + 1];
                    if (!string.IsNullOrWhiteSpace(text[end..next.Index]) ||
                        !next.Groups[2].Value.Any(c => c is >= '\u3400' and <= '\u9fff') ||
                        !TryReadQuantity(next, out var addition, out var nextScale) || nextScale >= scale)
                        break;
                    try { value += addition; }
                    catch (OverflowException) { break; }
                    scale = nextScale;
                    end = next.Index + next.Length;
                    index++;
                }
            }
            if (cursor == match.Index && output.Length > 0 && char.IsDigit(output[^1])) output.Append(' ');
            output.Append(value.ToString("0.############################", CultureInfo.InvariantCulture));
            cursor = end;
        }
        output.Append(text.AsSpan(cursor));
        return output.ToString();
    }

    private static bool TryReadQuantity(Match match, out decimal value, out decimal scale)
    {
        value = 0;
        scale = 1;
        var digits = string.Concat(match.Groups[1].Value.Select(c => char.IsDigit(c)
            ? (char)('0' + (int)char.GetNumericValue(c)) : c));
        digits = digits.Replace("\u066c", "", StringComparison.Ordinal).Replace("\u202f", "", StringComparison.Ordinal);
        // Commas with exactly three following digits are grouping separators.
        digits = Regex.Replace(digits, @",(?=\d{3}(?:\D|$))", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        digits = digits.Replace(',', '.').Replace('\u066b', '.');
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        scale = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "hundred" or "百" => 100m,
            "thousand" or "thousands" or "千" => 1000m,
            "万" or "萬" or "만" => 10000m,
            "million" or "millions" or "millionen" or "millón" or "millones" or
                "миллион" or "миллиона" or "миллионов" or "مليون" or "百万" or "百萬" => 1000000m,
            "亿" or "億" or "억" => 100000000m,
            "billion" or "billions" or "十亿" or "十億" => 1000000000m,
            "trillion" or "trillions" or "万亿" or "萬億" or "兆" => 1000000000000m,
            _ => 1m,
        };
        try { value *= scale; return true; }
        catch (OverflowException) { return false; }
    }

    [GeneratedRegex(@"(\p{Nd}+(?:[.,\u066b\u066c\u202f]\p{Nd}+)*)\s*((?:hundred|thousands?|millions?|millionen|millón|millones|миллион(?:а|ов)?|مليون|billions?|trillions?)(?!\p{L})|万亿|萬億|十亿|十億|百万|百萬|万|萬|亿|億|兆|百|千|만|억)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();

    private static string NormalizeClockTimes(string text) => ClockPattern().Replace(text, match =>
    {
        if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour > 23)
            return match.Value;
        var minute = match.Groups["minute"].Success
            ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59) return match.Value;
        var period = match.Groups["period"].Value.ToLowerInvariant().Replace(".", "", StringComparison.Ordinal);
        if (period.Length > 0)
        {
            if (hour > 12) return match.Value;
            hour %= 12;
            if (period is "pm" or "下午" or "晚上" or "中午" or "午後" or "오후" or "مساءً") hour += 12;
        }
        // Add a nonnumeric marker so a normalized 15:00 still counts as a
        // transformed expression and both sides opt into semantic comparison.
        return $"⟪time:{hour.ToString(CultureInfo.InvariantCulture)}:{minute.ToString(CultureInfo.InvariantCulture)}⟫";
    });

    [GeneratedRegex(@"(?<period>上午|下午|晚上|中午|早上|凌晨|午後|午前|오후|오전)\s*(?<hour>[0-9]{1,2})\s*[点點时時시](?:\s*(?<minute>[0-9]{1,2})\s*[分분])?|(?<![0-9])(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2}))?\s*(?<period>[ap]\.?m\.?|مساءً|صباحًا)(?!\p{L})|(?<![0-9])(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2})(?![0-9])|\s*h(?:eures?)?\b|\s*Uhr\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ClockPattern();

    [GeneratedRegex(@"\p{Nd}{2,4}\s*[-/.年]\s*\p{Nd}{1,2}\s*[-/.月]\s*\p{Nd}{1,4}|\p{Nd}{1,2}月\p{Nd}{1,2}日", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDatePattern();

    private static string WithoutQuotedExamples(string source, string translated)
    {
        // A Chinese explanation of Japanese grammar must retain the Japanese
        // examples it discusses. This exemption is limited to original quotes.
        if (!TranslationTargetLanguageMatcher.HasLatinNaturalLanguageClause(source)) return translated;
        var examples = QuotedExamplePattern().Matches(source)
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return QuotedExamplePattern().Replace(translated,
            match => examples.Contains(match.Groups[1].Value) ? string.Empty : match.Value);
    }

    [GeneratedRegex("""[「『“"]([^」』”"\r\n]{1,80})[」』”"]""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedExamplePattern();

    private static readonly string[] MonthCultures = ["en-US", "fr-FR", "es-ES", "ru-RU", "ar-EG"];
    private static readonly Dictionary<string, string> MonthNames = CreateMonthNames();
    private static readonly Regex MonthNamePattern = new(
        @"(?<!\p{L})(?:" + string.Join("|", MonthNames.Keys.OrderByDescending(name => name.Length).Select(Regex.Escape)) + @")(?!\p{L})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static Dictionary<string, string> CreateMonthNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cultureName in MonthCultures)
        {
            var format = CultureInfo.GetCultureInfo(cultureName).DateTimeFormat;
            for (var month = 1; month <= 12; month++)
            {
                var number = month.ToString(CultureInfo.InvariantCulture);
                foreach (var name in new[] { format.MonthNames[month - 1], format.MonthGenitiveNames[month - 1],
                             format.AbbreviatedMonthNames[month - 1], format.AbbreviatedMonthGenitiveNames[month - 1] })
                {
                    if (name.Length == 0) continue;
                    names.TryAdd(name, number);
                }
            }
        }
        return names;
    }

    private static string NormalizeMonthNames(string text) =>
        MonthNamePattern.Replace(text, match => MonthNames[match.Value]);
}
