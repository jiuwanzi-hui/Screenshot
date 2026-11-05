using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// All formats share one short-lived write queue. Payloads must be fully
// prepared before OpenClipboard: no rendering, OLE callbacks, I/O or awaits
// while another application's clipboard access is excluded.
internal static class NativeClipboardWriter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _pendingWrites;
    internal static bool HasPendingWrites => Volatile.Read(ref _pendingWrites) != 0;

    internal static async Task WriteAsync(Action write, CancellationToken token)
    {
        Interlocked.Increment(ref _pendingWrites);
        using var activity = Infrastructure.FeatureActivity.Begin();
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try { await Task.Run(write, token).ConfigureAwait(false); }
            finally { Gate.Release(); }
        }
        finally { Interlocked.Decrement(ref _pendingWrites); }
    }

    internal static void Publish(CancellationToken token, params ClipboardPayload[] payloads)
    {
        // A real, temporary HWND is required when EmptyClipboard is followed by
        // SetClipboardData. It is created/destroyed on this same worker thread.
        using var owner = new ClipboardOwner();
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (OpenClipboard(owner.Handle)) break;
            var error = Marshal.GetLastWin32Error();
            if (attempt >= 9)
                throw new Win32Exception(error, "剪贴板正被其他程序使用，请重试。");
            // Never wait with the clipboard open; cancellation ends the retry.
            if (token.WaitHandle.WaitOne(20)) token.ThrowIfCancellationRequested();
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空剪贴板。");
            foreach (var payload in payloads)
            {
                if (SetClipboardData(payload.Format, payload.Handle) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入剪贴板。");
                payload.TransferToSystem();
            }
        }
        finally { _ = CloseClipboard(); }
    }

    internal static uint RegisterFormat(string name)
    {
        var format = RegisterClipboardFormat(name);
        return format != 0 ? format : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class ClipboardOwner : IDisposable
    {
        internal IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        internal ClipboardOwner()
        {
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => _ = DestroyWindow(Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
}

internal sealed class ClipboardPayload : IDisposable
{
    internal uint Format { get; }
    internal IntPtr Handle { get; private set; }

    internal ClipboardPayload(uint format, int size, Action<IntPtr> fill)
    {
        Format = format;
        Handle = GlobalAlloc(0x0002, checked((nuint)size));
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var memory = GlobalLock(Handle);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { fill(memory); }
            finally { _ = GlobalUnlock(Handle); }
        }
        catch { Dispose(); throw; }
    }

    internal ClipboardPayload(uint format, byte[] bytes)
        : this(format, bytes.Length, memory => Marshal.Copy(bytes, 0, memory, bytes.Length)) { }

    internal void TransferToSystem() => Handle = IntPtr.Zero;
    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        _ = GlobalFree(Handle);
        Handle = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}
