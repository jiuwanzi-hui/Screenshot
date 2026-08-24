namespace SnapCut.Core;

/// <summary>
/// Selection arithmetic for the active scroll-capture frame backlog.
/// </summary>
/// <remarks>
/// Screen sampling is cheap (a BitBlt) while overlap matching is not, so a
/// fast fling produces samples faster than the matcher can stitch them and a
/// short backlog builds up. The backlog is stitched strictly in capture
/// order: consecutive samples are only milliseconds apart, so each one still
/// overlaps its predecessor no matter how fast the document scrolls.
/// </remarks>
public static class ScrollFrameSelection
{
    /// <summary>
    /// Index to evict when the backlog is over budget. The oldest sample is
    /// the next link in the stitch chain and the newest shows where the
    /// viewport is now, so a middle sample is dropped instead: that at most
    /// doubles one chain step rather than tearing either end.
    /// </summary>
    public static int SelectDecimationIndex(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 3);
        return count / 2;
    }
}
