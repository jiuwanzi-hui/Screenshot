using System.Reflection;
using System.Runtime.InteropServices;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class KeyboardModifierRecoveryTests
{
    [Theory]
    [InlineData(0x12u, 0xA4u, HotKeyModifiers.Alt, 'W', HotKeyAction.RecognizeText)]
    [InlineData(0x12u, 0xA5u, HotKeyModifiers.Alt, 'A', HotKeyAction.RegionCapture)]
    [InlineData(0x11u, 0xA2u, HotKeyModifiers.Control, 'D', HotKeyAction.PinImage)]
    [InlineData(0x10u, 0xA1u, HotKeyModifiers.Shift, 'W', HotKeyAction.RecognizeText)]
    [InlineData(0x5Bu, 0x5Bu, HotKeyModifiers.Windows, 'W', HotKeyAction.RecognizeText)]
    public async Task DelayedModifierDownCannotTurnTypingIntoCapture(
        uint rawKey, uint hookKey, HotKeyModifiers modifier, char key, HotKeyAction action)
    {
        GlobalHotKeyManager? manager = null;
        var triggered = new TaskCompletionSource<HotKeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                manager = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
                manager.KeyboardKeyStateOverride = _ => false;
                // Menu chords also reach WM_HOTKEY, preserving activation.
                manager.ForegroundMenuActiveOverride = () => false;
                // Avoid registering global chords: drive the actual hook with
                // synthetic packets so these tests cannot affect desktop input.
                var bindings = (Dictionary<int, HotKeyBinding>)typeof(GlobalHotKeyManager)
                    .GetField("_registeredBindings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
                bindings.Add(1, new HotKeyBinding(action, new HotKeyGesture(modifier, key)));
                manager.HotKeyPressed += (_, args) => triggered.TrySetResult(args.Action);

                manager.ProcessLowLevelModifierInputForTest(hookKey, true);
                manager.ProcessLowLevelModifierInputForTest(hookKey, false);
                manager.ProcessRawModifierInputForTest(rawKey, true); // Queued packet arrives late.
                Assert.Equal(IntPtr.Zero, SendKeyDown(manager, key));
                Assert.False(triggered.Task.IsCompleted);

                // A real held modifier must still trigger exactly this chord.
                manager.KeyboardKeyStateOverride = vk => vk == rawKey;
                Assert.Equal(IntPtr.Zero, SendKeyDown(manager, key));
                var message = new object[] { IntPtr.Zero, 0x0312, new IntPtr(1), IntPtr.Zero, false };
                typeof(GlobalHotKeyManager).GetMethod("WindowProcedure", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(manager, message);
                Assert.True((bool)message[4]);
            });
            Assert.Equal(action, await triggered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            WpfTestHost.Invoke(() => manager?.Dispose());
        }
    }

    private static IntPtr SendKeyDown(GlobalHotKeyManager manager, uint key)
    {
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<KeyboardPacket>());
        try
        {
            Marshal.StructureToPtr(new KeyboardPacket { VirtualKey = key }, data, false);
            return (IntPtr)typeof(GlobalHotKeyManager)
                .GetMethod("OnLowLevelKeyboardMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(manager, [0, new IntPtr(0x0100), data])!;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardPacket
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }
}
