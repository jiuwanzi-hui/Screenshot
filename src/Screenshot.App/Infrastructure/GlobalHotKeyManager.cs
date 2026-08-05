using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record HotKeyRegistrationResult(bool IsSuccess, string? ErrorMessage)
{
    public static HotKeyRegistrationResult Success { get; } = new(true, ErrorMessage: null);
}

public sealed class HotKeyPressedEventArgs : EventArgs
{
    private CapturedImage? _preCapturedScreen;

    public HotKeyPressedEventArgs(
        HotKeyAction action,
        CapturedImage? preCapturedScreen = null,
        CapturePointerContinuation? capturePointerContinuation = null)
    {
        Action = action;
        _preCapturedScreen = preCapturedScreen;
        CapturePointerContinuation = capturePointerContinuation;
    }

    public HotKeyAction Action { get; }

    public CapturePointerContinuation? CapturePointerContinuation { get; }

    public CapturePointerButton? HeldCaptureButton =>
        CapturePointerContinuation?.Button;

    public CapturedImage? DetachPreCapturedScreen()
    {
        var snapshot = _preCapturedScreen;
        _preCapturedScreen = null;
        return snapshot;
    }

    internal void DisposeUnusedPreCapturedScreen()
    {
        _preCapturedScreen?.Dispose();
        _preCapturedScreen = null;
    }
}

public sealed class HotKeyCaptureInputEventArgs : EventArgs
{
    public HotKeyCaptureInputEventArgs(
        uint virtualKey,
        HotKeyModifiers modifiers)
        : this(new HotKeyGesture(modifiers, virtualKey))
    {
    }

    public HotKeyCaptureInputEventArgs(HotKeyGesture gesture)
    {
        Gesture = gesture;
    }

    public HotKeyGesture Gesture { get; }

    public uint VirtualKey => Gesture.VirtualKey;

    public HotKeyModifiers Modifiers => Gesture.Modifiers;
}

public sealed class GlobalHotKeyManager : IDisposable
{
    private const int HotKeyAlreadyRegisteredError = 1409;
    private const int WindowMessageHotKey = 0x0312;
    private const int MessageOnlyWindow = -3;
    private const int LowLevelKeyboardHook = 13;
    private const int LowLevelMouseHook = 14;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageKeyUp = 0x0101;
    private const int WindowMessageSystemKeyDown = 0x0104;
    private const int WindowMessageSystemKeyUp = 0x0105;
    private const int WindowMessageMouseMove = 0x0200;
    private const int WindowMessageLeftButtonDown = 0x0201;
    private const int WindowMessageLeftButtonUp = 0x0202;
    private const int WindowMessageRightButtonDown = 0x0204;
    private const int WindowMessageRightButtonUp = 0x0205;
    private const int WindowMessageMiddleButtonDown = 0x0207;
    private const int WindowMessageMiddleButtonUp = 0x0208;
    private const int WindowMessageXButtonDown = 0x020B;
    private const int WindowMessageXButtonUp = 0x020C;
    private const uint XButtonBack = 1;
    private const uint XButtonForward = 2;
    private static readonly IntPtr ReplayedSideButtonExtraInfo =
        new(0x534E4150);
    private static readonly IntPtr ReplayedPrimaryButtonExtraInfo =
        new(0x534E4151);
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyAlt = 0x12;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyRightAlt = 0xA5;
    private static readonly TimeSpan PreCaptureLifetime = TimeSpan.FromSeconds(2);

    private readonly HwndSource _messageSource;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly NativeMethods.LowLevelMouseProcedure _mouseProcedure;
    private readonly DispatcherTimer _preCaptureExpiryTimer;
    private readonly DispatcherTimer _mouseHoldTimer;
    private readonly Dictionary<int, HotKeyBinding> _registeredBindings = [];
    private readonly HashSet<HotKeyAction> _preCapturedActions = [];
    private readonly HashSet<uint> _capturedModifierKeysDown = [];
    private readonly Dictionary<uint, PendingMouseHold> _pendingMouseHolds = [];
    private readonly HashSet<uint> _suppressedMouseButtonsUntilUp = [];
    private readonly HashSet<uint> _sideButtonsToReplayUntilUp = [];
    private readonly HashSet<uint> _primaryButtonsToReplayUntilUp = [];
    private readonly HashSet<uint> _primaryButtonsPassedThroughForHold = [];
    private readonly Dictionary<uint, CapturePointerContinuation>
        _capturePointerContinuations = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly int _mouseHookErrorCode;
    private CapturedImage? _preCapturedScreen;
    private DateTimeOffset _preCapturedAt;
    private TimeSpan _mouseLongPressDuration = TimeSpan.FromMilliseconds(700);
    private bool _mouseSideButtonsUseLongPress;
    private bool _areMouseShortcutsSuspended;
    private bool _isKeyboardCaptureActive;
    private int _mouseReplayDepth;
    private bool _disposed;

    public GlobalHotKeyManager()
    {
        var parameters = new HwndSourceParameters("Screenshot.App.HotKeySink")
        {
            ParentWindow = new IntPtr(MessageOnlyWindow),
            WindowStyle = 0,
        };

        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WindowProcedure);
        _preCaptureExpiryTimer = new DispatcherTimer
        {
            Interval = PreCaptureLifetime,
        };
        _preCaptureExpiryTimer.Tick += OnPreCaptureExpired;
        _mouseHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _mouseHoldTimer.Tick += OnMouseHoldTimerTick;
        _keyboardProcedure = OnLowLevelKeyboardMessage;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            LowLevelKeyboardHook,
            _keyboardProcedure,
            NativeMethods.GetModuleHandle(moduleName: null),
            threadId: 0);
        _mouseProcedure = OnLowLevelMouseMessage;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            LowLevelMouseHook,
            _mouseProcedure,
            NativeMethods.GetModuleHandle(moduleName: null),
            threadId: 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHookErrorCode = Marshal.GetLastWin32Error();
        }
    }

    public event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    public event EventHandler<HotKeyCaptureInputEventArgs>? HotKeyCaptureInputReceived;

    public IReadOnlyList<HotKeyBinding> RegisteredBindings =>
        _registeredBindings.Values.OrderBy(binding => binding.Action).ToArray();

    internal bool IsKeyboardCaptureActive => _isKeyboardCaptureActive;

    internal Action<uint>? ReplayMouseSideButtonOverride { get; set; }

    internal Action<uint, bool>? ReplayPrimaryMouseButtonOverride { get; set; }

    internal Action<uint>? ReplayPrimaryMouseButtonUpOverride { get; set; }

    public TimeSpan MouseLongPressDuration => _mouseLongPressDuration;

    public void ConfigureMouseLongPress(
        int milliseconds,
        bool sideButtonsUseLongPress = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mouseLongPressDuration = TimeSpan.FromMilliseconds(
            Math.Clamp(milliseconds, 300, 2000));
        _mouseSideButtonsUseLongPress = sideButtonsUseLongPress;
    }

    public void SetMouseShortcutsSuspended(bool suspended)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _areMouseShortcutsSuspended = suspended;
        if (!suspended)
        {
            return;
        }

        _pendingMouseHolds.Clear();
        _primaryButtonsPassedThroughForHold.Clear();
        _mouseHoldTimer.Stop();
    }

    public void BeginKeyboardCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearPreCapturedScreen();
        _capturedModifierKeysDown.Clear();
        _isKeyboardCaptureActive = true;
    }

    public void EndKeyboardCapture()
    {
        _isKeyboardCaptureActive = false;
        _capturedModifierKeysDown.Clear();
    }

    public IReadOnlyList<HotKeyBinding> SuspendRegistrations()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bindings = RegisteredBindings;
        UnregisterAll();
        return bindings;
    }

    public HotKeyRegistrationResult Apply(IReadOnlyList<HotKeyBinding> requestedBindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var validation = HotKeyConfiguration.Validate(requestedBindings);

        if (!validation.IsValid)
        {
            return new HotKeyRegistrationResult(false, validation.ErrorMessage);
        }

        var previousBindings = RegisteredBindings;
        UnregisterAll();

        if (TryRegisterAll(requestedBindings, out var registrationError))
        {
            return HotKeyRegistrationResult.Success;
        }

        UnregisterAll();
        _ = TryRegisterAll(previousBindings, out _);
        return new HotKeyRegistrationResult(false, registrationError);
    }

    public HotKeyRegistrationResult ApplyAvailable(
        IReadOnlyList<HotKeyBinding> requestedBindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var validation = HotKeyConfiguration.Validate(requestedBindings);

        if (!validation.IsValid)
        {
            return new HotKeyRegistrationResult(false, validation.ErrorMessage);
        }

        UnregisterAll();
        return RegisterAvailable(requestedBindings);
    }

    public HotKeyRegistrationResult RestoreRegistrations(
        IReadOnlyList<HotKeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();
        return RegisterAvailable(bindings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndKeyboardCapture();
        UnregisterAll();
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseHoldTimer.Stop();
        _mouseHoldTimer.Tick -= OnMouseHoldTimerTick;
        _pendingMouseHolds.Clear();
        _suppressedMouseButtonsUntilUp.Clear();
        _sideButtonsToReplayUntilUp.Clear();
        _primaryButtonsToReplayUntilUp.Clear();
        _primaryButtonsPassedThroughForHold.Clear();
        foreach (var continuation in _capturePointerContinuations.Values)
        {
            continuation.NotifyReleased();
        }
        _capturePointerContinuations.Clear();
        _preCaptureExpiryTimer.Stop();
        _preCaptureExpiryTimer.Tick -= OnPreCaptureExpired;
        ClearPreCapturedScreen();
        _messageSource.RemoveHook(WindowProcedure);
        _messageSource.Dispose();
    }

    private bool TryRegisterAll(
        IReadOnlyList<HotKeyBinding> bindings,
        out string? registrationError)
    {
        foreach (var binding in bindings)
        {
            if (!TryRegister(binding, out registrationError))
            {
                return false;
            }
        }

        registrationError = null;
        return true;
    }

    private HotKeyRegistrationResult RegisterAvailable(
        IReadOnlyList<HotKeyBinding> bindings)
    {
        var errors = new List<string>();

        foreach (var binding in bindings)
        {
            if (!TryRegister(binding, out var registrationError) &&
                registrationError is not null)
            {
                errors.Add(registrationError);
            }
        }

        return errors.Count == 0
            ? HotKeyRegistrationResult.Success
            : new HotKeyRegistrationResult(false, string.Join(Environment.NewLine, errors));
    }

    private bool TryRegister(
        HotKeyBinding binding,
        out string? registrationError)
    {
        var identifier = (int)binding.Action;
        if (binding.Gesture.IsMouseButton)
        {
            if (_mouseHook == IntPtr.Zero)
            {
                registrationError =
                    $"无法启用鼠标快捷键 {binding.Gesture}（Windows 错误代码 {_mouseHookErrorCode}）。";
                return false;
            }

            _registeredBindings[identifier] = binding;
            registrationError = null;
            return true;
        }

        var registered = NativeMethods.RegisterHotKey(
            _messageSource.Handle,
            identifier,
            (uint)binding.Gesture.Modifiers,
            binding.Gesture.VirtualKey);

        if (!registered)
        {
            var errorCode = Marshal.GetLastWin32Error();
            registrationError = CreateRegistrationError(binding, errorCode);
            return false;
        }

        _registeredBindings[identifier] = binding;
        registrationError = null;
        return true;
    }

    private void UnregisterAll()
    {
        foreach (var identifier in _registeredBindings.Keys.ToArray())
        {
            if (!_registeredBindings[identifier].Gesture.IsMouseButton)
            {
                _ = NativeMethods.UnregisterHotKey(_messageSource.Handle, identifier);
            }
        }

        _registeredBindings.Clear();
        _pendingMouseHolds.Clear();
        _sideButtonsToReplayUntilUp.Clear();
        _primaryButtonsToReplayUntilUp.Clear();
        _primaryButtonsPassedThroughForHold.Clear();
        _mouseHoldTimer.Stop();
        ClearPreCapturedScreen();
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WindowMessageHotKey &&
            _registeredBindings.TryGetValue(wParam.ToInt32(), out var binding))
        {
            RaiseHotKeyPressed(
                binding.Action,
                TakePreCapturedScreen(binding.Action));

            handled = true;
        }

        return IntPtr.Zero;
    }

    private IntPtr OnLowLevelKeyboardMessage(
        int code,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (code >= 0)
        {
            var keyboardData = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(
                lParam);
            var message = wParam.ToInt32();
            var isKeyDown = message is
                WindowMessageKeyDown or WindowMessageSystemKeyDown;
            var isKeyUp = message is
                WindowMessageKeyUp or WindowMessageSystemKeyUp;

            if ((isKeyDown || isKeyUp) &&
                ProcessKeyboardInputForCapture(
                    keyboardData.VirtualKey,
                    isKeyDown))
            {
                return new IntPtr(1);
            }

            if (isKeyDown)
            {
                TryPreCaptureTransientUi(keyboardData.VirtualKey);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr OnLowLevelMouseMessage(
        int code,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (code >= 0)
        {
            var mouseData = Marshal.PtrToStructure<NativeMethods.LowLevelMouseData>(
                lParam);
            var message = wParam.ToInt32();

            if (ShouldBypassMouseShortcutProcessing(
                    mouseData.ExtraInfo,
                    Volatile.Read(ref _mouseReplayDepth)))
            {
                return NativeMethods.CallNextHookEx(
                    _mouseHook,
                    code,
                    wParam,
                    lParam);
            }

            if (message == WindowMessageMouseMove)
            {
                CancelMovedMouseHolds(mouseData.Point);
            }
            else if (TryGetMouseButton(
                         message,
                         mouseData.MouseData,
                         out var virtualKey,
                         out var isButtonDown) &&
                     ProcessMouseButtonInput(
                         virtualKey,
                         isButtonDown,
                         mouseData.Point))
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool ProcessMouseButtonInput(
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        HotKeyModifiers? modifiersOverride = null)
    {
        if (!isButtonDown &&
            _capturePointerContinuations.Remove(
                virtualKey,
            out var releasedContinuation))
        {
            _ = _messageSource.Dispatcher.BeginInvoke(
                () => releasedContinuation.NotifyReleased());
        }

        if (!isButtonDown && _suppressedMouseButtonsUntilUp.Remove(virtualKey))
        {
            _pendingMouseHolds.Remove(virtualKey);
            StopMouseHoldTimerWhenIdle();
            if (_sideButtonsToReplayUntilUp.Remove(virtualKey))
            {
                _ = _messageSource.Dispatcher.BeginInvoke(
                    () => ReplayMouseSideButton(virtualKey));
            }
            else if (_primaryButtonsToReplayUntilUp.Remove(virtualKey))
            {
                QueueCompletedPrimaryMouseClickReplay(virtualKey);
            }
            return true;
        }

        if (_isKeyboardCaptureActive)
        {
            if (isButtonDown)
            {
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
                ProcessMouseInputForCapture(
                    virtualKey,
                    GetCapturedModifiers());
            }

            return true;
        }

        if (_areMouseShortcutsSuspended)
        {
            if (!isButtonDown)
            {
                _pendingMouseHolds.Remove(virtualKey);
                StopMouseHoldTimerWhenIdle();
            }

            return false;
        }

        if (!isButtonDown)
        {
            _pendingMouseHolds.Remove(virtualKey);
            _primaryButtonsPassedThroughForHold.Remove(virtualKey);
            StopMouseHoldTimerWhenIdle();
            return false;
        }

        var modifiers = modifiersOverride ??
            GetCurrentModifiersIncluding(virtualKey);
        var sideButtonUsesLongPress =
            modifiers == HotKeyModifiers.None &&
            _mouseSideButtonsUseLongPress &&
            IsMouseSideButton(virtualKey);
        var immediateBinding = FindMouseBinding(
            _registeredBindings.Values,
            virtualKey,
            modifiers,
            requiresHold: false);
        if (immediateBinding is not null && !sideButtonUsesLongPress)
        {
            var continuation = CreateCapturePointerContinuation(
                virtualKey,
                immediateBinding.Action,
                point);
            if (continuation is null)
            {
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
            }

            _ = _messageSource.Dispatcher.BeginInvoke(
                () =>
                {
                    RaiseHotKeyPressed(
                        immediateBinding.Action,
                        TakePreCapturedScreen(immediateBinding.Action),
                        continuation);
                });
            return true;
        }

        if (sideButtonUsesLongPress && immediateBinding is not null)
        {
            _suppressedMouseButtonsUntilUp.Add(virtualKey);
            _sideButtonsToReplayUntilUp.Add(virtualKey);
            _pendingMouseHolds[virtualKey] = new PendingMouseHold(
                immediateBinding,
                DateTimeOffset.UtcNow,
                point);
            _mouseHoldTimer.Start();
            return true;
        }

        var holdBinding = FindMouseBinding(
            _registeredBindings.Values,
            virtualKey,
            modifiers,
            requiresHold: true);
        if (holdBinding is null)
        {
            return false;
        }

        _pendingMouseHolds[virtualKey] = new PendingMouseHold(
            holdBinding,
            DateTimeOffset.UtcNow,
            point);

        if (virtualKey == HotKeyGesture.VirtualKeyMouseLeft)
        {
            // Preserve the physical left-button sequence so ordinary clicks,
            // double-clicks, text selection, and window resizing remain native.
            _primaryButtonsPassedThroughForHold.Add(virtualKey);
            _mouseHoldTimer.Start();
            return false;
        }

        _suppressedMouseButtonsUntilUp.Add(virtualKey);
        _primaryButtonsToReplayUntilUp.Add(virtualKey);
        _mouseHoldTimer.Start();
        return true;
    }

    internal bool ProcessMouseButtonInputForTest(
        uint virtualKey,
        bool isButtonDown,
        int x = 0,
        int y = 0,
        HotKeyModifiers? modifiers = null)
    {
        return ProcessMouseButtonInput(
            virtualKey,
            isButtonDown,
            new NativeMethods.NativePoint { X = x, Y = y },
            modifiers);
    }

    internal bool ProcessMouseInputForCapture(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        if (!_isKeyboardCaptureActive || !HotKeyGesture.IsMouseVirtualKey(virtualKey))
        {
            return false;
        }

        HotKeyCaptureInputReceived?.Invoke(
            this,
            new HotKeyCaptureInputEventArgs(
                new HotKeyGesture(modifiers, virtualKey)));
        return true;
    }

    internal static HotKeyBinding? FindMouseBinding(
        IEnumerable<HotKeyBinding> bindings,
        uint virtualKey,
        HotKeyModifiers modifiers,
        bool requiresHold)
    {
        return bindings.FirstOrDefault(binding =>
            binding.Gesture.IsMouseButton &&
            binding.Gesture.VirtualKey == virtualKey &&
            binding.Gesture.Modifiers == modifiers &&
            binding.Gesture.RequiresHold == requiresHold);
    }

    private void CancelMovedMouseHolds(NativeMethods.NativePoint point)
    {
        foreach (var (virtualKey, pending) in _pendingMouseHolds.ToArray())
        {
            if (point.X != pending.StartPoint.X ||
                point.Y != pending.StartPoint.Y)
            {
                _pendingMouseHolds.Remove(virtualKey);
                _primaryButtonsPassedThroughForHold.Remove(virtualKey);
                if (_primaryButtonsToReplayUntilUp.Remove(virtualKey))
                {
                    _suppressedMouseButtonsUntilUp.Remove(virtualKey);
                    ReplayPrimaryMouseButton(
                        virtualKey,
                        includeButtonUp: false);
                }
            }
        }

        StopMouseHoldTimerWhenIdle();
    }

    internal void ProcessMouseMoveForTest(int x, int y)
    {
        CancelMovedMouseHolds(
            new NativeMethods.NativePoint { X = x, Y = y });
    }

    private void OnMouseHoldTimerTick(object? sender, EventArgs e)
    {
        ProcessPendingMouseHolds(DateTimeOffset.UtcNow);
    }

    internal void ProcessPendingMouseHolds(DateTimeOffset now)
    {
        var ready = _pendingMouseHolds
            .Where(pair => now - pair.Value.PressedAt >= _mouseLongPressDuration)
            .ToArray();
        foreach (var (virtualKey, pending) in ready)
        {
            _pendingMouseHolds.Remove(virtualKey);
            _sideButtonsToReplayUntilUp.Remove(virtualKey);
            _primaryButtonsToReplayUntilUp.Remove(virtualKey);
            if (_primaryButtonsPassedThroughForHold.Remove(virtualKey))
            {
                // The target received the native down. Finish that interaction
                // before the capture overlay takes ownership of the still-held
                // physical button, then suppress its eventual native up.
                ReplayPrimaryMouseButtonUp(virtualKey);
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
            }
            var continuation = CreateCapturePointerContinuation(
                virtualKey,
                pending.Binding.Action,
                pending.StartPoint);
            RaiseHotKeyPressed(
                pending.Binding.Action,
                CaptureCurrentScreen(pending.Binding.Action),
                continuation);
        }

        StopMouseHoldTimerWhenIdle();
    }

    private void StopMouseHoldTimerWhenIdle()
    {
        if (_pendingMouseHolds.Count == 0)
        {
            _mouseHoldTimer.Stop();
        }
    }

    private static bool TryGetMouseButton(
        int message,
        uint mouseData,
        out uint virtualKey,
        out bool isButtonDown)
    {
        (virtualKey, isButtonDown) = message switch
        {
            WindowMessageLeftButtonDown =>
                (HotKeyGesture.VirtualKeyMouseLeft, true),
            WindowMessageLeftButtonUp =>
                (HotKeyGesture.VirtualKeyMouseLeft, false),
            WindowMessageRightButtonDown =>
                (HotKeyGesture.VirtualKeyMouseRight, true),
            WindowMessageRightButtonUp =>
                (HotKeyGesture.VirtualKeyMouseRight, false),
            WindowMessageMiddleButtonDown =>
                (HotKeyGesture.VirtualKeyMouseMiddle, true),
            WindowMessageMiddleButtonUp =>
                (HotKeyGesture.VirtualKeyMouseMiddle, false),
            WindowMessageXButtonDown =>
                (GetXButtonVirtualKey(mouseData), true),
            WindowMessageXButtonUp =>
                (GetXButtonVirtualKey(mouseData), false),
            _ => (0u, false),
        };
        return virtualKey != 0;
    }

    private static bool IsMouseSideButton(uint virtualKey) =>
        virtualKey is HotKeyGesture.VirtualKeyMouseBack or
            HotKeyGesture.VirtualKeyMouseForward;

    private static CapturePointerButton? GetCapturePointerButton(
        uint virtualKey)
    {
        return virtualKey switch
        {
            HotKeyGesture.VirtualKeyMouseLeft => CapturePointerButton.Left,
            HotKeyGesture.VirtualKeyMouseRight => CapturePointerButton.Right,
            _ => null,
        };
    }

    private CapturePointerContinuation? CreateCapturePointerContinuation(
        uint virtualKey,
        HotKeyAction action,
        NativeMethods.NativePoint startPoint)
    {
        var captureButton = GetCapturePointerButton(virtualKey);
        if (!captureButton.HasValue || !OpensCaptureSelection(action))
        {
            return null;
        }

        var continuation = new CapturePointerContinuation(
            captureButton.Value,
            new System.Drawing.Point(startPoint.X, startPoint.Y));
        _capturePointerContinuations[virtualKey] = continuation;
        return continuation;
    }

    private static bool OpensCaptureSelection(HotKeyAction action)
    {
        return action is HotKeyAction.RegionCapture or
            HotKeyAction.VideoRecording or
            HotKeyAction.RecognizeText or
            HotKeyAction.PinImage or
            HotKeyAction.ScrollCapture;
    }

    private void ReplayMouseSideButton(uint virtualKey)
    {
        if (ReplayMouseSideButtonOverride is not null)
        {
            ReplayMouseSideButtonOverride(virtualKey);
            return;
        }

        var mouseData = virtualKey switch
        {
            HotKeyGesture.VirtualKeyMouseBack => XButtonBack,
            HotKeyGesture.VirtualKeyMouseForward => XButtonForward,
            _ => 0u,
        };
        if (mouseData == 0)
        {
            return;
        }

        var inputs = new[]
        {
            new NativeMethods.NativeInput
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.NativeInputUnion
                {
                    Mouse = new NativeMethods.NativeMouseInput
                    {
                        MouseData = mouseData,
                        Flags = NativeMethods.MouseEventXDown,
                        ExtraInfo = ReplayedSideButtonExtraInfo,
                    },
                },
            },
            new NativeMethods.NativeInput
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.NativeInputUnion
                {
                    Mouse = new NativeMethods.NativeMouseInput
                    {
                        MouseData = mouseData,
                        Flags = NativeMethods.MouseEventXUp,
                        ExtraInfo = ReplayedSideButtonExtraInfo,
                    },
                },
            },
        };
        SendReplayedMouseInputs(inputs);
    }

    private void ReplayPrimaryMouseButton(
        uint virtualKey,
        bool includeButtonUp)
    {
        if (ReplayPrimaryMouseButtonOverride is not null)
        {
            ReplayPrimaryMouseButtonOverride(virtualKey, includeButtonUp);
            return;
        }

        var (downFlag, upFlag) = virtualKey switch
        {
            HotKeyGesture.VirtualKeyMouseLeft =>
                (NativeMethods.MouseEventLeftDown, NativeMethods.MouseEventLeftUp),
            HotKeyGesture.VirtualKeyMouseRight =>
                (NativeMethods.MouseEventRightDown, NativeMethods.MouseEventRightUp),
            HotKeyGesture.VirtualKeyMouseMiddle =>
                (NativeMethods.MouseEventMiddleDown, NativeMethods.MouseEventMiddleUp),
            _ => (0u, 0u),
        };
        if (downFlag == 0)
        {
            return;
        }

        var inputs = new NativeMethods.NativeInput[includeButtonUp ? 2 : 1];
        inputs[0] = CreateReplayedPrimaryMouseInput(downFlag);
        if (includeButtonUp)
        {
            inputs[1] = CreateReplayedPrimaryMouseInput(upFlag);
        }

        SendReplayedMouseInputs(inputs);
    }

    private void QueueCompletedPrimaryMouseClickReplay(uint virtualKey)
    {
        // SendInput from inside WH_MOUSE_LL can interleave the synthetic click
        // with the physical button-up that is still being suppressed. Replaying
        // after the hook returns preserves Windows' normal double-click order.
        _ = _messageSource.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            () => ReplayPrimaryMouseButton(
                virtualKey,
                includeButtonUp: true));
    }

    private void ReplayPrimaryMouseButtonUp(uint virtualKey)
    {
        if (ReplayPrimaryMouseButtonUpOverride is not null)
        {
            ReplayPrimaryMouseButtonUpOverride(virtualKey);
            return;
        }

        var upFlag = virtualKey switch
        {
            HotKeyGesture.VirtualKeyMouseLeft => NativeMethods.MouseEventLeftUp,
            HotKeyGesture.VirtualKeyMouseRight => NativeMethods.MouseEventRightUp,
            HotKeyGesture.VirtualKeyMouseMiddle => NativeMethods.MouseEventMiddleUp,
            _ => 0u,
        };
        if (upFlag == 0)
        {
            return;
        }

        SendReplayedMouseInputs([CreateReplayedPrimaryMouseInput(upFlag)]);
    }

    internal static bool ShouldBypassMouseShortcutProcessing(
        IntPtr extraInfo,
        int replayDepth)
    {
        return replayDepth > 0 ||
            extraInfo == ReplayedSideButtonExtraInfo ||
            extraInfo == ReplayedPrimaryButtonExtraInfo;
    }

    private void SendReplayedMouseInputs(
        NativeMethods.NativeInput[] inputs)
    {
        Interlocked.Increment(ref _mouseReplayDepth);
        try
        {
            _ = NativeMethods.SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf<NativeMethods.NativeInput>());
        }
        finally
        {
            Interlocked.Decrement(ref _mouseReplayDepth);
        }
    }

    private static NativeMethods.NativeInput CreateReplayedPrimaryMouseInput(
        uint flags)
    {
        return new NativeMethods.NativeInput
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.NativeInputUnion
            {
                Mouse = new NativeMethods.NativeMouseInput
                {
                    Flags = flags,
                    ExtraInfo = ReplayedPrimaryButtonExtraInfo,
                },
            },
        };
    }

    private static uint GetXButtonVirtualKey(uint mouseData)
    {
        return (mouseData >> 16) switch
        {
            XButtonBack => HotKeyGesture.VirtualKeyMouseBack,
            XButtonForward => HotKeyGesture.VirtualKeyMouseForward,
            _ => 0,
        };
    }

    internal bool ProcessKeyboardInputForCapture(
        uint virtualKey,
        bool isKeyDown)
    {
        if (!_isKeyboardCaptureActive)
        {
            return false;
        }

        if (IsModifierKey(virtualKey))
        {
            if (isKeyDown)
            {
                _capturedModifierKeysDown.Add(virtualKey);
            }
            else
            {
                _capturedModifierKeysDown.Remove(virtualKey);
            }

            return true;
        }

        if (isKeyDown)
        {
            HotKeyCaptureInputReceived?.Invoke(
                this,
                new HotKeyCaptureInputEventArgs(
                    virtualKey,
                    GetCapturedModifiers()));
        }

        return true;
    }

    private HotKeyModifiers GetCapturedModifiers()
    {
        var modifiers = HotKeyModifiers.None;
        if (_capturedModifierKeysDown.Any(IsControlKey))
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (_capturedModifierKeysDown.Any(IsAltKey))
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (_capturedModifierKeysDown.Any(IsShiftKey))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (_capturedModifierKeysDown.Any(IsWindowsKey))
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return modifiers;
    }

    private void TryPreCaptureTransientUi(uint virtualKey)
    {
        var modifiers = GetCurrentModifiersIncluding(virtualKey);
        var candidates = GetPreCaptureActions(
            _registeredBindings.Values,
            virtualKey,
            modifiers);
        if (candidates.Count == 0)
        {
            return;
        }

        if (_preCapturedScreen is not null &&
            DateTimeOffset.UtcNow - _preCapturedAt <= PreCaptureLifetime &&
            candidates.All(_preCapturedActions.Contains))
        {
            return;
        }

        ClearPreCapturedScreen();
        try
        {
            _preCapturedScreen = ScreenCaptureService.Capture(VirtualScreen.GetBounds());
            _preCapturedAt = DateTimeOffset.UtcNow;
            _preCaptureExpiryTimer.Stop();
            _preCaptureExpiryTimer.Start();
            foreach (var action in candidates)
            {
                _preCapturedActions.Add(action);
            }
        }
        catch
        {
            ClearPreCapturedScreen();
        }
    }

    internal static IReadOnlyList<HotKeyAction> GetPreCaptureActions(
        IEnumerable<HotKeyBinding> bindings,
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        var isModifier = IsModifierKey(virtualKey);
        var isAltKey = virtualKey is
            VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt;
        return bindings
            .Where(binding => IsTransientUiCaptureAction(binding.Action))
            .Where(binding => isAltKey
                ? binding.Gesture.Modifiers.HasFlag(HotKeyModifiers.Alt)
                : binding.Gesture.Modifiers == modifiers)
            .Where(binding => isModifier || binding.Gesture.VirtualKey == virtualKey)
            .Select(binding => binding.Action)
            .Distinct()
            .ToArray();
    }

    private CapturedImage? TakePreCapturedScreen(HotKeyAction action)
    {
        if (_preCapturedScreen is null ||
            !_preCapturedActions.Contains(action) ||
            DateTimeOffset.UtcNow - _preCapturedAt > PreCaptureLifetime)
        {
            ClearPreCapturedScreen();
            return null;
        }

        var snapshot = _preCapturedScreen;
        _preCapturedScreen = null;
        _preCapturedActions.Clear();
        _preCaptureExpiryTimer.Stop();
        return snapshot;
    }

    private static CapturedImage? CaptureCurrentScreen(HotKeyAction action)
    {
        if (!IsTransientUiCaptureAction(action))
        {
            return null;
        }

        try
        {
            return ScreenCaptureService.Capture(VirtualScreen.GetBounds());
        }
        catch
        {
            return null;
        }
    }

    private void RaiseHotKeyPressed(
        HotKeyAction action,
        CapturedImage? preCapturedScreen,
        CapturePointerContinuation? capturePointerContinuation = null)
    {
        var eventArgs = new HotKeyPressedEventArgs(
            action,
            preCapturedScreen,
            capturePointerContinuation);
        try
        {
            HotKeyPressed?.Invoke(this, eventArgs);
        }
        finally
        {
            eventArgs.DisposeUnusedPreCapturedScreen();
        }
    }

    private void ClearPreCapturedScreen()
    {
        _preCapturedScreen?.Dispose();
        _preCapturedScreen = null;
        _preCapturedActions.Clear();
        _preCapturedAt = default;
        _preCaptureExpiryTimer.Stop();
    }

    private void OnPreCaptureExpired(object? sender, EventArgs e)
    {
        ClearPreCapturedScreen();
    }

    internal static bool IsTransientUiCaptureAction(HotKeyAction action)
    {
        return action is HotKeyAction.RegionCapture or HotKeyAction.RecognizeText;
    }

    private static HotKeyModifiers GetCurrentModifiersIncluding(uint virtualKey)
    {
        var modifiers = HotKeyModifiers.None;
        if (IsKeyDown(VirtualKeyControl) ||
            virtualKey is VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl)
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKeyAlt) ||
            virtualKey is VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt)
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKeyShift) ||
            virtualKey is VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift)
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKeyLeftWindows) ||
            IsKeyDown(VirtualKeyRightWindows) ||
            virtualKey is VirtualKeyLeftWindows or VirtualKeyRightWindows)
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsModifierKey(uint virtualKey)
    {
        return IsShiftKey(virtualKey) ||
            IsControlKey(virtualKey) ||
            IsAltKey(virtualKey) ||
            IsWindowsKey(virtualKey);
    }

    private static bool IsShiftKey(uint virtualKey) =>
        virtualKey is VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift;

    private static bool IsControlKey(uint virtualKey) =>
        virtualKey is VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl;

    private static bool IsAltKey(uint virtualKey) =>
        virtualKey is VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt;

    private static bool IsWindowsKey(uint virtualKey) =>
        virtualKey is VirtualKeyLeftWindows or VirtualKeyRightWindows;

    private static bool IsKeyDown(uint virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    private static string CreateRegistrationError(HotKeyBinding binding, int errorCode)
    {
        if (errorCode == HotKeyAlreadyRegisteredError)
        {
            return $"快捷键 {binding.Gesture} 已被其他应用占用。Windows 无法自动修改对方设置，请释放该快捷键或改用其他组合键。";
        }

        return $"无法注册快捷键 {binding.Gesture}（Windows 错误代码 {errorCode}）。";
    }

    private sealed record PendingMouseHold(
        HotKeyBinding Binding,
        DateTimeOffset PressedAt,
        NativeMethods.NativePoint StartPoint);

    private static class NativeMethods
    {
        public const uint InputMouse = 0;
        public const uint MouseEventLeftDown = 0x0002;
        public const uint MouseEventLeftUp = 0x0004;
        public const uint MouseEventRightDown = 0x0008;
        public const uint MouseEventRightUp = 0x0010;
        public const uint MouseEventMiddleDown = 0x0020;
        public const uint MouseEventMiddleUp = 0x0040;
        public const uint MouseEventXDown = 0x0080;
        public const uint MouseEventXUp = 0x0100;

        public delegate IntPtr LowLevelKeyboardProcedure(
            int code,
            IntPtr wParam,
            IntPtr lParam);

        public delegate IntPtr LowLevelMouseProcedure(
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelKeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LowLevelMouseData
        {
            public NativePoint Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeInput
        {
            public uint Type;
            public NativeInputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct NativeInputUnion
        {
            [FieldOffset(0)]
            public NativeMouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMouseInput
        {
            public int DeltaX;
            public int DeltaY;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(
            IntPtr windowHandle,
            int identifier,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr windowHandle, int identifier);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int hookIdentifier,
            LowLevelKeyboardProcedure procedure,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(
            int hookIdentifier,
            LowLevelMouseProcedure procedure,
            IntPtr moduleHandle,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(
            IntPtr hookHandle,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] NativeInput[] inputs,
            int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
