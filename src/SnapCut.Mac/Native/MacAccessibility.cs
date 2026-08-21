using System.Runtime.InteropServices;

namespace SnapCut.Mac.Native;

/// <summary>
/// Accessibility is separate from Input Monitoring on recent macOS releases.
/// Keeping the probe here lets the settings page explain which capability is
/// missing instead of treating every failed event tap as the same error.
/// </summary>
internal static class MacAccessibility
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    public static bool IsTrusted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            return AXIsProcessTrusted();
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
