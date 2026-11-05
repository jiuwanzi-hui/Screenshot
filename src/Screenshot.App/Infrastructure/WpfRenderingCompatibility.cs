using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Screenshot.App.Infrastructure;

internal static class WpfRenderingCompatibility
{
    private const string SoftwareRenderingArgument = "--software-rendering";
    private const string HardwareRenderingArgument = "--hardware-rendering";
    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const int DisplayDeviceMirroringDriver = 0x00000008;
    private const int DisplayDeviceDisconnect = 0x02000000;
    private const int DisplayDeviceRemote = 0x04000000;

    private static readonly string[] VirtualDisplayNameFragments =
    [
        "virtual",
        "remote display",
        "indirect display",
        "todesk",
        "oray",
        "gameviewer",
        "parsec",
        "mirage",
    ];

    public static void ConfigureForCurrentSession()
    {
        RenderOptions.ProcessRenderMode = GetCurrentSessionRenderMode();
        Capture.CaptureTimingDiagnostics.Mark("wpf-rendering-mode",
            $"mode={RenderOptions.ProcessRenderMode}");
    }

    internal static RenderMode GetCurrentSessionRenderMode()
    {
        var arguments = Environment.GetCommandLineArgs();
        var explicitArgument = GetExplicitRenderingArgument(arguments);
        if (explicitArgument == SoftwareRenderingArgument)
        {
            return RenderMode.SoftwareOnly;
        }

        if (explicitArgument == HardwareRenderingArgument)
        {
            return RenderMode.Default;
        }

        // RenderCapability is the authoritative result for the active WPF
        // process. On hybrid laptops this also allows the Intel/AMD integrated
        // adapter to provide the hardware render path when the dGPU is not the
        // display adapter. Fall back to the adapter-name compatibility check
        // only when WPF reports Tier 0.
        return
            (RenderCapability.Tier >> 16) > 0
                ? RenderMode.Default
                : GetCompatibleRenderMode(
                    EnumerateDisplayAdapters(),
                    arguments);
    }

    internal static RenderMode GetCompatibleRenderMode(
        IEnumerable<DisplayAdapterDescriptor> adapters,
        IEnumerable<string>? commandLineArguments = null)
    {
        var explicitArgument = GetExplicitRenderingArgument(commandLineArguments ?? []);
        if (explicitArgument == SoftwareRenderingArgument)
        {
            return RenderMode.SoftwareOnly;
        }

        if (explicitArgument == HardwareRenderingArgument)
        {
            return RenderMode.Default;
        }

        // A real display output can use WPF's GPU composition even when the
        // user operates it through ToDesk. Software-rendering a transparent
        // desktop-sized capture window makes every pointer update compete
        // with the remote encoder. Keep the compatibility fallback only when
        // every active output is virtual/remote, which covers lid-closed and
        // display-less hosts that previously rendered WPF windows blank.
        return adapters.Any(IsPhysicalDesktopAdapter)
            ? RenderMode.Default
            : RenderMode.SoftwareOnly;
    }

    internal static string? GetExplicitRenderingArgument(IEnumerable<string> arguments)
    {
        var hardwareRequested = false;
        foreach (var argument in arguments)
        {
            // Preserve the existing software override precedence regardless
            // of argument order, and forward only this UI rendering choice.
            if (string.Equals(argument, SoftwareRenderingArgument, StringComparison.OrdinalIgnoreCase))
                return SoftwareRenderingArgument;
            if (string.Equals(argument, HardwareRenderingArgument, StringComparison.OrdinalIgnoreCase))
                hardwareRequested = true;
        }
        return hardwareRequested ? HardwareRenderingArgument : null;
    }

    private static bool IsPhysicalDesktopAdapter(
        DisplayAdapterDescriptor adapter)
    {
        if (!adapter.AttachedToDesktop || adapter.IsMirroringOrRemote)
        {
            return false;
        }

        return !VirtualDisplayNameFragments.Any(fragment =>
            adapter.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static List<DisplayAdapterDescriptor>
        EnumerateDisplayAdapters()
    {
        var adapters = new List<DisplayAdapterDescriptor>();
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice
            {
                Size = Marshal.SizeOf<DisplayDevice>(),
            };
            if (!NativeMethods.EnumDisplayDevices(
                    null,
                    index,
                    ref device,
                    0))
            {
                break;
            }

            var flags = device.StateFlags;
            adapters.Add(new DisplayAdapterDescriptor(
                device.DeviceString ?? string.Empty,
                (flags & DisplayDeviceAttachedToDesktop) != 0,
                (flags & (DisplayDeviceMirroringDriver |
                          DisplayDeviceDisconnect |
                          DisplayDeviceRemote)) != 0));
        }

        return adapters;
    }

    internal readonly record struct DisplayAdapterDescriptor(
        string Name,
        bool AttachedToDesktop,
        bool IsMirroringOrRemote);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        [MarshalAs(UnmanagedType.U4)]
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceString;

        [MarshalAs(UnmanagedType.U4)]
        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceKey;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(
            string? device,
            uint deviceNumber,
            ref DisplayDevice displayDevice,
            uint flags);
    }
}
