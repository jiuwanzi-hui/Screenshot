namespace Screenshot.App.Infrastructure;

/// <summary>Protects work that can outlive its window, such as a history write.</summary>
internal static class FeatureActivity
{
    private static int _count;
    internal static int Count => Volatile.Read(ref _count);
    internal static event Action? Changed;

    internal static IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        Changed?.Invoke();
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _count);
            Changed?.Invoke();
        }
    }
}
