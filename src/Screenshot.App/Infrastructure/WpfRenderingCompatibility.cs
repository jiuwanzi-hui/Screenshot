using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Screenshot.App.Infrastructure;

internal static class WpfRenderingCompatibility
{
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
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Any(argument => string.Equals(
                argument,
                "--software-rendering",
                StringComparison.OrdinalIgnoreCase)))
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            return;
        }

        if (arguments.Any(argument => string.Equals(
                argument,
                "--hardware-rendering",
                StringComparison.OrdinalIgnoreCase)))
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            return;
        }

        // RenderCapability is the authoritative result for the active WPF
        // process. On hybrid laptops this also allows the Intel/AMD integrated
        // adapter to provide the hardware render path when the dGPU is not the
        // display adapter. Fall back to the adapter-name compatibility check
        // only when WPF reports Tier 0.
        RenderOptions.ProcessRenderMode =
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
        var arguments = commandLineArguments ?? [];
        if (arguments.Any(argument => string.Equals(
                argument,
                "--software-rendering",
                StringComparison.OrdinalIgnoreCase)))
        {
            return RenderMode.SoftwareOnly;
        }

        if (arguments.Any(argument => string.Equals(
                argument,
                "--hardware-rendering",
                StringComparison.OrdinalIgnoreCase)))
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
