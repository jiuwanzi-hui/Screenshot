namespace Screenshot.App.Text;

internal static class TranslationTargetLanguageMatcher
{
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

        if (target == "zh")
        {
            var han = text.Count(IsHanCharacter);
            var kana = text.Count(character =>
                character is >= '\u3040' and <= '\u30ff');
            return han >= 2 && kana == 0 && han >= letters * 0.5;
        }

        if (target == "en")
        {
            var latin = text.Count(character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            return latin >= 4 && latin >= letters * 0.8;
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
}
