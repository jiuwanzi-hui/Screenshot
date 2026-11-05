namespace Screenshot.App.Infrastructure;

internal sealed class TrayClickSequence
{
    private bool _ignoreRelease;
    private bool _pending;
    internal bool LeftReleased()
    {
        if (_ignoreRelease) { _ignoreRelease = false; return false; }
        _pending = true;
        return true;
    }
    internal void DoubleClicked() { _pending = false; _ignoreRelease = true; }
    internal void Cancel() { _pending = false; _ignoreRelease = false; }
    internal bool CompleteSingleClick()
    {
        var pending = _pending; _pending = false; return pending;
    }
}
