namespace Screenshot.App.Text;

internal static class ArtisticOcrMerge
{
    internal static OcrRecognitionResult Merge(OcrRecognitionResult fast, OcrRecognitionResult enhanced)
    {
        if (!enhanced.IsSuccess || enhanced.Regions.Count == 0) return fast;
        if (fast.Regions.Count == 0) return enhanced;
        var kept = fast.Regions.ToList();
        var additions = new List<OcrTextRegion>();
        foreach (var candidate in enhanced.Regions)
        {
            // A text-only subsequence check treats a wrong Small character as
            // immutable. Use verified upright-column geometry and confidence,
            // retaining unrelated text and stronger Small results by position.
            if ((!candidate.RecoveredVerticalColumn && !candidate.VerifiedArtisticText) || candidate.RecognitionConfidence < .8f) continue;
            var overlapping = kept.Concat(additions).Where(region => Overlap(region, candidate) >= .55).ToArray();
            if (overlapping.Length > 0)
            {
                var weight = overlapping.Sum(region => Math.Max(1, region.Text.Length));
                var confidence = overlapping.Sum(region => region.RecognitionConfidence * Math.Max(1, region.Text.Length)) / weight;
                var originalText = string.Concat(overlapping.OrderBy(region => region.Y).Select(region => region.Text));
                // A new hypothesis must cover an old region before replacing
                // it; intersecting one end of a longer sentence is insufficient.
                if (overlapping.Any(region => Intersection(region, candidate) / (region.Width * region.Height) < .7)) continue;
                var addsMissingCharacters = HighQualityOcrService.IsCompleteSmallTextExpansion(originalText, candidate.Text);
                if (candidate.RecognitionConfidence < confidence + .08 &&
                    !(addsMissingCharacters && candidate.RecognitionConfidence >= confidence)) continue;
                foreach (var region in overlapping) { kept.Remove(region); additions.Remove(region); }
            }
            additions.Add(candidate);
        }
        if (additions.Count == 0) return fast;
        var regions = OcrReadingOrder.Sort(kept.Concat(additions));
        var words = WordsFor(fast, kept).Concat(WordsFor(enhanced, additions))
            .OrderBy(word => word.Y).ThenBy(word => word.X).ToArray();
        return fast with { Text = string.Join(Environment.NewLine, regions.Select(region => region.Text)),
            Regions = regions, Words = words };
    }

    private static IEnumerable<OcrWordRegion> WordsFor(OcrRecognitionResult result, IEnumerable<OcrTextRegion> regions) =>
        result.Words.Where(word => regions.Any(region =>
            word.X + word.Width / 2 >= region.X && word.X + word.Width / 2 <= region.X + region.Width &&
            word.Y + word.Height / 2 >= region.Y && word.Y + word.Height / 2 <= region.Y + region.Height));

    private static double Overlap(OcrTextRegion first, OcrTextRegion second)
        => Intersection(first, second) / Math.Max(1, Math.Min(first.Width * first.Height, second.Width * second.Height));

    private static double Intersection(OcrTextRegion first, OcrTextRegion second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return width * height;
    }
}
