using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal sealed class TranslationTermProtector
{
    private static readonly Regex LeadingTokenPattern = new(
        @"^(?<prefix>\s*[^A-Za-z0-9]*)(?<term>[A-Za-z][A-Za-z0-9+#_.-]*)(?<space>\s+)(?<rest>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<string?> _prefixes;

    private TranslationTermProtector(
        IReadOnlyList<string> segments,
        IReadOnlyList<string?> prefixes)
    {
        Segments = segments;
        _prefixes = prefixes;
    }

    public IReadOnlyList<string> Segments { get; }

    public static TranslationTermProtector Create(
        IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var leadingTerms = segments
            .Select(segment => LeadingTokenPattern.Match(segment))
            .Where(match => match.Success)
            .Select(match => match.Groups["term"].Value)
            .GroupBy(term => term, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        var protectedSegments = new string[segments.Count];
        var prefixes = new string?[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var match = LeadingTokenPattern.Match(segments[index]);
            if (!match.Success ||
                !ShouldProtect(match.Groups["term"].Value, leadingTerms))
            {
                protectedSegments[index] = segments[index];
                continue;
            }

            prefixes[index] = match.Groups["prefix"].Value +
                match.Groups["term"].Value +
                match.Groups["space"].Value;
            protectedSegments[index] = match.Groups["rest"].Value;
        }

        return new TranslationTermProtector(protectedSegments, prefixes);
    }

    public string Restore(int index, string translated)
    {
        if (index < 0 || index >= _prefixes.Count ||
            _prefixes[index] is not { } prefix)
        {
            return translated;
        }

        return prefix + translated.TrimStart();
    }

    private static bool ShouldProtect(
        string term,
        Dictionary<string, int> leadingTerms)
    {
        var hasInternalUppercase = term.Skip(1).Any(character =>
            character is >= 'A' and <= 'Z');
        if (hasInternalUppercase ||
            char.IsUpper(term[0]) &&
            term.Any(character => character is '.' or '+' or '#'))
        {
            return true;
        }

        return term.Length >= 10 &&
            leadingTerms.TryGetValue(term, out var occurrences) &&
            occurrences >= 2;
    }
}
