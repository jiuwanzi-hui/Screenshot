using System.Runtime.InteropServices;
using System.Windows;
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
    private const int WindowMessageInput = 0x00FF;
    private const int MessageOnlyWindow = -3;
    private const int LowLevelKeyboardHook = 13;
    private const int LowLevelMouseHook = 14;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageKeyUp = 0x0101;
    private const int WindowMessageSystemKeyDown = 0x0104;
    private const int WindowMessageSystemKeyUp = 0x0105;
    private const int WindowMessageMouseMove = 0x0200;
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int HitTestMinimizeButton = 8;
    private const int HitTestMaximizeButton = 9;
    private const int HitTestCloseButton = 20;
    private const int SystemMetricCaptionButtonWidth = 30;
    private const int SystemMetricCaptionButtonHeight = 31;
    private const uint AncestorRoot = 2;
    private const uint SendMessageTimeoutAbortIfHung = 0x0002;
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
    private const uint RawInputTypeMouse = 0;
    private const uint RawInputTypeKeyboard = 1;
    private const uint RawInputCommandInput = 0x10000003;
    private const uint RawInputDeviceRemove = 0x00000001;
    private const uint RawInputDeviceInputSink = 0x00000100;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageGenericMouse = 0x02;
    private const ushort HidUsageGenericKeyboard = 0x06;
    private const ushort RawMouseLeftDown = 0x0001;
    private const ushort RawMouseLeftUp = 0x0002;
    private const ushort RawMouseRightDown = 0x0004;
    private const ushort RawMouseRightUp = 0x0008;
    private const ushort RawMouseMiddleDown = 0x0010;
    private const ushort RawMouseMiddleUp = 0x0020;
    private const ushort RawMouseX1Down = 0x0040;
    private const ushort RawMouseX1Up = 0x0080;
    private const ushort RawMouseX2Down = 0x0100;
    private const ushort RawMouseX2Up = 0x0200;
    private const ushort RawKeyboardBreak = 0x0001;
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
    private static readonly TimeSpan MouseSourceDeduplicationWindow =
        TimeSpan.FromMilliseconds(120);
    private static readonly object InputDiagnosticsLock = new();
    private static readonly string InputDiagnosticsPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "SnapCut-MouseInput.log");

    private readonly HwndSource _messageSource;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly NativeMethods.LowLevelMouseProcedure _mouseProcedure;
    private readonly DispatcherTimer _preCaptureExpiryTimer;
    private readonly DispatcherTimer _mouseHoldTimer;
    private readonly Dictionary<int, HotKeyBinding> _registeredBindings = [];
    private readonly HashSet<HotKeyAction> _preCapturedActions = [];
    private readonly HashSet<uint> _capturedModifierKeysDown = [];
    private readonly HashSet<uint> _rawModifierKeysDown = [];
    private readonly HashSet<uint> _ignoredRawModifierKeysUntilUp = [];
    private readonly Dictionary<uint, PendingMouseHold> _pendingMouseHolds = [];
    private readonly Dictionary<uint, PendingModifierProbe> _modifierProbeHolds = [];
    private readonly List<RecentMouseEvent> _recentMouseEvents = [];
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
    private int _preCaptureGeneration;
    private int _preCaptureCaptureInFlight;
    private TimeSpan _mouseLongPressDuration = TimeSpan.FromMilliseconds(700);
    private bool _mouseSideButtonsUseLongPress;
    private bool _areMouseShortcutsSuspended;
    private bool _isKeyboardCaptureActive;
    private bool _rawKeyboardRegistered;
    private bool _rawMouseRegistered;
    private Window? _modifierProbeWindow;
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
        _rawKeyboardRegistered = RegisterRawKeyboardInput();
        _rawMouseRegistered = RegisterRawMouseInput();
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

        WriteInputDiagnostic(
            $"manager-created keyboardHook=0x{_keyboardHook.ToInt64():X} " +
            $"mouseHook=0x{_mouseHook.ToInt64():X} " +
            $"mouseHookError={_mouseHookErrorCode} " +
            $"rawKeyboard={_rawKeyboardRegistered} " +
            $"rawMouse={_rawMouseRegistered}");
    }

    public event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    public event EventHandler<HotKeyCaptureInputEventArgs>? HotKeyCaptureInputReceived;

    public IReadOnlyList<HotKeyBinding> RegisteredBindings =>
        _registeredBindings.Values.OrderBy(binding => binding.Action).ToArray();

    internal bool IsKeyboardCaptureActive => _isKeyboardCaptureActive;

    internal Action<uint>? ReplayMouseSideButtonOverride { get; set; }

    internal Action<uint, bool>? ReplayPrimaryMouseButtonOverride { get; set; }

    internal Action<uint>? ReplayPrimaryMouseButtonUpOverride { get; set; }

    internal Func<bool>? ActivateModifierProbeOverride { get; set; }

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
        _modifierProbeHolds.Clear();
        _recentMouseEvents.Clear();
        HideModifierProbe();
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
        _modifierProbeHolds.Clear();
        _recentMouseEvents.Clear();
        HideModifierProbe();
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
        UnregisterRawKeyboardInput();
        UnregisterRawMouseInput();
        _rawModifierKeysDown.Clear();
        _ignoredRawModifierKeysUntilUp.Clear();
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
        _modifierProbeHolds.Clear();
        HideModifierProbe();
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
        if (message == WindowMessageInput)
        {
            ProcessRawInput(lParam);
        }

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

            if ((isKeyDown || isKeyUp) && IsModifierKey(keyboardData.VirtualKey))
            {
                // Raw Input is the preferred source for modifier state, but
                // some applications (notably virtual-machine clients) can
                // delay or omit the first WM_INPUT keyboard packet. Keep the
                // low-level hook on the same state path so a mouse event that
                // follows that key event is not classified as unmodified.
                UpdateRawModifierState(
                    keyboardData.VirtualKey,
                    isKeyDown,
                    "WH_KEYBOARD_LL");
                WriteInputDiagnostic(
                    $"source=WH_KEYBOARD_LL key=0x{keyboardData.VirtualKey:X2} " +
                    $"event={(isKeyDown ? "down" : "up")} flags=0x{keyboardData.Flags:X} " +
                    $"extra=0x{keyboardData.ExtraInfo.ToInt64():X} " +
                    $"modifiers={GetCurrentModifiersIncluding(keyboardData.VirtualKey)}");
            }

            if ((isKeyDown || isKeyUp) &&
                ProcessKeyboardInputForCapture(
                    keyboardData.VirtualKey,
                    isKeyDown))
            {
                return new IntPtr(1);
            }

            // Do not start a second full-desktop GDI capture from the low-level
            // keyboard hook. The interactive capture path owns the single
            // snapshot; starting another one here can stall desktop input when
            // the first hotkey wakes the app after an idle period.
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
                if (_pendingMouseHolds.Count > 0)
                {
                    WriteInputDiagnostic(
                        $"source=WH_MOUSE_LL event=move x={mouseData.Point.X} y={mouseData.Point.Y} " +
                        $"flags=0x{mouseData.Flags:X} extra=0x{mouseData.ExtraInfo.ToInt64():X} " +
                        $"pending={string.Join(',', _pendingMouseHolds.Keys.Select(FormatMouseKey))}");
                }
                CancelMovedMouseHolds(mouseData.Point);
            }
            else if (TryGetMouseButton(
                message,
                mouseData.MouseData,
                out var virtualKey,
                out var isButtonDown))
            {
                if (TryConsumeRecentMouseEvent(
                        virtualKey,
                        isButtonDown,
                        mouseData.Point,
                        source: "WH_MOUSE_LL",
                        out var duplicateHandled))
                {
                    return duplicateHandled
                        ? new IntPtr(1)
                        : NativeMethods.CallNextHookEx(
                            _mouseHook,
                            code,
                            wParam,
                            lParam);
                }

                var handled = ProcessMouseButtonInputWithDiagnostics(
                    virtualKey,
                    isButtonDown,
                    mouseData.Point,
                    mouseData.Flags,
                    mouseData.ExtraInfo,
                    source: "WH_MOUSE_LL");
                RememberMouseEvent(
                    virtualKey,
                    isButtonDown,
                    mouseData.Point,
                    handled,
                    source: "WH_MOUSE_LL");
                if (handled)
                {
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool ProcessMouseButtonInputWithDiagnostics(
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        uint flags,
        IntPtr extraInfo,
        string source = "WH_MOUSE_LL")
    {
        var modifiers = GetCurrentModifiersIncluding(virtualKey);
        var binding = isButtonDown
            ? FindMouseBinding(_registeredBindings.Values, virtualKey, modifiers, requiresHold: false) ??
              FindMouseBinding(_registeredBindings.Values, virtualKey, modifiers, requiresHold: true)
            : null;
        WriteInputDiagnostic(
            $"source={source} key={FormatMouseKey(virtualKey)} " +
            $"event={(isButtonDown ? "down" : "up")} x={point.X} y={point.Y} " +
            $"flags=0x{flags:X} injected={(flags & 1) != 0} lowerIl={(flags & 2) != 0} " +
            $"extra=0x{extraInfo.ToInt64():X} modifiers={modifiers} " +
            $"raw={GetRawInputModifiers()} physical={GetPhysicalModifierState()} " +
            $"target={GetTargetWindowDescription(point)} " +
            $"binding={(binding is null ? "none" : $"{binding.Action}/hold={binding.Gesture.RequiresHold}")}");
        var handled = ProcessMouseButtonInput(
            virtualKey,
            isButtonDown,
            point,
            allowForegroundModifierProbe: true);
        WriteInputDiagnostic(
            $"source={source} mouse-result key={FormatMouseKey(virtualKey)} event={(isButtonDown ? "down" : "up")} " +
            $"handled={handled} pending={string.Join(',', _pendingMouseHolds.Keys.Select(FormatMouseKey))} " +
            $"suppressed={string.Join(',', _suppressedMouseButtonsUntilUp.Select(FormatMouseKey))}");
        return handled;
    }

    private bool TryConsumeRecentMouseEvent(
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        string source,
        out bool handled)
    {
        var now = DateTimeOffset.UtcNow;
        _recentMouseEvents.RemoveAll(
            recent => now - recent.ObservedAt > MouseSourceDeduplicationWindow);

        var duplicate = _recentMouseEvents.FirstOrDefault(
            recent =>
                recent.VirtualKey == virtualKey &&
                recent.IsButtonDown == isButtonDown &&
                Math.Abs(recent.Point.X - point.X) <= 3 &&
                Math.Abs(recent.Point.Y - point.Y) <= 3);
        if (duplicate is not null)
        {
            _recentMouseEvents.Remove(duplicate);
            handled = duplicate.Handled;
            WriteInputDiagnostic(
                $"source={source} mouse-duplicate key={FormatMouseKey(virtualKey)} " +
                $"event={(isButtonDown ? "down" : "up")} " +
                $"of={duplicate.Source}");
            return true;
        }

        handled = false;
        return false;
    }

    private void RememberMouseEvent(
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        bool handled,
        string source)
    {
        var now = DateTimeOffset.UtcNow;
        _recentMouseEvents.RemoveAll(
            recent => now - recent.ObservedAt > MouseSourceDeduplicationWindow);
        _recentMouseEvents.Add(
            new RecentMouseEvent(
                virtualKey,
                isButtonDown,
                point,
                now,
                handled,
                source));
    }

    private bool ProcessMouseButtonInput(
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        HotKeyModifiers? modifiersOverride = null,
        bool allowForegroundModifierProbe = false)
    {
        if (!isButtonDown && _modifierProbeHolds.Remove(virtualKey))
        {
            WriteInputDiagnostic(
                $"modifier-probe-cancel key={FormatMouseKey(virtualKey)} " +
                "reason=early-release");
            HideModifierProbeIfIdle();
            if (virtualKey == HotKeyGesture.VirtualKeyMouseLeft)
            {
                _primaryButtonsPassedThroughForHold.Remove(virtualKey);
                StopMouseHoldTimerWhenIdle();
                return false;
            }
        }

        CapturePointerContinuation? releasedContinuation = null;
        if (!isButtonDown &&
            _capturePointerContinuations.Remove(
                virtualKey,
                out releasedContinuation))
        {
            WriteInputDiagnostic(
                $"continuation-release-queued id={releasedContinuation.DiagnosticId} " +
                $"key={FormatMouseKey(virtualKey)}");
            _ = _messageSource.Dispatcher.BeginInvoke(
                () =>
                {
                    WriteInputDiagnostic(
                        $"continuation-release-delivered id={releasedContinuation.DiagnosticId}");
                    releasedContinuation.NotifyReleased();
                });
        }

        if (!isButtonDown && _suppressedMouseButtonsUntilUp.Remove(virtualKey))
        {
            var passThroughToCaptureOverlay = releasedContinuation is not null;
            WriteInputDiagnostic(
                $"hold-release key={FormatMouseKey(virtualKey)} " +
                $"reason=button-up-before-or-after-trigger " +
                $"passThroughToOverlay={passThroughToCaptureOverlay}");
            _pendingMouseHolds.Remove(virtualKey);
            _modifierProbeHolds.Remove(virtualKey);
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

            // A capture overlay now owns the continued physical gesture. Let
            // WPF observe the real button-up so its MouseDevice does not remain
            // pressed and consume the first toolbar or right-button action.
            return !passThroughToCaptureOverlay;
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
                WriteInputDiagnostic(
                    $"hold-cancel key={FormatMouseKey(virtualKey)} reason=shortcuts-suspended-release");
                _pendingMouseHolds.Remove(virtualKey);
                _modifierProbeHolds.Remove(virtualKey);
                HideModifierProbeIfIdle();
                StopMouseHoldTimerWhenIdle();
            }

            return false;
        }

        if (!isButtonDown)
        {
            if (_pendingMouseHolds.Remove(virtualKey))
            {
                WriteInputDiagnostic(
                    $"hold-cancel key={FormatMouseKey(virtualKey)} reason=early-release");
            }
            _pendingMouseHolds.Remove(virtualKey);
            _modifierProbeHolds.Remove(virtualKey);
            HideModifierProbeIfIdle();
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
            var suppressNativeCaptionButton =
                IsCaptionButton(point) &&
                IsPrimaryMouseButton(virtualKey);
            if (suppressNativeCaptionButton)
            {
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
                WriteInputDiagnostic(
                    $"caption-button-suppress key={FormatMouseKey(virtualKey)} " +
                    $"hit={GetCaptionHitTest(point)} action={immediateBinding.Action}");
            }

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
                point,
                allowForegroundModifierProbe);
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
            WriteInputDiagnostic(
                $"mouse-unhandled key={FormatMouseKey(virtualKey)} event=down " +
                $"reason=no-binding modifiers={modifiers}");
            return false;
        }

        _pendingMouseHolds[virtualKey] = new PendingMouseHold(
            holdBinding,
            DateTimeOffset.UtcNow,
            point,
            allowForegroundModifierProbe);

        if (virtualKey == HotKeyGesture.VirtualKeyMouseLeft &&
            !IsCaptionButton(point))
        {
            // Preserve the physical left-button sequence so ordinary clicks,
            // double-clicks, text selection, and window resizing remain native.
            _primaryButtonsPassedThroughForHold.Add(virtualKey);
            WriteInputDiagnostic(
                $"hold-pending key=left action={holdBinding.Action} modifiers={modifiers} " +
                $"pass-through=true start={point.X},{point.Y}");
            _mouseHoldTimer.Start();
            return false;
        }

        _suppressedMouseButtonsUntilUp.Add(virtualKey);
        _primaryButtonsToReplayUntilUp.Add(virtualKey);
        WriteInputDiagnostic(
            $"hold-pending key={FormatMouseKey(virtualKey)} action={holdBinding.Action} " +
            $"modifiers={modifiers} pass-through=false start={point.X},{point.Y}");
        _mouseHoldTimer.Start();
        return true;
    }

    private static bool IsPrimaryMouseButton(uint virtualKey)
    {
        return virtualKey is HotKeyGesture.VirtualKeyMouseLeft or
            HotKeyGesture.VirtualKeyMouseRight;
    }

    private static bool IsCaptionButton(NativeMethods.NativePoint point)
    {
        if (GetCaptionHitTest(point) is
            HitTestMinimizeButton or
            HitTestMaximizeButton or
            HitTestCloseButton)
        {
            return true;
        }

        // Some WPF/DWM title bars report HTCLIENT for the minimize glyph.
        // Use the system caption-button band as a window-scoped fallback so
        // this remains independent of a particular window's visual tree.
        return IsCaptionButtonByBounds(point);
    }

    private static int GetCaptionHitTest(NativeMethods.NativePoint point)
    {
        try
        {
            var windowHandle = NativeMethods.WindowFromPoint(point);
            if (windowHandle == IntPtr.Zero)
            {
                return 0;
            }

            var rootWindowHandle = NativeMethods.GetAncestor(
                windowHandle,
                AncestorRoot);
            if (rootWindowHandle != IntPtr.Zero)
            {
                windowHandle = rootWindowHandle;
            }

            var lParam = new IntPtr(
                (point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16));
            _ = NativeMethods.SendMessageTimeout(
                windowHandle,
                WindowMessageNonClientHitTest,
                IntPtr.Zero,
                lParam,
                SendMessageTimeoutAbortIfHung,
                20,
                out var result);
            return unchecked((int)result.ToInt64());
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsCaptionButtonByBounds(NativeMethods.NativePoint point)
    {
        try
        {
            var windowHandle = NativeMethods.WindowFromPoint(point);
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            var rootWindowHandle = NativeMethods.GetAncestor(
                windowHandle,
                AncestorRoot);
            if (rootWindowHandle != IntPtr.Zero)
            {
                windowHandle = rootWindowHandle;
            }

            if (!NativeMethods.GetWindowRect(windowHandle, out var windowRect))
            {
                return false;
            }

            var buttonWidth = NativeMethods.GetSystemMetrics(
                SystemMetricCaptionButtonWidth);
            var buttonHeight = NativeMethods.GetSystemMetrics(
                SystemMetricCaptionButtonHeight);
            if (buttonWidth <= 0)
            {
                buttonWidth = 30;
            }

            if (buttonHeight <= 0)
            {
                buttonHeight = 30;
            }

            return point.Y >= windowRect.Top &&
                point.Y < windowRect.Top + buttonHeight &&
                point.X >= windowRect.Right - (buttonWidth * 3) &&
                point.X < windowRect.Right;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsCaptionButtonHitTestForTest(int hitTest)
    {
        return hitTest is HitTestMinimizeButton or
            HitTestMaximizeButton or
            HitTestCloseButton;
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

    internal bool ProcessMouseButtonInputFromSourceForTest(
        uint virtualKey,
        bool isButtonDown,
        string source,
        int x = 0,
        int y = 0,
        HotKeyModifiers? modifiers = null)
    {
        var point = new NativeMethods.NativePoint { X = x, Y = y };
        if (TryConsumeRecentMouseEvent(
                virtualKey,
                isButtonDown,
                point,
                source,
                out _))
        {
            return false;
        }

        var handled = ProcessMouseButtonInput(
            virtualKey,
            isButtonDown,
            point,
            modifiers,
            allowForegroundModifierProbe: true);
        RememberMouseEvent(
            virtualKey,
            isButtonDown,
            point,
            handled,
            source);
        return handled;
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

    internal void ProcessRawModifierInputForTest(uint virtualKey, bool isKeyDown) =>
        UpdateRawModifierState(virtualKey, isKeyDown, "test");

    internal void ProcessLowLevelModifierInputForTest(
        uint virtualKey,
        bool isKeyDown) =>
        UpdateRawModifierState(virtualKey, isKeyDown, "test-low-level");

    internal void IgnoreRawModifierUntilReleaseForTest(uint virtualKey) =>
        IgnoreRawModifierUntilRelease(virtualKey, "test");

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
                WriteInputDiagnostic(
                    $"hold-cancel key={FormatMouseKey(virtualKey)} reason=real-move " +
                    $"start={pending.StartPoint.X},{pending.StartPoint.Y} " +
                    $"current={point.X},{point.Y}");
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

        foreach (var (virtualKey, probe) in _modifierProbeHolds.ToArray())
        {
            if (point.X != probe.Pending.StartPoint.X ||
                point.Y != probe.Pending.StartPoint.Y)
            {
                WriteInputDiagnostic(
                    $"hold-cancel key={FormatMouseKey(virtualKey)} reason=real-move " +
                    $"phase=modifier-probe start={probe.Pending.StartPoint.X}," +
                    $"{probe.Pending.StartPoint.Y} current={point.X},{point.Y}");
                _modifierProbeHolds.Remove(virtualKey);
                _primaryButtonsPassedThroughForHold.Remove(virtualKey);
                if (_primaryButtonsToReplayUntilUp.Remove(virtualKey))
                {
                    _suppressedMouseButtonsUntilUp.Remove(virtualKey);
                    ReplayPrimaryMouseButton(virtualKey, includeButtonUp: false);
                }
            }
        }

        HideModifierProbeIfIdle();

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
        ProcessModifierProbes(now);

        var ready = _pendingMouseHolds
            .Where(pair => now - pair.Value.PressedAt >= _mouseLongPressDuration)
            .ToArray();
        foreach (var (virtualKey, pending) in ready)
        {
            if (pending.Binding.Gesture.Modifiers == HotKeyModifiers.None &&
                HasModifiedMouseHoldBinding(virtualKey) &&
                GetCurrentModifiersIncluding(virtualKey) == HotKeyModifiers.None)
            {
                _pendingMouseHolds.Remove(virtualKey);
                _modifierProbeHolds[virtualKey] = new PendingModifierProbe(
                    pending,
                    now + TimeSpan.FromMilliseconds(250));
                ShowModifierProbe(pending.AllowForegroundModifierProbe);
                WriteInputDiagnostic(
                    $"modifier-probe-start key={FormatMouseKey(virtualKey)} " +
                    $"start={pending.StartPoint.X},{pending.StartPoint.Y} " +
                    $"readyAt={now + TimeSpan.FromMilliseconds(250):O}");
                continue;
            }

            TriggerMouseHold(virtualKey, pending, now);
        }

        StopMouseHoldTimerWhenIdle();
    }

    private void ProcessModifierProbes(DateTimeOffset now)
    {
        foreach (var (virtualKey, probe) in _modifierProbeHolds.ToArray())
        {
            var modifiers = GetCurrentModifiersIncluding(virtualKey);
            if (now < probe.ReadyAt && modifiers == HotKeyModifiers.None)
            {
                continue;
            }

            _modifierProbeHolds.Remove(virtualKey);
            HideModifierProbeIfIdle();
            var binding = FindMouseBinding(
                    _registeredBindings.Values,
                    virtualKey,
                    modifiers,
                    requiresHold: true) ??
                probe.Pending.Binding;
            IgnoreStaleProbeModifiers(modifiers);
            WriteInputDiagnostic(
                $"modifier-probe-complete key={FormatMouseKey(virtualKey)} " +
                $"modifiers={modifiers} selected={binding.Action} " +
                $"elapsed={(now - probe.Pending.PressedAt).TotalMilliseconds:0}ms");
            TriggerMouseHold(virtualKey, probe.Pending with { Binding = binding }, now);
        }
    }

    private void TriggerMouseHold(
        uint virtualKey,
        PendingMouseHold pending,
        DateTimeOffset now)
    {
        _pendingMouseHolds.Remove(virtualKey);
        WriteInputDiagnostic(
                $"hold-trigger key={FormatMouseKey(virtualKey)} action={pending.Binding.Action} " +
                $"modifiers={pending.Binding.Gesture.Modifiers} " +
                $"start={pending.StartPoint.X},{pending.StartPoint.Y} " +
                $"elapsed={(now - pending.PressedAt).TotalMilliseconds:0}ms");
            _sideButtonsToReplayUntilUp.Remove(virtualKey);
            _primaryButtonsToReplayUntilUp.Remove(virtualKey);
            if (_primaryButtonsPassedThroughForHold.Remove(virtualKey))
            {
                // The target received the native down. Finish that interaction
                // before the capture overlay takes ownership of the still-held
                // physical button, then suppress its eventual native up.
                ReplayPrimaryMouseButtonUp(virtualKey);
                WriteInputDiagnostic(
                    $"hold-trigger-replay-up key={FormatMouseKey(virtualKey)}");
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
            }
            var continuation = CreateCapturePointerContinuation(
                virtualKey,
                pending.Binding.Action,
                pending.StartPoint,
                enterPickerWhenReleasedWithoutSelection:
                    virtualKey == HotKeyGesture.VirtualKeyMouseLeft);
            RaiseHotKeyPressed(
                pending.Binding.Action,
                CaptureCurrentScreen(pending.Binding.Action),
                continuation);
            WriteInputDiagnostic(
                $"hold-trigger-raised key={FormatMouseKey(virtualKey)} " +
                $"continuation={(continuation is null ? "none" : continuation.Button.ToString())}");
    }

    private bool HasModifiedMouseHoldBinding(uint virtualKey)
    {
        return _registeredBindings.Values.Any(binding =>
            binding.Gesture.IsMouseButton &&
            binding.Gesture.VirtualKey == virtualKey &&
            binding.Gesture.RequiresHold &&
            binding.Gesture.Modifiers != HotKeyModifiers.None);
    }

    private void ShowModifierProbe(bool activateForeground)
    {
        if (!activateForeground)
        {
            WriteInputDiagnostic(
                "modifier-probe-passive reason=test-or-synthetic-path");
            return;
        }

        if (_modifierProbeWindow is not null)
        {
            return;
        }

        if (ActivateModifierProbeOverride is not null)
        {
            var overrideActivated = ActivateModifierProbeOverride();
            WriteInputDiagnostic(
                $"modifier-probe-foreground override=true activated={overrideActivated}");
            return;
        }

        var probeWindow = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32_000,
            Top = -32_000,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0.01,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = true,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStyle = WindowStyle.None,
        };
        _modifierProbeWindow = probeWindow;
        probeWindow.Show();
        var activated = probeWindow.Activate();
        WriteInputDiagnostic(
            $"modifier-probe-foreground activated={activated} " +
            $"active={probeWindow.IsActive}");
    }

    private void HideModifierProbeIfIdle()
    {
        if (_modifierProbeHolds.Count == 0)
        {
            HideModifierProbe();
        }
    }

    private void HideModifierProbe()
    {
        var probeWindow = _modifierProbeWindow;
        _modifierProbeWindow = null;
        if (probeWindow is null)
        {
            return;
        }

        probeWindow.Close();
    }

    private void StopMouseHoldTimerWhenIdle()
    {
        if (_pendingMouseHolds.Count == 0 &&
            _modifierProbeHolds.Count == 0)
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
        NativeMethods.NativePoint startPoint,
        bool enterPickerWhenReleasedWithoutSelection = false)
    {
        var captureButton = GetCapturePointerButton(virtualKey);
        if (!captureButton.HasValue || !OpensCaptureSelection(action))
        {
            return null;
        }

        var continuation = new CapturePointerContinuation(
            captureButton.Value,
            new System.Drawing.Point(startPoint.X, startPoint.Y),
            enterPickerWhenReleasedWithoutSelection);
        WriteInputDiagnostic(
            $"continuation-created id={continuation.DiagnosticId} " +
            $"button={captureButton.Value} action={action} " +
            $"start={startPoint.X},{startPoint.Y}");
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
        WriteInputDiagnostic($"replay-primary-up key={FormatMouseKey(virtualKey)}");
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

        if (Interlocked.Exchange(ref _preCaptureCaptureInFlight, 1) != 0)
        {
            return;
        }

        var generation = Volatile.Read(ref _preCaptureGeneration);
        var candidateActions = candidates.ToArray();
        _ = Task.Run(() =>
        {
            CapturedImage? snapshot = null;
            try
            {
                snapshot = ScreenCaptureService.Capture(VirtualScreen.GetBounds());
            }
            catch
            {
                // The normal capture path will provide a fallback if this
                // best-effort pre-capture cannot be completed.
            }

            try
            {
                _messageSource.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() =>
                    {
                        Interlocked.Exchange(ref _preCaptureCaptureInFlight, 0);
                        if (snapshot is null)
                        {
                            return;
                        }

                        if (generation != Volatile.Read(ref _preCaptureGeneration))
                        {
                            snapshot.Dispose();
                            return;
                        }

                        ClearPreCapturedScreen();
                        _preCapturedScreen = snapshot;
                        _preCapturedAt = DateTimeOffset.UtcNow;
                        _preCaptureExpiryTimer.Stop();
                        _preCaptureExpiryTimer.Start();
                        foreach (var action in candidateActions)
                        {
                            _preCapturedActions.Add(action);
                        }
                    }));
            }
            catch
            {
                Interlocked.Exchange(ref _preCaptureCaptureInFlight, 0);
                snapshot?.Dispose();
            }
        });
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
        WriteInputDiagnostic(
            $"hotkey-raised action={action} " +
            $"continuation={(capturePointerContinuation is null ? "none" : capturePointerContinuation.Button.ToString())}");
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
        Interlocked.Increment(ref _preCaptureGeneration);
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

    private HotKeyModifiers GetCurrentModifiersIncluding(uint virtualKey)
    {
        var modifiers = HotKeyModifiers.None;
        if (IsKeyDown(VirtualKeyControl) ||
            (!_ignoredRawModifierKeysUntilUp.Any(IsControlKey) &&
                _rawModifierKeysDown.Any(IsControlKey)) ||
            virtualKey is VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl)
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKeyAlt) ||
            (!_ignoredRawModifierKeysUntilUp.Any(IsAltKey) &&
                _rawModifierKeysDown.Any(IsAltKey)) ||
            virtualKey is VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt)
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKeyShift) ||
            (!_ignoredRawModifierKeysUntilUp.Any(IsShiftKey) &&
                _rawModifierKeysDown.Any(IsShiftKey)) ||
            virtualKey is VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift)
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKeyLeftWindows) ||
            IsKeyDown(VirtualKeyRightWindows) ||
            (!_ignoredRawModifierKeysUntilUp.Any(IsWindowsKey) &&
                _rawModifierKeysDown.Any(IsWindowsKey)) ||
            virtualKey is VirtualKeyLeftWindows or VirtualKeyRightWindows)
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return modifiers;
    }

    private HotKeyModifiers GetRawInputModifiers()
    {
        var modifiers = HotKeyModifiers.None;
        if (_rawModifierKeysDown.Any(IsControlKey))
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (_rawModifierKeysDown.Any(IsAltKey))
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (_rawModifierKeysDown.Any(IsShiftKey))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (_rawModifierKeysDown.Any(IsWindowsKey))
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return modifiers;
    }

    private void IgnoreStaleProbeModifiers(HotKeyModifiers modifiers)
    {
        if (modifiers.HasFlag(HotKeyModifiers.Control) &&
            !IsAnyControlKeyDown())
        {
            IgnoreRawModifierUntilRelease(VirtualKeyControl, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyLeftControl, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyRightControl, "probe-stale");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Alt) &&
            !IsAnyAltKeyDown())
        {
            IgnoreRawModifierUntilRelease(VirtualKeyAlt, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyLeftAlt, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyRightAlt, "probe-stale");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Shift) &&
            !IsAnyShiftKeyDown())
        {
            IgnoreRawModifierUntilRelease(VirtualKeyShift, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyLeftShift, "probe-stale");
            IgnoreRawModifierUntilRelease(VirtualKeyRightShift, "probe-stale");
        }
    }

    private void IgnoreRawModifierUntilRelease(uint virtualKey, string source)
    {
        if (!IsModifierKey(virtualKey))
        {
            return;
        }

        virtualKey = NormalizeModifierVirtualKey(virtualKey);
        _ignoredRawModifierKeysUntilUp.Add(virtualKey);
        _rawModifierKeysDown.Remove(virtualKey);
        WriteInputDiagnostic(
            $"source={source} raw-modifier-ignore-until-up key=0x{virtualKey:X2}");
    }

    private static bool IsAnyControlKeyDown() =>
        IsKeyDown(VirtualKeyControl) ||
        IsKeyDown(VirtualKeyLeftControl) ||
        IsKeyDown(VirtualKeyRightControl);

    private static bool IsAnyAltKeyDown() =>
        IsKeyDown(VirtualKeyAlt) ||
        IsKeyDown(VirtualKeyLeftAlt) ||
        IsKeyDown(VirtualKeyRightAlt);

    private static bool IsAnyShiftKeyDown() =>
        IsKeyDown(VirtualKeyShift) ||
        IsKeyDown(VirtualKeyLeftShift) ||
        IsKeyDown(VirtualKeyRightShift);

    private bool RegisterRawKeyboardInput()
    {
        var device = new NativeMethods.RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageGenericKeyboard,
            Flags = RawInputDeviceInputSink,
            TargetWindow = _messageSource.Handle,
        };
        return NativeMethods.RegisterRawInputDevices(
            [device],
            deviceCount: 1,
            Marshal.SizeOf<NativeMethods.RawInputDevice>());
    }

    private bool RegisterRawMouseInput()
    {
        var device = new NativeMethods.RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageGenericMouse,
            Flags = RawInputDeviceInputSink,
            TargetWindow = _messageSource.Handle,
        };
        return NativeMethods.RegisterRawInputDevices(
            [device],
            deviceCount: 1,
            Marshal.SizeOf<NativeMethods.RawInputDevice>());
    }

    private void UnregisterRawKeyboardInput()
    {
        if (!_rawKeyboardRegistered)
        {
            return;
        }

        var device = new NativeMethods.RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageGenericKeyboard,
            Flags = RawInputDeviceRemove,
            TargetWindow = IntPtr.Zero,
        };
        _ = NativeMethods.RegisterRawInputDevices(
            [device],
            deviceCount: 1,
            Marshal.SizeOf<NativeMethods.RawInputDevice>());
        _rawKeyboardRegistered = false;
    }

    private void UnregisterRawMouseInput()
    {
        if (!_rawMouseRegistered)
        {
            return;
        }

        var device = new NativeMethods.RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageGenericMouse,
            Flags = RawInputDeviceRemove,
            TargetWindow = IntPtr.Zero,
        };
        _ = NativeMethods.RegisterRawInputDevices(
            [device],
            deviceCount: 1,
            Marshal.SizeOf<NativeMethods.RawInputDevice>());
        _rawMouseRegistered = false;
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        uint inputSize = 0;
        if (NativeMethods.GetRawInputData(
                rawInputHandle,
                RawInputCommandInput,
                IntPtr.Zero,
                ref inputSize,
                headerSize) == uint.MaxValue ||
            inputSize < headerSize)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)inputSize));
        try
        {
            var copiedSize = inputSize;
            if (NativeMethods.GetRawInputData(
                    rawInputHandle,
                    RawInputCommandInput,
                    buffer,
                    ref copiedSize,
                    headerSize) != copiedSize)
            {
                return;
            }

            var header = Marshal.PtrToStructure<NativeMethods.RawInputHeader>(buffer);
            if (header.Type == RawInputTypeMouse)
            {
                ProcessRawMouseInput(
                    IntPtr.Add(buffer, checked((int)headerSize)));
                return;
            }

            if (header.Type != RawInputTypeKeyboard)
            {
                return;
            }

            var keyboard = Marshal.PtrToStructure<NativeMethods.RawKeyboard>(
                IntPtr.Add(buffer, checked((int)headerSize)));
            UpdateRawModifierState(
                keyboard.VirtualKey,
                (keyboard.Flags & RawKeyboardBreak) == 0,
                "WM_INPUT");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessRawMouseInput(IntPtr rawMouseData)
    {
        var mouse = Marshal.PtrToStructure<NativeMethods.RawMouse>(rawMouseData);
        var extraInfo = new IntPtr(unchecked((long)mouse.ExtraInformation));
        if (ShouldBypassMouseShortcutProcessing(extraInfo, replayDepth: 0))
        {
            WriteInputDiagnostic(
                $"source=WM_INPUT-MOUSE bypass-replay extra=0x{mouse.ExtraInformation:X}");
            return;
        }

        if (mouse.ButtonFlags != 0)
        {
            if (!NativeMethods.GetCursorPos(out var point))
            {
                point = default;
            }

            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseLeftDown,
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: true,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseLeftUp,
                HotKeyGesture.VirtualKeyMouseLeft,
                isButtonDown: false,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseRightDown,
                HotKeyGesture.VirtualKeyMouseRight,
                isButtonDown: true,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseRightUp,
                HotKeyGesture.VirtualKeyMouseRight,
                isButtonDown: false,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseMiddleDown,
                HotKeyGesture.VirtualKeyMouseMiddle,
                isButtonDown: true,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseMiddleUp,
                HotKeyGesture.VirtualKeyMouseMiddle,
                isButtonDown: false,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseX1Down,
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: true,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseX1Up,
                HotKeyGesture.VirtualKeyMouseBack,
                isButtonDown: false,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseX2Down,
                HotKeyGesture.VirtualKeyMouseForward,
                isButtonDown: true,
                point,
                mouse.ExtraInformation);
            ProcessRawMouseButton(
                mouse.ButtonFlags,
                RawMouseX2Up,
                HotKeyGesture.VirtualKeyMouseForward,
                isButtonDown: false,
                point,
                mouse.ExtraInformation);
        }

        if (mouse.LastX != 0 || mouse.LastY != 0)
        {
            if (NativeMethods.GetCursorPos(out var point))
            {
                CancelMovedMouseHolds(point);
            }
        }
    }

    private void ProcessRawMouseButton(
        ushort buttonFlags,
        ushort expectedFlag,
        uint virtualKey,
        bool isButtonDown,
        NativeMethods.NativePoint point,
        uint extraInformation)
    {
        if ((buttonFlags & expectedFlag) == 0)
        {
            return;
        }

        if (TryConsumeRecentMouseEvent(
                virtualKey,
                isButtonDown,
                point,
                source: "WM_INPUT-MOUSE",
                out _))
        {
            return;
        }

        var handled = ProcessMouseButtonInputWithDiagnostics(
            virtualKey,
            isButtonDown,
            point,
            0,
            new IntPtr(unchecked((long)extraInformation)),
            source: "WM_INPUT-MOUSE");
        RememberMouseEvent(
            virtualKey,
            isButtonDown,
            point,
            handled,
            source: "WM_INPUT-MOUSE");
    }

    private void UpdateRawModifierState(
        uint virtualKey,
        bool isKeyDown,
        string source)
    {
        if (!IsModifierKey(virtualKey))
        {
            return;
        }

        virtualKey = NormalizeModifierVirtualKey(virtualKey);
        if (isKeyDown && _ignoredRawModifierKeysUntilUp.Contains(virtualKey))
        {
            WriteInputDiagnostic(
                $"source={source} raw-modifier-ignored key=0x{virtualKey:X2} " +
                "reason=probe-late-down");
            return;
        }

        if (!isKeyDown)
        {
            _ignoredRawModifierKeysUntilUp.Remove(virtualKey);
        }

        var changed = isKeyDown
            ? _rawModifierKeysDown.Add(virtualKey)
            : _rawModifierKeysDown.Remove(virtualKey);
        if (changed)
        {
            WriteInputDiagnostic(
                $"source={source} raw-modifier key=0x{virtualKey:X2} " +
                $"event={(isKeyDown ? "down" : "up")} " +
                $"modifiers={GetRawInputModifiers()}");
        }
    }

    private static string FormatMouseKey(uint virtualKey) =>
        virtualKey switch
        {
            HotKeyGesture.VirtualKeyMouseLeft => "left",
            HotKeyGesture.VirtualKeyMouseRight => "right",
            HotKeyGesture.VirtualKeyMouseMiddle => "middle",
            HotKeyGesture.VirtualKeyMouseBack => "back",
            HotKeyGesture.VirtualKeyMouseForward => "forward",
            _ => $"0x{virtualKey:X2}",
        };

    private static string GetPhysicalModifierState() =>
        $"ctrl={(IsKeyDown(VirtualKeyControl) ? 1 : 0)}" +
        $"(L={(IsKeyDown(VirtualKeyLeftControl) ? 1 : 0)},R={(IsKeyDown(VirtualKeyRightControl) ? 1 : 0)}) " +
        $"alt={(IsKeyDown(VirtualKeyAlt) ? 1 : 0)} " +
        $"shift={(IsKeyDown(VirtualKeyShift) ? 1 : 0)}";

    private static string GetTargetWindowDescription(NativeMethods.NativePoint point)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(point);
            if (hwnd == IntPtr.Zero)
            {
                return "none";
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            var className = new System.Text.StringBuilder(128);
            _ = NativeMethods.GetClassName(hwnd, className, className.Capacity);
            return $"hwnd=0x{hwnd.ToInt64():X};pid={processId};class={className}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static void WriteInputDiagnostic(string message)
    {
        try
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("SNAPCUT_MOUSE_INPUT_LOG"),
                    "0",
                    StringComparison.Ordinal))
            {
                return;
            }

            lock (InputDiagnosticsLock)
            {
                System.IO.File.AppendAllText(
                    InputDiagnosticsPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect global input handling.
        }
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

    private static uint NormalizeModifierVirtualKey(uint virtualKey)
    {
        if (IsControlKey(virtualKey))
        {
            return VirtualKeyControl;
        }

        if (IsAltKey(virtualKey))
        {
            return VirtualKeyAlt;
        }

        if (IsShiftKey(virtualKey))
        {
            return VirtualKeyShift;
        }

        return virtualKey;
    }

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
        NativeMethods.NativePoint StartPoint,
        bool AllowForegroundModifierProbe);

    private sealed record PendingModifierProbe(
        PendingMouseHold Pending,
        DateTimeOffset ReadyAt);

    private sealed record RecentMouseEvent(
        uint VirtualKey,
        bool IsButtonDown,
        NativeMethods.NativePoint Point,
        DateTimeOffset ObservedAt,
        bool Handled,
        string Source);

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
        public struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr TargetWindow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RawInputHeader
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr Parameter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RawKeyboard
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VirtualKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct RawMouse
        {
            [FieldOffset(0)]
            public ushort Flags;

            [FieldOffset(4)]
            public ushort ButtonFlags;

            [FieldOffset(6)]
            public ushort ButtonData;

            [FieldOffset(8)]
            public uint RawButtons;

            [FieldOffset(12)]
            public int LastX;

            [FieldOffset(16)]
            public int LastY;

            [FieldOffset(20)]
            public uint ExtraInformation;
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices(
            [In] RawInputDevice[] devices,
            uint deviceCount,
            int size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(
            IntPtr rawInputHandle,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(
            IntPtr windowHandle,
            uint flags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeRect rectangle);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(
            IntPtr windowHandle,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #pragma warning disable CA1838
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(
            IntPtr windowHandle,
            System.Text.StringBuilder className,
            int maxCount);
        #pragma warning restore CA1838

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(
            uint inputCount,
            [In] NativeInput[] inputs,
            int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
