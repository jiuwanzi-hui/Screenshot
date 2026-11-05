namespace Screenshot.App.Text.Realtime;

internal static class RealtimeParagraphLayout
{
    internal static IReadOnlyList<OcrTextRegion> Group(IReadOnlyList<OcrTextRegion> regions)
    {
        var paragraphs = TranslationPresentationLayout.GroupParagraphs(regions).ToList();
        // Infographic cards often separate a large "75%" from "of developers…"
        // by typography/background. Sending the incomplete clause to a model
        // invites an invented percentage. Preserve the actual adjacent number.
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var body = paragraphs[i];
            if (i == 0 || !body.Text.StartsWith("of ", StringComparison.Ordinal)) continue;
            var index = paragraphs.FindLastIndex(i - 1, i, headline =>
            {
                var value = headline.Text.Trim();
                var gap = body.Y - (headline.Y + headline.Height);
                return value.EndsWith('%') && value.Any(char.IsDigit) &&
                    value.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || c is '%' or '.' or ',') &&
                    gap >= 0 && gap <= headline.Height * 3 &&
                    Math.Abs(body.X - headline.X) <= Math.Max(12, body.EstimatedFontSize) &&
                    headline.X + headline.Width <= body.X + body.Width;
            });
            if (index < 0) continue;
            var prefix = paragraphs[index];
            var left = Math.Min(prefix.X, body.X);
            paragraphs[i] = body with { Text = prefix.Text.Trim() + " " + body.Text, X = left, Y = prefix.Y,
                Width = body.X + body.Width - left, Height = body.Y + body.Height - prefix.Y };
            paragraphs.RemoveAt(index); i--;
        }
        return paragraphs;
    }
}
