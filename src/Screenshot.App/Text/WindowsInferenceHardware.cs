using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Screenshot.App.Text;

internal sealed record InferenceAdapter(string Name, string Identity, long BudgetMiB);
internal sealed record InferenceHardwareSnapshot(string Fingerprint, IReadOnlyList<InferenceAdapter> Adapters);

internal static class WindowsInferenceHardware
{
    // Read WDDM's current budget without a D3D device, Vulkan instance or worker.
    // Budget is an OS allocation allowance, not a claim about globally free VRAM.
    internal static InferenceHardwareSnapshot Capture()
    {
        var adapters = new List<InferenceAdapter>();
        var identity = new StringBuilder("b10276-isolated-v1|").Append(Environment.OSVersion.Version)
            .Append('|').Append(Environment.ProcessorCount);
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            identity.Append('|').Append(machine?.GetValue("MachineGuid"));
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            identity.Append('|').Append(cpu?.GetValue("ProcessorNameString")).Append('|').Append(cpu?.GetValue("Identifier"));
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in (display?.GetSubKeyNames() ?? []).Order(StringComparer.Ordinal))
            {
                using var driver = display!.OpenSubKey(name);
                identity.Append('|').Append(driver?.GetValue("MatchingDeviceId"))
                    .Append('|').Append(driver?.GetValue("DriverVersion"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { identity.Append("|registry-unavailable"); }

        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) >= 0)
            {
                var enumerate = Method<EnumAdapters>(factory, 12);
                for (uint index = 0; index < 32 && enumerate(factory, index, out var adapter) >= 0; index++)
                {
                    try
                    {
                        if (Method<GetDescription>(adapter, 10)(adapter, out var desc) < 0 || (desc.Flags & 2) != 0) continue;
                        // Vulkan's numbered device names are not a permanent
                        // hardware identity. Reconfirm after a driver instance /
                        // boot changes the adapter LUID, even for the same model.
                        var adapterId = $"{desc.VendorId:X}:{desc.DeviceId:X}:{desc.SubSysId:X}:{desc.Revision:X}:{desc.DedicatedVideoMemory}:{desc.Luid:X}";
                        var budget = 0L;
                        var adapter3Id = new Guid("645967a4-1392-4310-a798-8053ce3e93fd");
                        if (Marshal.QueryInterface(adapter, ref adapter3Id, out var adapter3) >= 0)
                        {
                            try
                            {
                                if (Method<QueryMemory>(adapter3, 14)(adapter3, 0, 0, out var memory) >= 0)
                                    budget = (long)(Math.Min(desc.DedicatedVideoMemory.ToUInt64(),
                                        memory.Budget > memory.CurrentUsage ? memory.Budget - memory.CurrentUsage : 0) / 1048576);
                            }
                            finally { Marshal.Release(adapter3); }
                        }
                        adapters.Add(new(desc.Description.Trim(), adapterId, budget));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        { identity.Append("|dxgi-unavailable"); }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        foreach (var adapter in adapters.OrderBy(adapter => adapter.Identity, StringComparer.Ordinal))
            identity.Append('|').Append(adapter.Identity);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))), adapters);
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemory { public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryMemory(IntPtr adapter, uint node, uint segment, out VideoMemory memory);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
