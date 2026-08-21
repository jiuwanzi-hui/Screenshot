using System.Windows.Interop;
using System.Windows.Media;

namespace Screenshot.App.Infrastructure;

internal static class WpfRenderingCompatibility
{
    public static void ConfigureForCurrentSession()
    {
        // Remote-control tools that are not RDP still look like a local
        // session. With the laptop lid closed or no physical monitor attached,
        // WPF may create a hardware render target that only paints white.
        // This affects WPF composition only; capture, OCR, translation and
        // video encoding keep their own acceleration paths.
        RenderOptions.ProcessRenderMode = GetCompatibleRenderMode();
    }

    internal static RenderMode GetCompatibleRenderMode() =>
        RenderMode.SoftwareOnly;
}
