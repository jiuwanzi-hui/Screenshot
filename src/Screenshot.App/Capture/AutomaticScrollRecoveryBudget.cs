namespace Screenshot.App.Capture;

// Reconnecting to the same saved viewport is not capture progress. Otherwise
// each successful backstep replenishes retries and creates an endless bounce.
internal sealed class AutomaticScrollRecoveryBudget(int maximumRounds = 2)
{
    private int _frontier;
    private int _rounds;
    internal void Reset(int outputHeight) { _frontier = outputHeight; _rounds = 0; }
    internal bool TryRecover(int outputHeight)
    {
        if (outputHeight >= _frontier + 24) Reset(outputHeight);
        return ++_rounds <= maximumRounds;
    }
}
