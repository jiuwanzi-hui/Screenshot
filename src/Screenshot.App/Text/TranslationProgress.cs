namespace Screenshot.App.Text;

/// <summary>Operation-scoped progress; concurrent windows never share status.</summary>
internal sealed class TranslationProgress : IDisposable
{
    private static readonly AsyncLocal<TranslationProgress?> Current = new();
    private TranslationProgress? _previous;
    private SynchronizationContext? _context;
    private Action<string>? _receive;
    private int _disposed;

    private TranslationProgress(Action<string> receive)
    {
        _receive = receive;
        _context = SynchronizationContext.Current;
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static TranslationProgress Begin(Action<string> receive) => new(receive);

    internal static void Report(string message)
    {
        var scope = Current.Value;
        if (scope is null || Volatile.Read(ref scope._disposed) != 0) return;
        var context = Volatile.Read(ref scope._context);
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
            scope.Deliver(message);
        else context.Post(_ => scope.Deliver(message), null);
    }

    private void Deliver(string message)
    {
        if (Volatile.Read(ref _disposed) == 0) Volatile.Read(ref _receive)?.Invoke(message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
        // Queued work can still hold an ExecutionContext from this operation.
        // Keep its disposed sentinel, but release the UI and captured images.
        _previous = null;
        Interlocked.Exchange(ref _receive, null);
        Interlocked.Exchange(ref _context, null);
    }
}
