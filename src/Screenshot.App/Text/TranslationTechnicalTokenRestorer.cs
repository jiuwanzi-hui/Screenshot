using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static class TranslationTechnicalTokenRestorer
{
    private static readonly Regex TokenPattern = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z][A-Za-z0-9+#_.-]*)(?:/[A-Za-z0-9+#_.-]+)*(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Restore(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        var sourceTokens = TokenPattern.Matches(source)
            .Select(match => match.Value.TrimEnd('.'))
            .Where(IsTechnicalToken)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(token => token.Length)
            .ToArray();
        var sourceTokenSet = sourceTokens.ToHashSet(StringComparer.Ordinal);
        var result = translated;
        foreach (var sourceToken in sourceTokens)
        {
            if (ContainsToken(result, sourceToken))
            {
                continue;
            }

            var candidate = TokenPattern.Matches(result)
                .Select(match => match.Value.TrimEnd('.'))
                .Where(token => !string.Equals(
                    token,
                    sourceToken,
                    StringComparison.Ordinal) &&
                    !sourceTokenSet.Contains(token))
                .Select(token => new
                {
                    Token = token,
                    Distance = EditDistance(
                        token.ToUpperInvariant(),
                        sourceToken.ToUpperInvariant()),
                })
                .Where(item => item.Distance > 0 &&
                    item.Distance <= Math.Max(1, sourceToken.Length / 6))
                .OrderBy(item => item.Distance)
                .ThenBy(item => Math.Abs(item.Token.Length - sourceToken.Length))
                .FirstOrDefault();
            if (candidate is null)
            {
                continue;
            }

            result = Regex.Replace(
                result,
                $@"(?<![A-Za-z0-9]){Regex.Escape(candidate.Token)}(?![A-Za-z0-9])",
                sourceToken,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        return result;
    }

    private static bool ContainsToken(string text, string token)
    {
        return Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    private static bool IsTechnicalToken(string token)
    {
        var letters = token.Where(char.IsLetter).ToArray();
        if (letters.Length < 3)
        {
            return false;
        }

        return char.IsUpper(token[0]) && token.Length >= 5 ||
            letters.All(character => character is >= 'A' and <= 'Z') ||
            letters.Skip(1).Any(character => character is >= 'A' and <= 'Z') ||
            token.Any(character => character is '/' or '_' or '+' or '#' or '-') ||
            token.Contains('.');
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] +
                    (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
