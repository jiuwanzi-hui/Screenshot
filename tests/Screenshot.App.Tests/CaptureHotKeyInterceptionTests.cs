using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class CaptureHotKeyInterceptionTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void PinDisplayChangeDiscardsMenuFrameAndRejectsItsLateWorkerWithoutEndingCapture()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            manager.SetCaptureOverlayActive(true);
            using var stale = new CapturedImage(new Bitmap(24, 18));
            using var late = new CapturedImage(new Bitmap(24, 18));
            var generation = (long)typeof(GlobalHotKeyManager).GetField("_contextMenuCaptureGeneration", Private)!.GetValue(manager)!;
            var store = typeof(GlobalHotKeyManager).GetMethod("StoreImmediatePreCapturedScreen", Private)!;
            var take = typeof(GlobalHotKeyManager).GetMethod("TakePreCapturedScreen", Private)!;
            store.Invoke(manager, [stale, new[] { HotKeyAction.RegionCapture }, generation]);

            manager.NotifyDesktopContentChanged();

            Assert.Null(take.Invoke(manager, [HotKeyAction.RegionCapture, true]));
            store.Invoke(manager, [late, new[] { HotKeyAction.RegionCapture }, generation]);
            Assert.Throws<ObjectDisposedException>(() => late.Preview);
            Assert.Null(take.Invoke(manager, [HotKeyAction.RegionCapture, true]));
            Assert.True(manager.ShouldSuppressContextMenuGestureForTest(isButtonDown: false));
            Assert.False(manager.HasRecentContextMenuGestureForTest());
            // A newly opened menu is still eligible; invalidation must not
            // disable the menu-preserving screenshot path permanently.
            var current = (long)typeof(GlobalHotKeyManager).GetField("_contextMenuCaptureGeneration", Private)!.GetValue(manager)!;
            using var fresh = new CapturedImage(new Bitmap(24, 18));
            store.Invoke(manager, [fresh, new[] { HotKeyAction.RegionCapture }, current]);
            Assert.Same(fresh, take.Invoke(manager, [HotKeyAction.RegionCapture, true]));
        });
    }

    [Theory]
    [InlineData(HotKeyAction.RegionCapture, 'A', false)]
    [InlineData(HotKeyAction.RegionCapture, 'A', true)]
    [InlineData(HotKeyAction.RecognizeText, 'W', false)]
    [InlineData(HotKeyAction.RecognizeText, 'W', true)]
    [InlineData(HotKeyAction.PinImage, 'D', false)]
    [InlineData(HotKeyAction.PinImage, 'D', true)]
    public void OpenMenusKeepSystemHotKeyDeliveryAndTheirFrozenPixels(HotKeyAction action, char key, bool menuOpen)
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager(deferLowLevelInputHooks: true)
            {
                ForegroundMenuActiveOverride = () => menuOpen,
                KeyboardKeyStateOverride = vk => vk == 0x12,
            };
            var bindings = (Dictionary<int, HotKeyBinding>)typeof(GlobalHotKeyManager)
                .GetField("_registeredBindings", Private)!.GetValue(manager)!;
            bindings.Add(1, new HotKeyBinding(action, new HotKeyGesture(HotKeyModifiers.Alt, key)));
            // Model a frame cached before Alt closes a menu; no real desktop capture.
            var frame = new CapturedImage(new Bitmap(24, 18));
            var generation = (long)typeof(GlobalHotKeyManager).GetField("_contextMenuCaptureGeneration", Private)!.GetValue(manager)!;
            typeof(GlobalHotKeyManager).GetMethod("StoreImmediatePreCapturedScreen", Private)!
                .Invoke(manager, [frame, new[] { action }, generation]);
            var count = 0;
            manager.HotKeyPressed += (_, args) =>
            {
                Assert.Equal(action, args.Action);
                Assert.Same(frame, args.DetachPreCapturedScreen());
                count++;
            };
            Assert.Equal(IntPtr.Zero, Hook(manager, 0x12, false));
            Assert.Equal(IntPtr.Zero, Hook(manager, key, false));
            Assert.Equal(0, count); // Hook must not start a second, unregistered capture.
            var message = new object[] { IntPtr.Zero, 0x0312, new IntPtr(1), IntPtr.Zero, false };
            typeof(GlobalHotKeyManager).GetMethod("WindowProcedure", Private)!.Invoke(manager, message);
            Assert.True((bool)message[4]);
            Assert.Equal(1, count);
            Assert.Equal(IntPtr.Zero, Hook(manager, key, true));
            Assert.Equal(IntPtr.Zero, Hook(manager, 0x12, true));
            frame.Dispose();
        });
    }

    [Fact]
    public void MenuTypingAndModifierReleaseAreNotSwallowed()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager(deferLowLevelInputHooks: true)
            { ForegroundMenuActiveOverride = () => true, KeyboardKeyStateOverride = _ => false };
            foreach (var key in new uint[] { 0x12, 'A', 'W', 'F' })
            {
                Assert.Equal(IntPtr.Zero, Hook(manager, key, false));
                Assert.Equal(IntPtr.Zero, Hook(manager, key, true));
            }
        });
    }

    private static IntPtr Hook(GlobalHotKeyManager manager, uint key, bool up)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<KeyboardPacket>());
        try
        {
            Marshal.StructureToPtr(new KeyboardPacket { VirtualKey = key }, memory, false);
            return (IntPtr)typeof(GlobalHotKeyManager).GetMethod("OnLowLevelKeyboardMessage", Private)!
                .Invoke(manager, [0, new IntPtr(up ? 0x101 : 0x100), memory])!;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardPacket { public uint VirtualKey, Scan, Flags, Time; public IntPtr ExtraInfo; }
}
