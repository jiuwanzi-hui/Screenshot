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

        if (IsShortAsciiIdentifier(text))
        {
            return true;
        }

        if (target == "zh")
        {
            var han = text.Count(IsHanCharacter);
            var kana = text.Count(character =>
                character is >= '\u3040' and <= '\u30ff');
            // A mixed Chinese/Latin UI label is already usable Chinese. Sending
            // the whole row to a model often corrupts identifiers such as
            // MySQL+PG_TD or Ubuntu+Debian while needlessly redrawing the Han text.
            return han >= 1 && kana == 0;
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

    private static bool IsShortAsciiIdentifier(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length is >= 1 and <= 3 &&
               letters.All(character => character is >= 'A' and <= 'Z');
    }
}
