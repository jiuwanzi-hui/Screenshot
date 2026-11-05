using System.Text.RegularExpressions;

namespace Screenshot.App.Text.Realtime;

internal sealed class RealtimeGlossary
{
    private readonly Dictionary<string, string> _terms = new(StringComparer.Ordinal);
    private readonly Regex? _pattern;
    internal RealtimeGlossary(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length is > 0 and <= 100 && parts[1].Length is > 0 and <= 160)
                _terms[parts[0]] = parts[1];
        }
        if (_terms.Count > 0)
            _pattern = new Regex(string.Join('|', _terms.Keys.OrderByDescending(x => x.Length).Select(term =>
                (char.IsAsciiLetterOrDigit(term[0]) ? "(?<![A-Za-z0-9_])" : "") + Regex.Escape(term) +
                (char.IsAsciiLetterOrDigit(term[^1]) ? "(?![A-Za-z0-9_])" : ""))),
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }
    internal RealtimeProtectedTerms Protect(IReadOnlyList<string> texts)
    {
        var prefix = "ZXQTERM";
        while (texts.Any(text => text.Contains(prefix, StringComparison.OrdinalIgnoreCase))) prefix += "X";
        var terms = new Dictionary<string, RealtimeProtectedTerms.Term>(StringComparer.Ordinal);
        var segments = texts.Select(text => _pattern?.Replace(text, match =>
        {
            if (!terms.TryGetValue(match.Value, out var term))
            {
                var number = terms.Count; var suffix = string.Empty;
                do { suffix = (char)('A' + number % 26) + suffix; number = number / 26 - 1; } while (number >= 0);
                term = new(prefix + suffix + "QXZ", match.Value, _terms[match.Value]);
                terms.Add(match.Value, term);
            }
            return term.Token;
        }) ?? text).ToArray();
        return new(segments, terms.Values.ToArray());
    }
    internal bool TryExact(string text, out string translated) => _terms.TryGetValue(text.Trim(), out translated!);
}

internal sealed class RealtimeProtectedTerms(string[] segments, RealtimeProtectedTerms.Term[] terms)
{
    internal sealed record Term(string Token, string Source, string Target);
    internal string[] Segments { get; } = segments;
    private readonly HashSet<string> _sourceLines = segments.SelectMany(segment => TranslationTextNormalizer.Normalize(segment).Split('\n'))
        .Select(line => line.Trim()).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
    internal string SourceHints => System.Text.Json.JsonSerializer.Serialize(terms.Select(term => new { placeholder = term.Token, meaning = term.Source }));
    internal string RestoreDetectionSource(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(_sourceLines.Contains)
            ? RestoreSource(text) : text;
    internal string RestoreSource(string text)
    {
        foreach (var term in terms) text = text.Replace(term.Token, term.Source, StringComparison.OrdinalIgnoreCase);
        return text;
    }
    internal bool TryRestore(int index, string translated, out string restored)
        => TryRestoreText(Segments[index], translated, out restored);
    internal bool TryResolveOnlyTerms(int index, out string translated)
    {
        var remaining = Segments[index];
        foreach (var term in terms) remaining = remaining.Replace(term.Token, string.Empty, StringComparison.Ordinal);
        translated = string.Empty;
        return remaining.Length < Segments[index].Length && !remaining.Any(char.IsLetter) &&
            TryRestore(index, Segments[index], out translated);
    }
    internal bool TryRestoreText(string source, string translated, out string restored)
    {
        restored = translated;
        foreach (var term in terms)
        {
            var expected = Count(source, term.Token);
            if (Count(translated, term.Token) != expected) { restored = string.Empty; return false; }
            restored = restored.Replace(term.Token, term.Target, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
    private static int Count(string text, string token)
    {
        var count = 0; var offset = 0;
        while ((offset = text.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; offset += token.Length; }
        return count;
    }
}
