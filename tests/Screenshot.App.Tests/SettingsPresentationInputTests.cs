using System.Reflection;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class SettingsPresentationInputTests
{
    [Fact]
    public void SettingsPresentationDetachesMouseOnlyAndNestedLeasesRestoreItOnce()
    {
        WpfTestHost.Invoke(() =>
        {
            using var keys = new GlobalHotKeyManager();
            var keyboard = Handle(keys, "_keyboardHook");
            Assert.NotEqual(IntPtr.Zero, Handle(keys, "_mouseHook"));
            var first = keys.SuspendMouseForWindowPresentation();
            using (keys.SuspendMouseForWindowPresentation())
            {
                Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
                Assert.Equal(keyboard, Handle(keys, "_keyboardHook"));
                first.Dispose();
                Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
                keys.StartInputHooks();
                Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
            }
            Assert.NotEqual(IntPtr.Zero, Handle(keys, "_mouseHook"));
            first.Dispose();
            Assert.Equal(keyboard, Handle(keys, "_keyboardHook"));
        });
    }

    [Fact]
    public void DeferredStartAndCaptureSuspensionCannotReinstallMouseDuringSettingsLayout()
    {
        WpfTestHost.Invoke(() =>
        {
            using var keys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
            using (keys.SuspendMouseForWindowPresentation())
            {
                keys.StartInputHooks();
                Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
                keys.SetMouseShortcutsSuspended(true);
            }
            Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
            keys.SetMouseShortcutsSuspended(false);
            keys.SetMouseShortcutsSuspended(true);
            typeof(GlobalHotKeyManager).GetMethod("ResumeMouseInputHooks", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(keys, null);
            Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
        });
    }

    [Fact]
    public void DisposingManagerDuringPresentationDoesNotReinstallHooks()
    {
        WpfTestHost.Invoke(() =>
        {
            var keys = new GlobalHotKeyManager();
            using var presentation = keys.SuspendMouseForWindowPresentation();
            keys.Dispose();
            presentation.Dispose();
            Assert.Equal(IntPtr.Zero, Handle(keys, "_mouseHook"));
            Assert.Equal(IntPtr.Zero, Handle(keys, "_keyboardHook"));
        });
    }

    private static IntPtr Handle(GlobalHotKeyManager keys, string name) =>
        (IntPtr)typeof(GlobalHotKeyManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(keys)!;
}
