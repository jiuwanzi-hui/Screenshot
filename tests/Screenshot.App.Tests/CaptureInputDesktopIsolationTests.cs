using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Tests;

public sealed class CaptureInputDesktopIsolationTests
{
    [Fact]
    public Task VisibleSurfaceCannotMistakeAnotherWindowsMouseCaptureForReadiness() => OnPrivateDesktop(() =>
    {
        using var overlay = new TestWindow("Capture", 0, 0, 240, 160);
        using var dialog = new TestWindow("Menu owner", 30, 30, 100, 70);
        CaptureInputActivation.RaiseSurface(overlay.Handle);
        Assert.Equal(overlay.Handle, WindowFromPoint(new(60, 60)));
        _ = SetCapture(dialog.Handle);
        try
        {
            Assert.True(CaptureInputReadiness.HasForeignMouseCapture(overlay.Handle, GetCurrentThreadId()));
            Assert.False(CaptureInputReadiness.HasForeignMouseCapture(dialog.Handle, GetCurrentThreadId()));
        }
        finally { _ = ReleaseCapture(); }
        Assert.False(CaptureInputReadiness.HasForeignMouseCapture(overlay.Handle, GetCurrentThreadId()));
    });

    // Create real HWNDs on a private desktop that is never made the input
    // desktop. No windows, test patterns, clicks or keys reach the user's UI.
    [Fact]
    public Task ExistingTopmostDialogDoesNotStayAboveTheCaptureSurface() => OnPrivateDesktop(() =>
    {
        using var overlay = new TestWindow("Capture", 0, 0, 240, 160);
        using var dialog = new TestWindow("Error dialog", 30, 30, 100, 70);
        Assert.Equal(dialog.Handle, GetTopWindow(IntPtr.Zero));
        var dialogStyle = GetWindowLongW(dialog.Handle, -20);
        Assert.True(GetWindowRect(overlay.Handle, out var original));

        CaptureInputActivation.RaiseSurface(overlay.Handle);

        Assert.Equal(overlay.Handle, GetTopWindow(IntPtr.Zero));
        Assert.Equal(dialogStyle, GetWindowLongW(dialog.Handle, -20));
        Assert.True(GetWindowRect(overlay.Handle, out var actual));
        Assert.Equal(original, actual);
        Assert.Equal(overlay.Handle, WindowFromPoint(new(60, 60)));
    });

    [Theory]
    [InlineData(0x1B)]
    [InlineData(0x43)]
    public Task PointerOnlyPickerQueuesKeysAndUnhooksWhenClosed(int key) => OnPrivateDesktop(() =>
    {
        using var overlay = new TestWindow("Picker", 0, 0, 240, 160);
        Assert.False(CaptureInputReadiness.OwnsForeground(overlay.Handle));
        using var fallback = new CapturePickerKeyboardFallback(overlay.Handle, () => true);
        var callback = typeof(CapturePickerKeyboardFallback).GetMethod("OnKeyboard",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, key);
            // Invoke the callback directly: no global key injection, even on
            // the isolated desktop. Its queued private message uses a real HWND.
            Assert.Equal(new IntPtr(1), callback.Invoke(fallback, [0, new IntPtr(0x100), data]));
            Assert.Equal(0, overlay.LastKey); // never process input inside a hook
            WinForms.Application.DoEvents();
            Assert.Equal(key, overlay.LastKey);
            Assert.Equal(new IntPtr(1), callback.Invoke(fallback, [0, new IntPtr(0x101), data]));
            fallback.Dispose();
            Assert.Equal(IntPtr.Zero, typeof(CapturePickerKeyboardFallback).GetField("_hook",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fallback));
        }
        finally { Marshal.FreeHGlobal(data); }
    });

    private static async Task OnPrivateDesktop(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = CreateDesktopW("SnapCut-test-" + Guid.NewGuid().ToString("N"), null,
            IntPtr.Zero, 0, 0xCB /* read/create-window/hook/enumerate/write */, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                if (!SetThreadDesktop(desktop))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                test();
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                WinForms.Application.ExitThread();
                if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
            }
        }) { IsBackground = true };
        // STA initialization itself can create an OLE HWND before the delegate
        // starts, preventing SetThreadDesktop. NativeWindow needs no STA COM.
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally
        {
            // WinForms can retain its thread message HWND until thread exit.
            // Close the desktop after that exit, not while it is still in use.
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "Isolated desktop thread must exit.");
            Assert.True(CloseDesktop(desktop), "Private desktop must be released.");
        }
    }

    private sealed class TestWindow : WinForms.NativeWindow, IDisposable
    {
        internal int LastKey { get; private set; }
        internal TestWindow(string caption, int x, int y, int width, int height)
        {
            CreateHandle(new WinForms.CreateParams { Caption = caption, X = x, Y = y, Width = width, Height = height,
                Style = unchecked((int)0x80000000), ExStyle = 0x88 });
            _ = ShowWindow(Handle, 4); // SHOWNOACTIVATE, on the private desktop only
        }
        protected override void WndProc(ref WinForms.Message message)
        {
            if (message.Msg == CapturePickerKeyboardFallback.ForwardedKeyMessage)
            { LastKey = message.WParam.ToInt32(); return; }
            base.WndProc(ref message);
        }
        public void Dispose() => DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] private record struct Rect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string name, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
}
