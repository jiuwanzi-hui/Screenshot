namespace Screenshot.App.Text;

internal static class TranslationTargetLanguageMatcher
{
    // Product names embedded in Chinese prose can remain unchanged. Do not
    // generalize this to every capitalized English word (Host, Core, Settings).
    private static readonly HashSet<string> EmbeddedProductNames = new(
        ["Windows", "Mac", "Linux", "Ubuntu", "Debian", "Word", "Excel", "PowerPoint"],
        StringComparer.Ordinal);
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
            // Preserve mixed-language rows even when OCR groups the scripts.
            if (kana != 0)
            {
                return false;
            }

            // A short mixed label such as “OCR Host 和 Core。” still contains
            // words to translate. Sentence length and function words cannot
            // establish that its English content is already Chinese.
            return han > 0 && !HasTranslatableLatinWords(text);
        }

        var detection = Cld3OfflineLanguageDetector.Shared.Detect(TranslationTextNormalizer.Normalize(text));
        // Sharing the Latin alphabet does not make French or Spanish English.
        // Only a reliable language match may suppress a translation request.
        return detection.IsSuccess && detection.ErrorMessage is null &&
            detection.IsReliable && detection.Confidence >= 0.85 && string.Equals(
            detection.LanguageCode,
            target,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldTranslate(string text, string sourceLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter) || IsLikelyInvariant(text))
        {
            return false;
        }

        var source = TranslationLanguageCatalog.NormalizeOfflineCode(sourceLanguage);
        var target = TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage);
        // Explicit source selection also disambiguates Han-only Japanese titles
        // and short phrases which a statistical detector cannot identify.
        return source is not null && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
            !IsAlreadyTargetLanguage(text, targetLanguage);
    }

    internal static int[] GetPendingSegmentIndices(IReadOnlyList<string> segments, string sourceLanguage, string targetLanguage)
    {
        var source = TranslationLanguageCatalog.NormalizeOfflineCode(sourceLanguage);
        var target = TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage);
        // Han-only headings on a Japanese page must not be mistaken for Chinese.
        // A browser translation icon (for example “aあ”) is not Japanese
        // page context. It must not cause already-Chinese navigation to be sent
        // to the model again; actual Japanese phrases still supply context.
        var hasJapaneseContext = source is null && segments.Any(text => text.Count(c => c is >= '\u3040' and <= '\u30ff') >= 2);
        var pending = Enumerable.Range(0, segments.Count)
            .Where(index => ShouldTranslate(segments[index],
                hasJapaneseContext && segments[index].Any(IsHanCharacter) &&
                !segments[index].Any(c => char.IsLetter(c) && c <= '\u024f') ? "ja" : sourceLanguage,
                targetLanguage)).ToArray();
        if (source is not null || target is null || pending.Length == 0) return pending;

        // A short title alone is often uncertain even when its detector agrees
        // with the target. Use a confidently identified body paragraph as context;
        // never override a different detected language or a different script.
        var hasTargetContext = segments.Where(text => text.Count(char.IsLetter) >= 64)
            .Select(text => Cld3OfflineLanguageDetector.Shared.Detect(text))
            .Any(detection => detection.ErrorMessage is null && detection.IsReliable &&
                detection.Confidence >= .98 && detection.LanguageCode == target);
        if (!hasTargetContext) return pending;
        return pending.Where(index =>
        {
            var text = segments[index];
            if (text.Length > 80 || target is not ("en" or "fr" or "es" or "de" or "it" or "pt" or "nl") ||
                text.Any(c => char.IsLetter(c) && c > '\u024f')) return true;
            var detection = Cld3OfflineLanguageDetector.Shared.Detect(text);
            return detection.LanguageCode != target || detection.Confidence < .35;
        }).ToArray();
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

    internal static bool HasTranslatableLatinWords(string text) => GetLatinWords(text)
        .Any(word => word.Length > 1 && !LooksLikeCodeIdentifier(word) &&
            !EmbeddedProductNames.Contains(word) && word.Any(char.IsLower));

    internal static bool IsAmbiguousShortLabel(string text)
    {
        var value = text.Trim();
        if (value.Any(IsHanCharacter) && HasTranslatableLatinWords(value)) return false;
        // A selected compound label must not be silently echoed just because
        // other paragraphs in the batch translated successfully.
        if (TranslationTextNormalizer.ExpandCompoundLabel(value) is not null) return false;
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

        // Selected compound labels are language-bearing even when OCR includes
        // wrappers, naming separators or a file suffix. Check before URI/file
        // heuristics: a trailing colon can otherwise look like a URI scheme.
        if (TranslationTextNormalizer.ExpandCompoundLabel(value) is not null) return false;

        if (IsStandaloneResourceReference(value))
        {
            return true;
        }

        // A brand joined to ordinary words is still a translatable label
        // (including versioned/truncated download names). The first CamelCase
        // token must not exempt every word following a hyphen.
        if (value.Contains('-') && !value.Contains('/') && !value.Contains('@'))
        {
            var label = HasFileLikeExtension(value)
                ? value[..^System.IO.Path.GetExtension(value).Length]
                : value;
            var labelWords = GetLatinWords(label);
            if (labelWords.Length >= 2 && labelWords.Any(word =>
                    word.Length > 2 && !LooksLikeCodeIdentifier(word)))
                return false;
        }

        // An @-mention can appear inside an ordinary sentence (for example
        // "I @UseListary every day"). Protect standalone handles, but do not
        // discard the surrounding prose from translation.
        if (value.Contains('@') && !value.Any(char.IsWhiteSpace))
        {
            return true;
        }

        if (!value.Any(char.IsWhiteSpace) && HasFileLikeExtension(value))
        {
            return true;
        }

        if (LooksLikeMeasurement(value) || LooksLikeProductVersion(value) ||
            value.StartsWith('<') && value.EndsWith('>'))
        {
            return true;
        }

        // A trailing attribution such as “（編集WB）” or an embedded CPU/API
        // acronym does not make an entire Japanese, Chinese, Russian or Arabic
        // paragraph invariant. Preserve standalone identifiers, not their prose.
        if (value.Count(character => char.IsLetter(character) && character > '\u024f') >= 4)
        {
            return false;
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

        if (latinWords.Length <= 3 && latinWords.Any(word => word.Length >= 2 && word.All(char.IsUpper)) &&
            latinWords.All(word => word.All(char.IsUpper) || LooksLikeCodeIdentifier(word)))
        {
            return true;
        }

        return latinWords.Length == 1 && !value.Any(char.IsWhiteSpace) &&
            LooksLikeCodeIdentifier(latinWords[0]);
    }

    private static bool IsStandaloneResourceReference(string text)
    {
        // A resource must occupy the whole selection. Uri.TryCreate alone also
        // accepts prose such as "Status: installation failed", and a backslash
        // inside a paragraph only protects the path, not the surrounding words.
        var value = text;
        if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0])
            value = value[1..^1];
        if (!value.Any(char.IsWhiteSpace))
            return value.Contains('\\') || value.Contains('/') ||
                value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

        // File-list annotations such as "src/file.cs +2240" are not prose.
        // Any letters after the reference must go through language detection.
        var firstSpace = value.IndexOfAny([' ', '\t']);
        if (firstSpace > 0 && value[..firstSpace].Any(c => c is '\\' or '/') &&
            !value[firstSpace..].Any(char.IsLetter)) return true;

        // Spaces are legal in directory names. Only rooted/explicit relative
        // paths can use this exception; reject prose within each component.
        // Check components separately so "D:\Program Files\My App\model.bin"
        // does not look like a sentence merely because it has many words.
        var drivePath = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
            value[2] is '\\' or '/';
        var rootedPath = drivePath || value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.StartsWith('/') || value.StartsWith(@".\", StringComparison.Ordinal) ||
            value.StartsWith(@"..\", StringComparison.Ordinal) ||
            value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal);
        if (!rootedPath || value.Any(c => c is '\r' or '\n' or '\t' or '"' or '<' or '>' or '|' or '?' or '*'))
            return false;

        var path = drivePath ? value[3..] : value;
        return !path.Contains(':') && path.Split(['\\', '/']).All(component =>
            !component.Any(char.IsWhiteSpace) ||
            !HasLatinNaturalLanguageClause(component) &&
            !component.Any(c => char.IsPunctuation(c) && c is not '-' and not '_') &&
            !component.Any(c => char.IsLetter(c) && c > '\u024f'));
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

        // A count such as "1 Comment" is prose, not a measurement unit.
        return compact[firstLetter..].ToLowerInvariant() is
            "px" or "pt" or "em" or "rem" or "dpi" or "hz" or "khz" or "mhz" or "ghz" or
            "fps" or "ms" or "s" or "min" or "h" or "mm" or "cm" or "m" or "km" or
            "mg" or "g" or "kg" or "kb" or "mb" or "gb" or "tb" or "kib" or "mib" or
            "gib" or "tib" or "kbps" or "mbps" or "gbps" or "mb/s" or "gb/s" or "km/h";
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
