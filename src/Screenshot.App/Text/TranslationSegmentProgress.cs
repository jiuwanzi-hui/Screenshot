namespace Screenshot.App.Text;

// Validated paragraphs can appear without waiting for unrelated slow paragraphs.
// The source accompanies the result because ordered providers may retry a subset.
internal sealed class TranslationSegmentProgress : IDisposable
{
    private static readonly AsyncLocal<TranslationSegmentProgress?> Current = new();
    private readonly TranslationSegmentProgress? _previous;
    private Action<string, string>? _receive;
    private TranslationSegmentProgress(Action<string, string> receive)
    { _previous = Current.Value; _receive = receive; Current.Value = this; }
    internal static TranslationSegmentProgress Begin(Action<string, string> receive) => new(receive);
    internal static void Report(string source, string translation)
    {
        var scope = Current.Value;
        if (scope is not null) Volatile.Read(ref scope._receive)?.Invoke(source, translation);
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref _receive, null);
        if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
    }
}
