using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Screenshot.App.Infrastructure;

// Native selection HWNDs do not need a WPF render pass. Transparent WPF
// windows can tick Rendering at 30 Hz, regardless of the monitor's refresh.
// This clock wakes only during a gesture and keeps at most one UI frame queued.
internal sealed class NativePointerFrameClock(Action present, Action unavailable) : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _lifecycle = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private int _generation;
    private int _activeGeneration;
    private int _pendingGeneration;
    private bool _disposed;

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed || _activeGeneration != 0) return;
            Volatile.Write(ref _activeGeneration, ++_generation);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "SnapCut pointer frames" };
                _thread.Start();
            }
            _wake.Set();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            Volatile.Write(ref _activeGeneration, 0);
            _wake.Set();
        }
    }

    private void Run()
    {
        try
        {
            using var timer = NativeTimer.TryCreate();
            WaitHandle[] waits = timer is null ? [_wake] : [_wake, timer];
            while (!Volatile.Read(ref _disposed))
            {
                var generation = Volatile.Read(ref _activeGeneration);
                if (generation == 0)
                {
                    _wake.WaitOne();
                    continue;
                }
                _wake.WaitOne(0); // Drain the wake for this start; do not lose a quick restart.
                QueueFrame(generation);
                // Driver queries are off the dispatcher and occur once per
                // gesture, so starting a hotkey/drag cannot wait on a driver.
                var period = Stopwatch.Frequency / GetRefreshRate();
                var deadline = (double)Stopwatch.GetTimestamp();
                while (!Volatile.Read(ref _disposed) &&
                       Volatile.Read(ref _activeGeneration) == generation)
                {
                    QueueFrame(generation);
                    var now = Stopwatch.GetTimestamp();
                    deadline += period;
                    if (deadline <= now) deadline = now + period; // Skip missed frames; never catch up.
                    var remaining = (deadline - now) / Stopwatch.Frequency;
                    if (timer is not null && timer.Arm(remaining))
                    {
                        if (WaitHandle.WaitAny(waits) == 0) break;
                    }
                    else if (_wake.WaitOne(Math.Max(1, (int)Math.Ceiling(remaining * 1000)))) break;
                }
            }
        }
        catch (Exception)
        {
            // Keep selection usable if a session/dispatcher or timer backend
            // becomes unavailable. No native-clock failure can escape a worker.
            try
            {
                if (!_dispatcher.HasShutdownStarted)
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, unavailable);
            }
            catch (InvalidOperationException) { }
        }
        finally
        {
            lock (_lifecycle)
            {
                Volatile.Write(ref _activeGeneration, 0);
                Volatile.Write(ref _disposed, true);
                _wake.Dispose();
            }
        }
    }

    private void QueueFrame(int generation)
    {
        if (_dispatcher.HasShutdownStarted ||
            Interlocked.CompareExchange(ref _pendingGeneration, generation, 0) != 0) return;
        // Input priority is a WPF background priority and can be starved by
        // continuous native mouse messages. This is one bounded frame request,
        // not one request per mouse report, so it can use foreground priority.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            Interlocked.CompareExchange(ref _pendingGeneration, 0, generation);
            if (Volatile.Read(ref _activeGeneration) == generation && !Volatile.Read(ref _disposed))
                present();
        }));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            Volatile.Write(ref _activeGeneration, 0);
            Volatile.Write(ref _disposed, true);
            if (_thread is null) _wake.Dispose();
            else _wake.Set(); // The worker owns final cleanup; never Join on mouse release.
        }
    }

    private static double GetRefreshRate()
    {
        var device = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
        string? name = null;
        if (GetCursorPos(out var cursor))
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(MonitorFromPoint(cursor, 2), ref monitor)) name = monitor.Device;
        }
        return EnumDisplaySettingsW(name, -1, ref device) && device.Frequency > 1
            ? Math.Clamp(device.Frequency, 30, 240) : 60;
    }

    private sealed class NativeTimer : WaitHandle
    {
        private NativeTimer(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        public static NativeTimer? TryCreate()
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x00100002);
            if (handle == IntPtr.Zero) handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x00100002);
            return handle == IntPtr.Zero ? null : new NativeTimer(handle);
        }
        public bool Arm(double seconds)
        {
            var due = -Math.Max(1, (long)(seconds * 10_000_000));
            return SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public int Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Form;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
        IntPtr callback, IntPtr argument, bool resume);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DisplayMode settings);
}
