namespace Screenshot.App.Capture;

/// <summary>Owns capture interaction until hand-off, independently of a recording's lifetime.</summary>
internal sealed class CaptureInteractionScope : IDisposable
{
    private Action<bool>? _stateChanged;

    internal CaptureInteractionScope(Action<bool> stateChanged)
    {
        _stateChanged = stateChanged;
        stateChanged(true);
    }

    public void Dispose()
    {
        // The controls-ready callback releases startup. The later recording
        // cleanup must not release a different screenshot started meanwhile.
        Interlocked.Exchange(ref _stateChanged, null)?.Invoke(false);
    }
}
