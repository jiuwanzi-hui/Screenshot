using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class HotKeyMessageWindowTests
{
    [Fact]
    public void InputWindowHasNoWpfTargetAndReleasesItsHandle()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new HotKeyMessageWindow();
            var handle = window.Handle;
            Assert.True(IsWindow(handle));
            Assert.False(IsWindowVisible(handle));
            Assert.Null(HwndSource.FromHwnd(handle));
            var received = 0;
            window.AddHook((IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message != 0x802A) return IntPtr.Zero;
                Assert.Equal(handle, hwnd);
                Assert.Equal(new IntPtr(71), w);
                Assert.Equal(new IntPtr(93), l);
                received++;
                handled = true;
                return new IntPtr(123);
            });
            Assert.Equal(new IntPtr(123), SendMessage(handle, 0x802A, new IntPtr(71), new IntPtr(93)));
            Assert.Equal(1, received);
            window.Dispose();
            window.Dispose();
            Assert.False(IsWindow(handle));
        });
    }

    [Fact]
    public async Task QueuedHotKeyMessageReachesExistingDispatcherHandler()
    {
        GlobalHotKeyManager? manager = null;
        var completed = new TaskCompletionSource<HotKeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                manager = new GlobalHotKeyManager(deferLowLevelInputHooks: true)
                    { ForegroundMenuActiveOverride = () => false };
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                // Dispatch to our HWND only; no global chord registration/input injection.
                var bindings = (Dictionary<int, HotKeyBinding>)typeof(GlobalHotKeyManager)
                    .GetField("_registeredBindings", flags)!.GetValue(manager)!;
                bindings[71] = new(HotKeyAction.OpenSettings, new HotKeyGesture(HotKeyModifiers.Control, 'A'));
                manager.HotKeyPressed += (_, args) => completed.TrySetResult(args.Action);
                var window = (HotKeyMessageWindow)typeof(GlobalHotKeyManager)
                    .GetField("_messageSource", flags)!.GetValue(manager)!;
                Assert.True(PostMessage(window.Handle, 0x0312, new IntPtr(71), IntPtr.Zero));
            });
            Assert.Equal(HotKeyAction.OpenSettings, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { WpfTestHost.Invoke(() => manager?.Dispose()); }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
}
