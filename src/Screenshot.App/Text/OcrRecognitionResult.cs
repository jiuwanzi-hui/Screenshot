namespace Screenshot.App.Text;

public sealed record OcrTextRegion(
    string Text,
    double X,
    double Y,
    double Width,
    double Height)
{
    public double EstimatedFontSize { get; init; }
    // Native-worker-only evidence for merging the optional artistic pass.
    // These are not part of the public pipe/UI payload.
    internal float RecognitionConfidence { get; init; }
    internal bool RecoveredVerticalColumn { get; init; }
    internal bool VerifiedArtisticText { get; init; }
}

public sealed record OcrWordRegion(
    string Text,
    double X,
    double Y,
    double Width,
    double Height)
{
    // Set only after a second OCR pass on an isolated, structurally verified cell.
    internal bool IsTableCellText { get; init; }
}

public sealed record OcrRecognitionResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public string? Notice { get; init; }
    public OcrPerformanceDetails? Performance { get; init; }
    public string? ResultNotice => Performance is null ? Notice :
        string.Join("\n", new[] { Notice, Performance.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public IReadOnlyList<OcrTextRegion> Regions { get; init; } = [];

    public IReadOnlyList<OcrWordRegion> Words { get; init; } = [];

    public static OcrRecognitionResult Failure(string errorMessage)
    {
        return new OcrRecognitionResult(false, string.Empty, errorMessage);
    }
}

public sealed record OcrPerformanceDetails(double ModelPreparationMilliseconds, double RecognitionMilliseconds,
    int CpuThreads, int RowParallelism)
{
    public double WorkerStartMilliseconds { get; init; }
    public double QueueMilliseconds { get; init; }
    public double TotalMilliseconds { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public bool UsedArtisticModel { get; init; }
    public double ArtisticMilliseconds { get; init; }
    public string Description => $"OCR 共 {TotalMilliseconds / 1000:F2} 秒 · {ImageWidth} × {ImageHeight} · " +
        (UsedArtisticModel ? $"Small + Medium 增强（增强 {ArtisticMilliseconds / 1000:F2} 秒）" : "Small") +
        $"。排队／预准备等待 {QueueMilliseconds / 1000:F2} 秒，进程准备 {WorkerStartMilliseconds / 1000:F2} 秒，" +
        $"模型准备 {ModelPreparationMilliseconds / 1000:F2} 秒，识别 {RecognitionMilliseconds / 1000:F2} 秒；" +
        $"{CpuThreads} 个 CPU 线程，{RowParallelism} 行并行。";
}
