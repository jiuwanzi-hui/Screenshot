using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

internal static class MacWindowSnapService
{
    private const int CfNumberIntType = 9;

    public static CGRect? FindWindowAt(CGPoint point)
    {
        var windows = CoreGraphics.CGWindowListCopyWindowInfo(
            CoreGraphics.WindowListOptionOnScreenOnly |
            CoreGraphics.WindowListExcludeDesktopElements,
            0);
        if (windows == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var count = CoreFoundation.CFArrayGetCount(windows);
            for (var index = 0L; index < count; index++)
            {
                var dictionary = CoreFoundation.CFArrayGetValueAtIndex(windows, index);
                if (dictionary == IntPtr.Zero ||
                    ReadInteger(dictionary, CoreGraphics.WindowLayerKey) != 0 ||
                    ReadInteger(dictionary, CoreGraphics.WindowOwnerPidKey) == Environment.ProcessId)
                {
                    continue;
                }

                var boundsDictionary = CoreFoundation.CFDictionaryGetValue(
                    dictionary,
                    CoreGraphics.WindowBoundsKey);
                if (boundsDictionary == IntPtr.Zero ||
                    !CoreGraphics.CGRectMakeWithDictionaryRepresentation(
                        boundsDictionary,
                        out var bounds) ||
                    bounds.Size.Width < 20 || bounds.Size.Height < 20)
                {
                    continue;
                }

                if (point.X >= bounds.Left && point.X < bounds.Right &&
                    point.Y >= bounds.Top && point.Y < bounds.Bottom)
                {
                    return bounds;
                }
            }

            return null;
        }
        finally
        {
            CoreFoundation.CFRelease(windows);
        }
    }

    private static int ReadInteger(IntPtr dictionary, IntPtr key)
    {
        var number = CoreFoundation.CFDictionaryGetValue(dictionary, key);
        return number != IntPtr.Zero && CoreFoundation.CFNumberGetValue(
            number,
            CfNumberIntType,
            out var value)
            ? value
            : 0;
    }
}
