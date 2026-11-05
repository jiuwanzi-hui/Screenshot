using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Screenshot.App.Text;

// The OS closes the job when SnapCut exits, including crashes and forced exits.
// This prevents an orphan inference worker from retaining GPU memory indefinitely.
internal sealed class TranslationWorkerLifetime : IDisposable
{
    private readonly SafeFileHandle _job;

    internal static async Task<(Process Process, TranslationWorkerLifetime Lifetime)> StartAsync(
        ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var creation = Task.Run(() =>
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动离线推理程序。");
            try { return (process, new TranslationWorkerLifetime(process)); }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                finally { process.Dispose(); }
                throw;
            }
        });
        try { return await creation.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // Process creation itself may wait inside antivirus/loader hooks.
            // Cancellation must return promptly, but also reap any late child.
            _ = DisposeLateAsync(creation);
            throw;
        }
    }

    private static async Task DisposeLateAsync(Task<(Process, TranslationWorkerLifetime)> creation)
    {
        try
        {
            var (process, lifetime) = await creation.ConfigureAwait(false);
            lifetime.Dispose();
            process.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
    }

    internal TranslationWorkerLifetime(Process process)
    {
        _job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(_job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Dispose() => _job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        in ExtendedLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
