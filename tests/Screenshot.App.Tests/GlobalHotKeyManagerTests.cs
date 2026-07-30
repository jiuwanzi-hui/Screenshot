using System.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class GlobalHotKeyManagerTests
{
    [Fact]
    public void PreCapturesBeforeAltCanDismissTransientUi()
    {
        var modifiers = HotKeyModifiers.Control | HotKeyModifiers.Alt;
        var bindings = new[]
        {
            new HotKeyBinding(
                HotKeyAction.RegionCapture,
                new HotKeyGesture(modifiers, 'S')),
            new HotKeyBinding(
                HotKeyAction.RecognizeText,
                new HotKeyGesture(modifiers, 'O')),
            new HotKeyBinding(
                HotKeyAction.OpenSettings,
                new HotKeyGesture(modifiers, 0xBC)),
        };

        var actions = GlobalHotKeyManager.GetPreCaptureActions(
            bindings,
            virtualKey: 0x12,
            modifiers: HotKeyModifiers.Alt);

        Assert.Contains(HotKeyAction.RegionCapture, actions);
        Assert.Contains(HotKeyAction.RecognizeText, actions);
        Assert.DoesNotContain(HotKeyAction.OpenSettings, actions);
    }

    [Fact]
    public void RegistersAndReleasesUncommonGlobalHotKeys()
    {
        using var completed = new ManualResetEventSlim();
        Exception? exception = null;
        var registrationSucceeded = false;
        var registeredCount = 0;
        var suspendedCount = 0;
        var registeredCountWhileSuspended = -1;
        var resumedSuccessfully = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var manager = new GlobalHotKeyManager();
                var modifiers =
                    HotKeyModifiers.Control |
                    HotKeyModifiers.Alt |
                    HotKeyModifiers.Shift;
                var bindings = Enum.GetValues<HotKeyAction>()
                    .Select((action, index) => new HotKeyBinding(
                        action,
                        new HotKeyGesture(modifiers, (uint)(0x83 + index))))
                    .ToArray();

                var result = manager.Apply(bindings);
                registrationSucceeded = result.IsSuccess;
                registeredCount = manager.RegisteredBindings.Count;
                var suspendedBindings = manager.SuspendRegistrations();
                suspendedCount = suspendedBindings.Count;
                registeredCountWhileSuspended = manager.RegisteredBindings.Count;
                resumedSuccessfully = manager.RestoreRegistrations(
                    suspendedBindings).IsSuccess;
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
        Assert.True(registrationSucceeded);
        Assert.Equal(Enum.GetValues<HotKeyAction>().Length, registeredCount);
        Assert.Equal(Enum.GetValues<HotKeyAction>().Length, suspendedCount);
        Assert.Equal(0, registeredCountWhileSuspended);
        Assert.True(resumedSuccessfully);
    }

    [Fact]
    public void KeepsUncontestedBindingsWhenOneInitialHotKeyIsOccupied()
    {
        using var completed = new ManualResetEventSlim();
        Exception? exception = null;
        HotKeyRegistrationResult? result = null;
        var registeredCount = 0;

        var thread = new Thread(() =>
        {
            try
            {
                using var blocker = new GlobalHotKeyManager();
                using var manager = new GlobalHotKeyManager();
                var modifiers =
                    HotKeyModifiers.Control |
                    HotKeyModifiers.Alt |
                    HotKeyModifiers.Shift;
                var blockerBindings = CreateBindings(modifiers, firstVirtualKey: 0x7C);
                var requestedBindings = CreateBindings(modifiers, firstVirtualKey: 0x82)
                    .ToArray();
                requestedBindings[0] = blockerBindings[0];

                Assert.True(blocker.Apply(blockerBindings).IsSuccess);
                result = manager.ApplyAvailable(requestedBindings);
                registeredCount = manager.RegisteredBindings.Count;
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(Enum.GetValues<HotKeyAction>().Length - 1, registeredCount);
    }

    [Fact]
    public void ApplyingAnEmptyBindingListUnregistersExistingHotKeys()
    {
        using var completed = new ManualResetEventSlim();
        Exception? exception = null;
        var registeredBeforeClear = 0;
        var registeredAfterClear = -1;

        var thread = new Thread(() =>
        {
            try
            {
                using var manager = new GlobalHotKeyManager();
                var binding = new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.Control |
                        HotKeyModifiers.Alt |
                        HotKeyModifiers.Shift,
                        0x87));

                Assert.True(manager.Apply([binding]).IsSuccess);
                registeredBeforeClear = manager.RegisteredBindings.Count;
                Assert.True(manager.Apply([]).IsSuccess);
                registeredAfterClear = manager.RegisteredBindings.Count;
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
        Assert.Equal(1, registeredBeforeClear);
        Assert.Equal(0, registeredAfterClear);
    }

    [Fact]
    public void KeyboardCaptureConsumesKeyMessagesAndReportsOnlyKeyDown()
    {
        using var completed = new ManualResetEventSlim();
        Exception? exception = null;
        var inputs = new List<HotKeyCaptureInputEventArgs>();
        var consumedBeforeCapture = true;
        var consumedAltDown = false;
        var consumedKeyDown = false;
        var consumedKeyUp = false;
        var consumedAltUp = false;
        var consumedAfterCapture = true;

        var thread = new Thread(() =>
        {
            try
            {
                using var manager = new GlobalHotKeyManager();
                manager.HotKeyCaptureInputReceived += (_, eventArgs) =>
                    inputs.Add(eventArgs);

                consumedBeforeCapture = manager.ProcessKeyboardInputForCapture(
                    'A',
                    isKeyDown: true);
                manager.BeginKeyboardCapture();
                consumedAltDown = manager.ProcessKeyboardInputForCapture(
                    virtualKey: 0x12,
                    isKeyDown: true);
                consumedKeyDown = manager.ProcessKeyboardInputForCapture(
                    'A',
                    isKeyDown: true);
                consumedKeyUp = manager.ProcessKeyboardInputForCapture(
                    'A',
                    isKeyDown: false);
                consumedAltUp = manager.ProcessKeyboardInputForCapture(
                    virtualKey: 0x12,
                    isKeyDown: false);
                manager.EndKeyboardCapture();
                consumedAfterCapture = manager.ProcessKeyboardInputForCapture(
                    'A',
                    isKeyDown: true);
            }
            catch (Exception caughtException)
            {
                exception = caughtException;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(exception);
        Assert.False(consumedBeforeCapture);
        Assert.True(consumedAltDown);
        Assert.True(consumedKeyDown);
        Assert.True(consumedKeyUp);
        Assert.True(consumedAltUp);
        Assert.False(consumedAfterCapture);
        var input = Assert.Single(inputs);
        Assert.Equal((uint)'A', input.VirtualKey);
        Assert.Equal(HotKeyModifiers.Alt, input.Modifiers);
    }

    private static HotKeyBinding[] CreateBindings(
        HotKeyModifiers modifiers,
        uint firstVirtualKey)
    {
        return Enum.GetValues<HotKeyAction>()
            .Select((action, index) => new HotKeyBinding(
                action,
                new HotKeyGesture(modifiers, firstVirtualKey + (uint)index)))
            .ToArray();
    }
}
