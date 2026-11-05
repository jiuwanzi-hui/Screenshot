using System.Globalization;
using System.Text;

namespace Screenshot.App.Editor;

public enum NumberingStyle
{
    Digits,
    UpperLetters,
    LowerLetters,
    UpperRoman,
    LowerRoman,
}

internal static class AnnotationNumbering
{
    internal static int Maximum(NumberingStyle style) =>
        style is NumberingStyle.UpperRoman or NumberingStyle.LowerRoman ? 3999 : 9999;

    internal static string Format(int value, NumberingStyle style)
    {
        value = Math.Clamp(value, 1, Maximum(style));
        if (style == NumberingStyle.Digits) return value.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        if (style is NumberingStyle.UpperLetters or NumberingStyle.LowerLetters)
        {
            while (value > 0)
            {
                value--;
                text.Insert(0, (char)('A' + value % 26));
                value /= 26;
            }
        }
        else
        {
            foreach (var (amount, symbol) in RomanSymbols)
                while (value >= amount)
                {
                    text.Append(symbol);
                    value -= amount;
                }
        }
        return style is NumberingStyle.LowerLetters or NumberingStyle.LowerRoman
            ? text.ToString().ToLowerInvariant() : text.ToString();
    }

    private static readonly (int Amount, string Symbol)[] RomanSymbols =
    [ (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
      (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") ];

    internal static bool TryParse(string text, NumberingStyle style, out int value)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value >= 1 && value <= Maximum(style);
        if (text.Length is 0 or > 15) return false;
        text = text.ToUpperInvariant();
        value = 0;
        if (style is NumberingStyle.UpperLetters or NumberingStyle.LowerLetters)
        {
            foreach (var letter in text)
            {
                if (letter is < 'A' or > 'Z') return false;
                value = value * 26 + letter - 'A' + 1;
                if (value > Maximum(style)) return false;
            }
            return value > 0;
        }
        if (style is not (NumberingStyle.UpperRoman or NumberingStyle.LowerRoman)) return false;
        var remaining = text;
        foreach (var (amount, symbol) in RomanSymbols)
            while (remaining.StartsWith(symbol, StringComparison.Ordinal))
            {
                value += amount;
                remaining = remaining[symbol.Length..];
            }
        return remaining.Length == 0 && value >= 1 && value <= Maximum(style) &&
            Format(value, NumberingStyle.UpperRoman) == text;
    }
}
