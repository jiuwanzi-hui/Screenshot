using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static partial class TranslationTextNormalizer
{
    // OCR line grouping may insert an ASCII space in the middle of a Japanese
    // word (選 手, バドミン トン). Remove only such CJK-to-CJK joins; Latin
    // prose, numbers and paragraph boundaries retain their spacing. Selected
    // compound labels get word boundaries in the request copy below.
    internal static string Normalize(string text)
    {
        var normalized = TranslationCompletenessValidator.NormalizeQuantities(
            CjkWordBreak().Replace(text, string.Empty));
        // Only expand standalone labels, including punctuation and file suffixes.
        // OCR text, coordinates, paths and names inside prose are not rewritten.
        var expanded = ExpandCompoundLabel(normalized);
        return expanded is null ? normalized : normalized.Replace(normalized.Trim(), expanded, StringComparison.Ordinal);
    }

    internal static string? ExpandCompoundLabel(string text)
    {
        var value = text.Trim();
        var start = 0;
        var end = value.Length;
        while (start < end && IsLabelPunctuation(value[start])) start++;
        while (end > start && IsLabelPunctuation(value[end - 1])) end--;
        if (start == end) return null;
        var label = value[start..end];
        // A standalone filename's descriptive stem can be translated, while
        // its extension remains in the request. Full paths/URLs/handles fail
        // the token checks below and retain their existing protection.
        var suffix = string.Empty;
        var dot = label.LastIndexOf('.');
        if (dot > 0 && label.Length - dot is >= 2 and <= 11 &&
            label.AsSpan(dot + 1).ToArray().All(char.IsAsciiLetterOrDigit))
        {
            suffix = label[dot..];
            label = label[..dot];
        }

        var parts = label.Split(['_', '-'], StringSplitOptions.None);
        if (parts.Any(part => part.Length < 2 || !char.IsAsciiLetter(part[0]) ||
            part.Any(c => !char.IsAsciiLetterOrDigit(c)))) return null;
        var expanded = parts.Select(ExpandCamelCaseToken).ToArray();
        // Preserve a brand prefix in an already separated descriptive label
        // (AcmeTool-User-Guide). Its following words are already translatable;
        // splitting the brand itself here would lose its spelling.
        if (parts.Length > 1 && char.IsAsciiLetterUpper(parts[0][0]) && expanded[0] is not null &&
            expanded.Skip(1).All(part => part is null)) return value;
        // Separators provide word boundaries even when OCR loses capitalization.
        // A single all-uppercase acronym remains protected.
        if (parts.Length == 1 && expanded[0] is null) return null;
        return value[..start] + string.Join(' ', parts.Select((part, index) => expanded[index] ?? part)) +
            suffix + value[end..];
    }

    private static bool IsLabelPunctuation(char value) => char.IsWhiteSpace(value) ||
        value is '(' or ')' or '[' or ']' or '（' or '）' or '【' or '】' or
            '\'' or '"' or '`' or '“' or '”' or '‘' or '’' or '.' or ',' or ':' or ';' or '!' or '?' or
            '。' or '，' or '：' or '；' or '！' or '？';

    private static string? ExpandCamelCaseToken(string value)
    {
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        var words = new List<string>();
        var start = 0;
        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiLetterUpper(value[index])) continue;
            // userName -> user Name; HTTPServer -> HTTP Server (not H T T P).
            if (char.IsAsciiLetterLower(value[index - 1]) || char.IsAsciiDigit(value[index - 1]) ||
                char.IsAsciiLetterUpper(value[index - 1]) && index + 1 < value.Length &&
                char.IsAsciiLetterLower(value[index + 1]))
            {
                words.Add(value[start..index]);
                start = index;
            }
        }
        if (words.Count == 0) return null;
        words.Add(value[start..]);
        return string.Join(' ', words);
    }

    [GeneratedRegex(@"(?<=[\u3040-\u30ff\u3400-\u9fff])[ \t]+(?=[\u3040-\u30ff\u3400-\u9fff])", RegexOptions.CultureInvariant)]
    private static partial Regex CjkWordBreak();
}
