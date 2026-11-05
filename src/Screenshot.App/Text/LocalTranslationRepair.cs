using System.Text.RegularExpressions;

namespace Screenshot.App.Text;

internal static partial class LocalTranslationRepair
{
    internal static TranslationSegmentsResult MergeRetry(IReadOnlyList<string> source,
        TranslationSegmentsResult previous, IReadOnlyList<int> requestedIndices,
        TranslationSegmentsResult retry, string targetLanguage)
    {
        var output = previous.Segments.ToArray();
        var failed = new List<int>();
        for (var i = 0; i < requestedIndices.Count; i++)
        {
            var index = requestedIndices[i];
            if (retry.IsSuccess && retry.Segments.Count == requestedIndices.Count &&
                retry.FailedSegmentIndices?.Contains(i) != true &&
                TranslationCompletenessValidator.Validate(source[index], retry.Segments[i], targetLanguage) is null)
                output[index] = retry.Segments[i];
            else failed.Add(index);
        }
        return new TranslationSegmentsResult(true, output, failed.Count == 0 ? null :
            $"仍有 {failed.Count} 段未完成，已保留原文。" + retry.ErrorMessage)
        {
            FailedSegmentIndices = failed,
            Notice = string.Join("\n", new[] { previous.Notice, retry.Notice }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
        };
    }

    // Only used after a failed quality check. Keep successful paragraphs and
    // the trained model prompt intact; shorter sentences reduce omissions.
    internal static IReadOnlyList<string> SplitForRepair(string text)
    {
        var sentences = SentenceBoundary().Split(text).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return sentences.SelectMany(sentence => LocalLargeModelTranslationProvider.SplitText(sentence, 220)).ToArray();
    }

    internal static async Task<string?> RetryAsync(string source, string targetLanguage,
        Func<string, CancellationToken, Task<string>> translate, CancellationToken token)
    {
        var pieces = SplitForRepair(source);
        // Repeating an identical deterministic request cannot repair a short
        // sentence. Leave that case to the other installed offline engine.
        if (pieces.Count < 2) return null;
        var translated = new List<string>(pieces.Count);
        foreach (var piece in pieces)
        {
            token.ThrowIfCancellationRequested();
            var result = await translate(piece, token).ConfigureAwait(false);
            if (TranslationCompletenessValidator.Validate(piece, result, targetLanguage) is not null)
                return null;
            translated.Add(result);
        }
        var combined = string.Join(" ", translated);
        return TranslationCompletenessValidator.Validate(source, combined, targetLanguage) is null ? combined : null;
    }

    [GeneratedRegex(@"(?<=[。！？])\s*|(?<=[.!?])\s+(?=[\p{Lu}\p{Lo}])|[\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundary();
}
