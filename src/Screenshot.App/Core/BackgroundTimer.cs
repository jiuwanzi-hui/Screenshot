namespace Screenshot.App.Core;

// Lifetime/persistence timers belong to the service, not to the operation that
// first creates it. Capturing AsyncLocal progress here retains closed windows.
internal static class BackgroundTimer
{
    internal static System.Threading.Timer Create(TimerCallback callback)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return new(callback, null, Timeout.Infinite, Timeout.Infinite);
        using (ExecutionContext.SuppressFlow())
            return new(callback, null, Timeout.Infinite, Timeout.Infinite);
    }
}
