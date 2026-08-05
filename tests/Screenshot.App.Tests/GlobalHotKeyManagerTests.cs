using System.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class GlobalHotKeyManagerTests
{
    [Fact]
    public void MouseReplayDepthBypassesShortcutProcessingWithoutDriverMarker()
    {
        Assert.True(GlobalHotKeyManager.ShouldBypassMouseShortcutProcessing(
            IntPtr.Zero,
            replayDepth: 1));
        Assert.False(GlobalHotKeyManager.ShouldBypassMouseShortcutProcessing(
            IntPtr.Zero,
            replayDepth: 0));
    }

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

    [Fact]
    public void MouseCaptureConsumesAndReportsMouseButtons()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            HotKeyCaptureInputEventArgs? input = null;
            manager.HotKeyCaptureInputReceived += (_, eventArgs) =>
                input = eventArgs;

            Assert.False(manager.ProcessMouseInputForCapture(
                HotKeyGesture.VirtualKeyMouseBack,
                HotKeyModifiers.None));
            manager.BeginKeyboardCapture();
            Assert.True(manager.ProcessMouseInputForCapture(
                HotKeyGesture.VirtualKeyMouseBack,
                HotKeyModifiers.Control));
            manager.EndKeyboardCapture();

            Assert.NotNull(input);
            Assert.Equal(HotKeyGesture.VirtualKeyMouseBack, input.VirtualKey);
            Assert.Equal(HotKeyModifiers.Control, input.Modifiers);
        });
    }

    [Fact]
    public void FindsMouseBindingsWithTheExpectedTriggerMode()
    {
        var bindings = new[]
        {
            new HotKeyBinding(
                HotKeyAction.RegionCapture,
                new HotKeyGesture(
                    HotKeyModifiers.None,
                    HotKeyGesture.VirtualKeyMouseLeft)),
            new HotKeyBinding(
                HotKeyAction.RecognizeText,
                new HotKeyGesture(
                    HotKeyModifiers.Control,
                    HotKeyGesture.VirtualKeyMouseLeft)),
        };

        var hold = GlobalHotKeyManager.FindMouseBinding(
            bindings,
            HotKeyGesture.VirtualKeyMouseLeft,
            HotKeyModifiers.None,
            requiresHold: true);
        var modifiedHold = GlobalHotKeyManager.FindMouseBinding(
            bindings,
            HotKeyGesture.VirtualKeyMouseLeft,
            HotKeyModifiers.Control,
            requiresHold: true);

        Assert.Equal(HotKeyAction.RegionCapture, hold?.Action);
        Assert.Equal(HotKeyAction.RecognizeText, modifiedHold?.Action);
        Assert.Null(GlobalHotKeyManager.FindMouseBinding(
            bindings,
            HotKeyGesture.VirtualKeyMouseLeft,
            HotKeyModifiers.Control,
            requiresHold: false));
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(850, 850)]
    [InlineData(5000, 2000)]
    public void ClampsConfigurableMouseLongPressDuration(
        int configuredMilliseconds,
        int expectedMilliseconds)
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(configuredMilliseconds);

            Assert.Equal(
                expectedMilliseconds,
                manager.MouseLongPressDuration.TotalMilliseconds);
        });
    }

    [Fact]
    public void MouseHoldTriggersOnceAfterConfiguredDuration()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(600);
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var triggeredCount = 0;
            manager.HotKeyPressed += (_, eventArgs) =>
            {
                Assert.Equal(HotKeyAction.RegionCapture, eventArgs.Action);
                Assert.Equal(
                    CapturePointerButton.Left,
                    eventArgs.HeldCaptureButton);
                triggeredCount++;
            };

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));

            Assert.Equal(1, triggeredCount);
        });
    }

    [Fact]
    public void LongLeftHoldPassesInitialDownButSuppressesPhysicalRelease()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(600);
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var replayedDown = false;
            var replayedUp = false;
            var triggered = false;
            manager.ReplayPrimaryMouseButtonOverride = (_, includeButtonUp) =>
                replayedDown = !includeButtonUp;
            manager.ReplayPrimaryMouseButtonUpOverride = _ => replayedUp = true;
            manager.HotKeyPressed += (_, _) => triggered = true;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true));
            Assert.False(replayedDown);
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

            Assert.True(triggered);
            Assert.True(replayedUp);
            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false));
        });
    }

    [Fact]
    public void MouseHoldContinuationKeepsOriginalButtonDownPoint()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(600);
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            CapturePointerContinuation? continuation = null;
            manager.HotKeyPressed += (_, eventArgs) =>
                continuation = eventArgs.CapturePointerContinuation;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                x: 137,
                y: 241));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

            Assert.NotNull(continuation);
            Assert.Equal(
                new System.Drawing.Point(137, 241),
                continuation.StartScreenPoint);
        });
    }

    [Fact]
    public void VideoRecordingMouseHoldKeepsOriginalButtonDownPoint()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(600);
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.VideoRecording,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            CapturePointerContinuation? continuation = null;
            manager.HotKeyPressed += (_, eventArgs) =>
                continuation = eventArgs.CapturePointerContinuation;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                x: 211,
                y: 307));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

            Assert.NotNull(continuation);
            Assert.Equal(
                new System.Drawing.Point(211, 307),
                continuation.StartScreenPoint);
        });
    }

    [Fact]
    public void ModifiedMouseHoldContinuationKeepsButtonDownPoint()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.Control,
                        HotKeyGesture.VirtualKeyMouseRight)),
            ]).IsSuccess);
            CapturePointerContinuation? continuation = null;
            manager.HotKeyPressed += (_, eventArgs) =>
                continuation = eventArgs.CapturePointerContinuation;

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseRight,
                isButtonDown: true,
                x: 319,
                y: 427,
                modifiers: HotKeyModifiers.Control));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));

            Assert.NotNull(continuation);
            Assert.Equal(
                new System.Drawing.Point(319, 427),
                continuation.StartScreenPoint);
        });
    }

    [Fact]
    public void MouseCaptureShortcutPassesHeldButtonAndReleasesMouseUp()
    {
        GlobalHotKeyManager? manager = null;
        CapturePointerButton? heldButton = null;
        var replayedClickCount = 0;
        using var triggered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        WpfTestHost.Invoke(() =>
        {
            manager = new GlobalHotKeyManager();
            manager.ReplayPrimaryMouseButtonOverride = (_, _) =>
                replayedClickCount++;
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.Control,
                        HotKeyGesture.VirtualKeyMouseRight)),
            ]).IsSuccess);
            manager.HotKeyPressed += (_, eventArgs) =>
            {
                heldButton = eventArgs.HeldCaptureButton;
                Assert.NotNull(eventArgs.CapturePointerContinuation);
                _ = eventArgs.CapturePointerContinuation
                    .WaitForReleaseAsync()
                    .ContinueWith(
                        _ => released.Set(),
                        TaskScheduler.Default);
                triggered.Set();
            };

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseRight,
                isButtonDown: true,
                modifiers: HotKeyModifiers.Control));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
        });

        Assert.True(triggered.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(CapturePointerButton.Right, heldButton);
            manager!.SetMouseShortcutsSuspended(true);
            Assert.True(manager!.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseRight,
                isButtonDown: false,
                modifiers: HotKeyModifiers.Control));
        });
        Assert.True(released.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(0, replayedClickCount);
            manager!.SetMouseShortcutsSuspended(false);
            manager!.Dispose();
        });
    }

    [Fact]
    public void SuspendedMouseShortcutsPassThroughWithoutTriggering()
    {
        GlobalHotKeyManager? manager = null;
        var triggeredCount = 0;
        using var triggered = new ManualResetEventSlim();
        WpfTestHost.Invoke(() =>
        {
            manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.Control,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            manager.HotKeyPressed += (_, _) =>
            {
                triggeredCount++;
                triggered.Set();
            };

            manager.SetMouseShortcutsSuspended(true);
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                modifiers: HotKeyModifiers.Control));
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                modifiers: HotKeyModifiers.Control));
            Assert.Equal(0, triggeredCount);

            manager.SetMouseShortcutsSuspended(false);
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                modifiers: HotKeyModifiers.Control));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
        });

        Assert.True(triggered.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(1, triggeredCount);
            Assert.True(manager!.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                modifiers: HotKeyModifiers.Control));
            manager.Dispose();
        });
    }

    [Fact]
    public void ModifiedMouseHoldDoesNotTriggerAfterEarlyRelease()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.Control,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var triggered = false;
            var replayedDown = false;
            manager.ReplayPrimaryMouseButtonOverride = (_, includeButtonUp) =>
                replayedDown = !includeButtonUp;
            manager.HotKeyPressed += (_, _) => triggered = true;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                modifiers: HotKeyModifiers.Control));
            Assert.False(replayedDown);
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                modifiers: HotKeyModifiers.Control));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));

            Assert.False(triggered);
        });
    }

    [Fact]
    public void ShortClickPassesThroughWithoutSyntheticMouseInput()
    {
        var order = new List<string>();
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            manager.ReplayPrimaryMouseButtonOverride = (_, includeButtonUp) =>
                order.Add(includeButtonUp ? "full-click" : "down");

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true));
            order.Add("physical-up");
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false));

            Assert.Equal(["physical-up"], order);
        });
    }

    [Fact]
    public void DoubleClickPassesBothNativeClickSequencesThrough()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.RegionCapture,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var replayedInputCount = 0;
            var triggered = false;
            manager.ReplayPrimaryMouseButtonOverride = (_, _) =>
                replayedInputCount++;
            manager.ReplayPrimaryMouseButtonUpOverride = _ =>
                replayedInputCount++;
            manager.HotKeyPressed += (_, _) => triggered = true;

            for (var click = 0; click < 2; click++)
            {
                Assert.False(manager.ProcessMouseButtonInputForTest(
                    HotKeyGesture.VirtualKeyMouseLeft,
                    isButtonDown: true));
                Assert.False(manager.ProcessMouseButtonInputForTest(
                    HotKeyGesture.VirtualKeyMouseLeft,
                    isButtonDown: false));
            }

            Assert.Equal(0, replayedInputCount);
            Assert.False(triggered);
        });
    }

    [Fact]
    public void ModifiedMouseHoldDoesNotTriggerAfterDragging()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.Control,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var triggered = false;
            var replayedDown = false;
            manager.ReplayPrimaryMouseButtonOverride = (_, includeButtonUp) =>
                replayedDown = !includeButtonUp;
            manager.HotKeyPressed += (_, _) => triggered = true;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                x: 100,
                y: 100,
                modifiers: HotKeyModifiers.Control));
            Assert.False(replayedDown);
            manager.ProcessMouseMoveForTest(x: 120, y: 100);
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                x: 120,
                y: 100,
                modifiers: HotKeyModifiers.Control));

            Assert.False(triggered);
        });
    }

    [Fact]
    public void OnePixelMovementCancelsMouseLongPress()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseLeft)),
            ]).IsSuccess);
            var triggered = false;
            manager.HotKeyPressed += (_, _) => triggered = true;

            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                x: 100,
                y: 100));
            manager.ProcessMouseMoveForTest(x: 101, y: 100);
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
            Assert.False(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                x: 101,
                y: 100));

            Assert.False(triggered);
        });
    }

    [Fact]
    public void MouseSideButtonTriggersImmediatelyByDefault()
    {
        GlobalHotKeyManager? manager = null;
        var triggeredCount = 0;
        using var triggered = new ManualResetEventSlim();
        WpfTestHost.Invoke(() =>
        {
            manager = new GlobalHotKeyManager();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseBack)),
            ]).IsSuccess);
            manager.HotKeyPressed += (_, eventArgs) =>
            {
                Assert.Equal(HotKeyAction.OpenSettings, eventArgs.Action);
                triggeredCount++;
                triggered.Set();
            };

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: true));
        });

        Assert.True(triggered.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(1, triggeredCount);
            manager!.Dispose();
        });
    }

    [Fact]
    public void MouseSideButtonShortPressReplaysOriginalClickWithoutTriggering()
    {
        GlobalHotKeyManager? manager = null;
        var triggeredCount = 0;
        uint? replayedButton = null;
        using var replayed = new ManualResetEventSlim();
        WpfTestHost.Invoke(() =>
        {
            manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(700, sideButtonsUseLongPress: true);
            manager.ReplayMouseSideButtonOverride = virtualKey =>
            {
                replayedButton = virtualKey;
                replayed.Set();
            };
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseForward)),
            ]).IsSuccess);
            manager.HotKeyPressed += (_, _) => triggeredCount++;

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseForward,
                isButtonDown: true));
            Assert.Equal(0, triggeredCount);
            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseForward,
                isButtonDown: false));
            Assert.Equal(0, triggeredCount);
        });

        Assert.True(replayed.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(0, triggeredCount);
            Assert.Equal(
                HotKeyGesture.VirtualKeyMouseForward,
                replayedButton);
            manager!.Dispose();
        });
    }

    [Fact]
    public void MouseSideButtonLongPressDoesNotTriggerAgainOnRelease()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(600, sideButtonsUseLongPress: true);
            var replayedCount = 0;
            manager.ReplayMouseSideButtonOverride = _ => replayedCount++;
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseBack)),
            ]).IsSuccess);
            var triggeredCount = 0;
            manager.HotKeyPressed += (_, _) => triggeredCount++;

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: true));
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
            Assert.Equal(1, triggeredCount);
            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: false));
            Assert.Equal(1, triggeredCount);
            Assert.Equal(0, replayedCount);
        });
    }

    [Fact]
    public void MovingDuringMouseSideButtonHoldCancelsShortcutAndReplaysClick()
    {
        GlobalHotKeyManager? manager = null;
        var triggeredCount = 0;
        using var replayed = new ManualResetEventSlim();
        WpfTestHost.Invoke(() =>
        {
            manager = new GlobalHotKeyManager();
            manager.ConfigureMouseLongPress(700, sideButtonsUseLongPress: true);
            manager.ReplayMouseSideButtonOverride = _ => replayed.Set();
            Assert.True(manager.Apply(
            [
                new HotKeyBinding(
                    HotKeyAction.OpenSettings,
                    new HotKeyGesture(
                        HotKeyModifiers.None,
                        HotKeyGesture.VirtualKeyMouseBack)),
            ]).IsSuccess);
            manager.HotKeyPressed += (_, _) => triggeredCount++;

            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: true,
                x: 100,
                y: 100));
            manager.ProcessMouseMoveForTest(x: 120, y: 100);
            manager.ProcessPendingMouseHolds(
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2));
            Assert.True(manager.ProcessMouseButtonInputForTest(
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: false));
        });

        Assert.True(replayed.Wait(TimeSpan.FromSeconds(2)));
        WpfTestHost.Invoke(() =>
        {
            Assert.Equal(0, triggeredCount);
            manager!.Dispose();
        });
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
