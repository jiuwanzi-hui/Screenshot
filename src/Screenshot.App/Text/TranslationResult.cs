namespace Screenshot.App.Text;

public sealed record TranslationResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public string? Notice { get; init; }

    public static TranslationResult Failure(string errorMessage)
    {
        return new TranslationResult(false, string.Empty, errorMessage);
    }
}

public sealed record TranslationSegmentsResult(
    bool IsSuccess,
    IReadOnlyList<string> Segments,
    string? ErrorMessage)
{
    // Indices refer to the original input, including invariant labels. A partial
    // route may be retried without discarding or translating its valid output again.
    public IReadOnlyList<int>? FailedSegmentIndices { get; init; }
    public string? Notice { get; init; }

    public static TranslationSegmentsResult Failure(string errorMessage)
    {
        return new TranslationSegmentsResult(false, [], errorMessage);
    }
}
