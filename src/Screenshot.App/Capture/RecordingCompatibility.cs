using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Screenshot.App.Capture;

internal readonly record struct RecordingProfile(bool UseWindowsGraphicsCapture, bool HardwareEncoding)
{
    internal static RecordingProfile Default => new(false, true);
    internal static RecordingProfile Compatible => new(true, false);
    internal static RecordingProfile Software => new(false, false);
    internal string Description => HardwareEncoding ? "硬件加速录制" : "兼容录制（软件编码）";
}

internal readonly record struct RecordingDisplay(string DeviceName, string Description, bool Attached, bool Remote);

internal static class RecordingCompatibility
{
    private static readonly string[] VirtualNameFragments =
    [
        "IddDriver", "indirect display", "virtual", "remote display", "mirage",
        "todesk", "oray", "parsec", "gameviewer",
    ];
    internal static bool SupportsWindowsGraphicsCapture()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    internal static RecordingProfile Resolve(string deviceName)
    {
        var displays = new List<RecordingDisplay>();
        for (uint index = 0; index < 128; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            displays.Add(new(device.DeviceName ?? string.Empty, device.DeviceString ?? string.Empty,
                (device.StateFlags & 1) != 0, (device.StateFlags & (8 | 0x04000000)) != 0));
        }
        return Choose(deviceName, displays, System.Windows.Forms.SystemInformation.TerminalServerSession,
            Environment.GetCommandLineArgs().Contains("--compatible-recording", StringComparer.OrdinalIgnoreCase));
    }

    internal static string GetDisplayIdentity(string deviceName)
    {
        for (uint index = 0; index < 128; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if (string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return $"{device.DeviceName}|{device.DeviceId}|{device.DeviceKey}|{device.DeviceString}";
        }
        return deviceName;
    }

    internal static RecordingProfile Choose(string deviceName, IEnumerable<RecordingDisplay> displays,
        bool remoteSession, bool forceCompatible = false)
    {
        // Only the output being recorded matters. An installed, unused virtual
        // adapter must not disable the Intel/AMD/NVIDIA physical output's encoder.
        var target = displays.FirstOrDefault(display => display.Attached &&
            string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        var name = target.Description ?? string.Empty;
        var virtualOutput = target.Remote || VirtualNameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        // Dedicated VRAM is deliberately not used: integrated GPUs share RAM.
        return forceCompatible || remoteSession || virtualOutput
            ? RecordingProfile.Compatible : RecordingProfile.Default;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
}
