using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

internal static class MacCursorService
{
    public static CGPoint GetGlobalPosition()
    {
        var cgEvent = CoreGraphics.CGEventCreate(IntPtr.Zero);
        if (cgEvent == IntPtr.Zero)
        {
            return new CGPoint();
        }

        try
        {
            return CoreGraphics.CGEventGetLocation(cgEvent);
        }
        finally
        {
            CoreFoundation.CFRelease(cgEvent);
        }
    }
}
