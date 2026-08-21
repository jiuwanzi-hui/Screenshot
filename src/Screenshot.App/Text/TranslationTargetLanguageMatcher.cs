namespace Screenshot.App.Text;

internal static class TranslationTargetLanguageMatcher
{
    private static readonly HashSet<string> EnglishFunctionWords = new(
        [
            "a", "an", "and", "are", "as", "at", "be", "been", "but",
            "by", "can", "do", "for", "from", "has", "have", "in", "is",
            "it", "not", "of", "on", "or", "our", "so", "that", "the",
            "their", "this", "to", "was", "we", "what", "when", "where",
            "which", "will", "with", "you", "your",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsAlreadyTargetLanguage(
        string text,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var target = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        if (target is null)
        {
            return false;
        }

        var letters = text.Count(char.IsLetter);
        if (letters == 0)
        {
            return true;
        }

        if (IsLikelyInvariant(text))
        {
            return true;
        }

        if (target == "zh")
        {
            var han = text.Count(IsHanCharacter);
            var kana = text.Count(character =>
                character is >= '\u3040' and <= '\u30ff');
            // OCR often groups Chinese and English from the same visual row
            // (for example “服务状态 Service is running”). Treating any row
            // containing one Han character as already translated silently
            // drops the English part. Only skip rows where Han is clearly the
            // dominant script; mixed/English-heavy rows are sent to the
            // translator, which can preserve the embedded Chinese and product
            // identifiers while translating the English words.
            if (kana != 0)
            {
                return false;
            }

            // Keep isolated Han glyphs and technical identifiers (for example
            // MySQL+PG_TD) out of the translation request. They are not prose,
            // even when the identifier also contains a Chinese suffix.
            if ((han == 1 && letters <= 2) ||
                (han > 0 && text.Any(character => character is '+' or '_' or '/' or '\\')))
            {
                return true;
            }

            return han > 0 && !HasLatinNaturalLanguageClause(text);
        }

        if (target == "en")
        {
            var latin = text.Count(character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            return latin >= 1 && latin >= letters * 0.8;
        }

        var detection = Cld3OfflineLanguageDetector.Shared.Detect(text);
        return detection.IsSuccess && string.Equals(
            detection.LanguageCode,
            target,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHanCharacter(char value) =>
        value is >= '\u3400' and <= '\u4dbf' or
            >= '\u4e00' and <= '\u9fff' or
            >= '\uf900' and <= '\ufaff';

    internal static bool HasLatinNaturalLanguageClause(string text)
    {
        var words = GetLatinWords(text);
        return words.Length >= 3 &&
            (words.Any(EnglishFunctionWords.Contains) || words.Length >= 5);
    }

    internal static bool IsAmbiguousShortLabel(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value.Length > 24 ||
            value.Any(character => character is '.' or ',' or ';' or ':' or
                '!' or '?' or '。' or '，' or '；' or '：' or '！' or '？'))
        {
            return false;
        }

        var words = GetLatinWords(value);
        if (words.Length == 0 || words.Length > 2 ||
            words.Any(EnglishFunctionWords.Contains))
        {
            return false;
        }

        return words.All(word =>
            word.Length <= 4 || char.IsUpper(word[0]));
    }

    internal static bool IsLikelyInvariant(string text)
    {
        var value = text.Trim();
        if (value.Length <= 1 || !value.Any(char.IsLetter))
        {
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out _) ||
            value.Contains('@') || value.Contains('\\'))
        {
            return true;
        }

        var firstToken = value.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        if ((firstToken.Contains('/') &&
             !string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(firstToken))) ||
            (!value.Any(char.IsWhiteSpace) && value.Contains('/')) ||
            (!value.Any(char.IsWhiteSpace) && HasFileLikeExtension(value)))
        {
            return true;
        }

        if (LooksLikeMeasurement(value) || LooksLikeProductVersion(value) ||
            LooksLikeSourceCode(value) || value.EndsWith('>'))
        {
            return true;
        }

        var latinWords = GetLatinWords(value);
        if (latinWords.Length == 0)
        {
            return false;
        }

        // Acronyms and API names are common inside ordinary prose. They make
        // the token invariant, not the whole OCR row (for example a sentence
        // mentioning IP, SDK, PATH, or .NET still needs translation).
        if (HasLatinNaturalLanguageClause(value))
        {
            return false;
        }

        if (latinWords.Length <= 3 && latinWords.Any(word =>
                word.Length >= 2 && word.All(char.IsUpper)))
        {
            return true;
        }

        return !value.Any(char.IsWhiteSpace) &&
            (latinWords[0].Length <= 3 || LooksLikeCodeIdentifier(latinWords[0]));
    }

    private static string[] GetLatinWords(string text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (IsAsciiLatinLetter(character))
            {
                word.Append(character);
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words.ToArray();
    }

    private static bool LooksLikeMeasurement(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var firstLetter = -1;
        for (var index = 0; index < compact.Length; index++)
        {
            if (IsAsciiLatinLetter(compact[index]))
            {
                firstLetter = index;
                break;
            }
        }

        if (firstLetter <= 0 || !compact[..firstLetter].All(character =>
                char.IsDigit(character) || character is '.' or '+' or '-'))
        {
            return false;
        }

        return compact[firstLetter..].All(character =>
            IsAsciiLatinLetter(character) || character is '/' or '%');
    }

    private static bool HasFileLikeExtension(string value)
    {
        var extension = System.IO.Path.GetExtension(value);
        return extension.Length is >= 2 and <= 11 &&
            extension.AsSpan(1).ToArray().All(char.IsLetterOrDigit);
    }

    private static bool LooksLikeProductVersion(string value)
    {
        var tokens = value.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 2 &&
            tokens[0].Any(IsAsciiLatinLetter) &&
            tokens[1].Any(char.IsDigit) &&
            tokens[1].All(character =>
                char.IsDigit(character) || character is '.' or '-' or '_');
    }

    private static bool LooksLikeSourceCode(string value)
    {
        var code = value.TrimStart('/', '*', ' ', '\t');
        if (code.StartsWith('<') && code.EndsWith('>'))
        {
            return true;
        }

        string[] declarations =
        [
            "using ", "namespace ", "class ", "record ", "struct ",
            "interface ", "enum ", "public ", "private ", "protected ",
            "internal ", "static ", "const ", "readonly ", "var ",
            "let ", "function ", "def ", "import ", "from ", "package ",
        ];
        return declarations.Any(prefix =>
                   code.StartsWith(prefix, StringComparison.Ordinal)) &&
               (code.EndsWith(';') || code.Contains('=') || code.Contains('{'));
    }

    private static bool LooksLikeCodeIdentifier(string value)
    {
        if (value.Contains("::", StringComparison.Ordinal) || value.Contains('_'))
        {
            return true;
        }

        return value.Skip(1).Any(character =>
            character is >= 'A' and <= 'Z');
    }

    private static bool IsAsciiLatinLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
