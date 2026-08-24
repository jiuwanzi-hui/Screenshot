namespace Screenshot.App.Text;

public sealed record TranslationLanguage(
    string Tag,
    string OfflineCode,
    string DisplayName,
    bool CanBeOfflineTarget = true);

public static class TranslationLanguageCatalog
{
    // Mozilla Firefox Translations currently routes its multilingual Bergamot
    // models through English. Languages without an English -> language model
    // remain valid source languages, but cannot be selected as offline targets.
    public static IReadOnlyList<TranslationLanguage> Languages { get; } =
    [
        new("zh-Hans", "zh", "简体中文"),
        new("zh-Hant", "zh_hant", "繁體中文"),
        new("en", "en", "English"),
        new("ja", "ja", "日本語"),
        new("ko", "ko", "한국어"),
        new("fr", "fr", "Français"),
        new("de", "de", "Deutsch"),
        new("es", "es", "Español"),
        new("ru", "ru", "Русский"),
        new("ar", "ar", "العربية"),
        new("az", "az", "Azərbaycanca"),
        new("bg", "bg", "Български"),
        new("bn", "bn", "বাংলা"),
        new("bs", "bs", "Bosanski"),
        new("ca", "ca", "Català"),
        new("cs", "cs", "Čeština"),
        new("da", "da", "Dansk"),
        new("el", "el", "Ελληνικά"),
        new("et", "et", "Eesti"),
        new("eu", "eu", "Euskara"),
        new("fa", "fa", "فارسی"),
        new("fi", "fi", "Suomi"),
        new("gl", "gl", "Galego"),
        new("gu", "gu", "ગુજરાતી"),
        new("he", "he", "עברית"),
        new("hi", "hi", "हिन्दी"),
        new("hr", "hr", "Hrvatski"),
        new("hu", "hu", "Magyar"),
        new("id", "id", "Bahasa Indonesia"),
        new("is", "is", "Íslenska"),
        new("it", "it", "Italiano"),
        new("kn", "kn", "ಕನ್ನಡ"),
        new("lt", "lt", "Lietuvių"),
        new("lv", "lv", "Latviešu"),
        new("ml", "ml", "മലയാളം"),
        new("ms", "ms", "Bahasa Melayu"),
        new("nb", "nb", "Norsk bokmål"),
        new("nl", "nl", "Nederlands"),
        new("no", "no", "Norsk"),
        new("pl", "pl", "Polski"),
        new("pt", "pt", "Português"),
        new("ro", "ro", "Română"),
        new("sk", "sk", "Slovenčina"),
        new("sl", "sl", "Slovenščina"),
        new("sq", "sq", "Shqip"),
        new("sr", "sr", "Српски"),
        new("sv", "sv", "Svenska"),
        new("ta", "ta", "தமிழ்"),
        new("te", "te", "తెలుగు"),
        new("th", "th", "ไทย"),
        new("tr", "tr", "Türkçe"),
        new("uk", "uk", "Українська"),
        new("vi", "vi", "Tiếng Việt"),
        new("be", "be", "Беларуская", CanBeOfflineTarget: false),
        new("hbs", "hbs", "Srpskohrvatski", CanBeOfflineTarget: false),
        new("mr", "mr", "मराठी", CanBeOfflineTarget: false),
        new("nn", "nn", "Norsk nynorsk", CanBeOfflineTarget: false),
        new("ur", "ur", "اردو", CanBeOfflineTarget: false),
    ];

    public static IReadOnlyList<TranslationLanguage> OfflineTargetLanguages { get; } =
        Languages.Where(language => language.CanBeOfflineTarget).ToArray();

    public static IReadOnlyList<string> OfflineSourceCodes { get; } = Languages
        .Select(language => language.OfflineCode)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool IsSupportedSource(string? languageCode)
    {
        var code = NormalizeOfflineCode(languageCode);
        return code is not null && OfflineSourceCodes.Contains(
            code,
            StringComparer.OrdinalIgnoreCase);
    }

    public static string? NormalizeOfflineCode(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = languageTag.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.StartsWith("zh-hant", StringComparison.Ordinal) ||
            normalized is "zh-tw" or "zh-hk" or "zh-mo")
        {
            return "zh_hant";
        }

        if (normalized.StartsWith("zh", StringComparison.Ordinal))
        {
            return "zh";
        }

        var baseCode = normalized.Split('-', 2)[0];
        return baseCode switch
        {
            "iw" => "he",
            "in" => "id",
            "no" when normalized.Contains("nynorsk", StringComparison.Ordinal) => "nn",
            _ => baseCode,
        };
    }

    public static string GetDisplayName(string? languageTag)
    {
        var code = NormalizeOfflineCode(languageTag);
        return Languages.FirstOrDefault(language =>
                   string.Equals(
                       language.OfflineCode,
                       code,
                       StringComparison.OrdinalIgnoreCase))?.DisplayName ??
               languageTag?.Trim() ??
               "未知语言";
    }

    public static IReadOnlyList<string> BuildRoute(
        string sourceLanguage,
        string targetLanguage)
    {
        var source = NormalizeOfflineCode(sourceLanguage);
        var target = NormalizeOfflineCode(targetLanguage);
        if (source is null || target is null)
        {
            return [];
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return source == "en" || target == "en"
            ? [$"{source}-{target}"]
            : [$"{source}-en", $"en-{target}"];
    }

    public static IReadOnlyList<string> BuildAutoDetectPackDirections(
        string targetLanguage)
    {
        var target = NormalizeOfflineCode(targetLanguage);
        if (target is null)
        {
            return [];
        }

        var directions = OfflineSourceCodes
            .Where(source =>
                !string.Equals(source, "en", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            .Select(source => $"{source}-en")
            .ToList();
        if (!string.Equals(target, "en", StringComparison.OrdinalIgnoreCase))
        {
            directions.Add($"en-{target}");
        }

        return directions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
