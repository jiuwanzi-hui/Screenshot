using System.Runtime.InteropServices;

namespace Screenshot.App.Infrastructure;

/// <summary>Compositor-owned frosted desktop glass; the UI supplies its neutral tint.</summary>
internal static class ClearGlassBackdrop
{
    internal static bool SetEnabled(IntPtr handle, bool enabled)
    {
        if (handle == IntPtr.Zero) return false;
        // Blur the actual windows underneath, never a captured wallpaper.
        // WPF supplies the translucent tint and control rims.
        if (DwmIsCompositionEnabled(out var composition) != 0 || !composition) return false;
        if (!enabled)
        {
            var disabled = SetAccent(handle, 0);
            var opaque = new BlurBehind { Flags = 1 };
            return DwmEnableBlurBehindWindow(handle, ref opaque) == 0 && disabled;
        }
        // Keep WPF's per-pixel alpha enabled separately from the blur policy.
        // Otherwise Windows composites the extended frame as a solid surface.
        var region = CreateRectRgn(0, 0, -1, -1);
        if (region == IntPtr.Zero) return false;
        try
        {
            var alpha = new BlurBehind { Flags = 3, Enabled = true, Region = region };
            if (DwmEnableBlurBehindWindow(handle, ref alpha) != 0) return false;
        }
        finally { _ = DeleteObject(region); }
        // Use blur without Acrylic's luminosity wash. The WPF surface supplies
        // the measured tint so the desktop retains its underlying colors.
        return SetAccent(handle, 3);
    }

    private static bool SetAccent(IntPtr handle, int mode)
    {
        // Leave the native tint flags clear: forcing its color plane can hide
        // the desktop behind a solid fill on current Windows builds.
        var policy = new AccentPolicy { State = mode };
        var size = Marshal.SizeOf<AccentPolicy>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionAttribute { Attribute = 19, Data = memory, Size = (nuint)size };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        internal uint Flags;
        [MarshalAs(UnmanagedType.Bool)] internal bool Enabled;
        internal IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] internal bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        internal int State;
        internal int Flags;
        internal uint Tint;
        internal int Animation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttribute
    {
        internal int Attribute;
        internal IntPtr Data;
        internal nuint Size;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr handle, ref BlurBehind blur);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr handle, ref CompositionAttribute data);
}
