using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Infrastructure;

/// <summary>
/// Provides the scheduling interval used by pointer-following UI. Interaction
/// producers run at a fixed 1ms cadence; presentation is still naturally
/// limited by the window compositor. Keeping input sampling independent from
/// the monitor refresh rate prevents low-refresh or remote displays from
/// making pointer tracking feel intermittent.
/// </summary>
internal static class DisplayRefreshRateService
{
    private const double InteractionRefreshRate = 1000;

    public static double GetInteractionRefreshRate(DrawingRectangle bounds)
    {
        _ = bounds;
        return InteractionRefreshRate;
    }

    public static TimeSpan GetInteractionFrameInterval(DrawingRectangle bounds) =>
        TimeSpan.FromSeconds(1d / GetInteractionRefreshRate(bounds));

}
