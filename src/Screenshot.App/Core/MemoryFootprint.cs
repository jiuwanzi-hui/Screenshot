namespace Screenshot.App.Core;

/// <summary>
/// Hands the memory retired by a finished capture session back to the OS.
/// </summary>
/// <remarks>
/// Native images are released by their owners. Managed collection is coalesced
/// after the close burst; emptying the working set evicts live UI and hook code
/// and makes the next interaction page it all back in.
/// </remarks>
internal static class MemoryFootprint
{
    private static readonly System.Threading.Timer CollectionTimer = BackgroundTimer.Create(
        static _ => CollectRetiredBuffers());
    private static int _interactionActive;
    private static int _collectionPending;
    private static long _lastCollection;
    private static int _finalizerFollowup;

    public static void SetInteractionActive(bool active)
    {
        Volatile.Write(ref _interactionActive, active ? 1 : 0);
        if (!active && Volatile.Read(ref _collectionPending) != 0)
            CollectionTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public static void TrimAfterHeavyOperation()
    {
        Volatile.Write(ref _collectionPending, 1);
        CollectionTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private static void CollectRetiredBuffers()
    {
        try
        {
            var now = Environment.TickCount64;
            // Retired application buffers must not remain live just because the
            // user is typing in another app. Pause for our capture interaction,
            // then resume on its completion instead of polling global input.
            if (Volatile.Read(ref _collectionPending) == 0 ||
                Volatile.Read(ref _interactionActive) != 0) return;
            var followup = Volatile.Read(ref _finalizerFollowup) != 0;
            var remaining = (followup ? 3_000 : 30_000) - (now - Interlocked.Read(ref _lastCollection));
            if (remaining > 0)
            {
                CollectionTimer.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                return;
            }
            // A forced compacting GC suspends every managed thread even when
            // invoked through Task.Run, including the system input hook thread.
            // Request one background collection after retired windows have
            // detached their buffers. Optimized may skip it indefinitely on
            // machines with ample RAM, retaining the native bitmap finalizers.
            Interlocked.Exchange(ref _lastCollection, now);
            Interlocked.Exchange(ref _collectionPending, 0);
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            // Bitmap/font finalizers can retire native allocations after the
            // first collection. One bounded follow-up releases that garbage;
            // there is no repeating idle GC or working-set eviction.
            Interlocked.Exchange(ref _finalizerFollowup, followup ? 0 : 1);
            if (!followup)
            {
                Volatile.Write(ref _collectionPending, 1);
                CollectionTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception)
        {
            // Trimming is best-effort and must never break the flow it ends.
        }
    }

}
