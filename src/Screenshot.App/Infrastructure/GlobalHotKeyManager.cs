using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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
    // Ordinary keyboard captures are only a latency optimization. Keep their
    // frame short-lived so a changed desktop is never represented by an old
    // cached image. Context-menu snapshots use their separate four-second
    // window below and are intentionally unaffected.
    private static readonly TimeSpan PostCaptureCompositionSettleWindow =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ContextMenuCaptureWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ContextMenuCaptureDelay =
        // One compositor frame is enough for a popup that was already
        // detected. A long fixed delay makes the menu visibly disappear
        // before the overlay opens on slower desktop capture paths.
        TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ContextMenuCapturePollInterval =
        TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ContextMenuCaptureWait =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ContextMenuMouseHoldWait =
        TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan MouseSourceDeduplicationWindow =
        TimeSpan.FromMilliseconds(120);
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int WindowStyleIndex = -16;
    private const long WindowStylePopup = 0x80000000L;

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
    private readonly Dictionary<uint, PendingImmediateMouseCapture> _pendingImmediateMouseCaptures = [];
    private readonly Dictionary<uint, PendingModifierProbe> _modifierProbeHolds = [];
    private readonly List<RecentMouseEvent> _recentMouseEvents = [];
    private readonly HashSet<uint> _suppressedMouseButtonsUntilUp = [];
    private readonly HashSet<uint> _suppressedTransientMenuModifiers = [];
    private readonly HashSet<uint> _suppressedTransientMenuKeys = [];
    private readonly Dictionary<HotKeyAction, long> _earlyKeyboardHotKeys = [];
    private readonly HashSet<uint> _sideButtonsToReplayUntilUp = [];
    private readonly HashSet<uint> _primaryButtonsToReplayUntilUp = [];
    private readonly HashSet<uint> _primaryButtonsPassedThroughForHold = [];
    private readonly Dictionary<uint, CapturePointerContinuation>
        _capturePointerContinuations = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly int _mouseHookErrorCode;
    private CapturedImage? _preCapturedScreen;
    private readonly object _immediatePreCaptureLock = new();
    private CapturedImage? _immediatePreCapturedScreen;
    private readonly HashSet<HotKeyAction> _immediatePreCapturedActions = [];
    private DateTimeOffset _immediatePreCapturedAt;
    private DateTimeOffset _preCapturedAt;
    private int _preCaptureGeneration;
    private long _lastRightButtonUpTimestamp;
    private int _lastRightButtonUpLikelyExplorer;
    private long _contextMenuCaptureGeneration;
    private long _lastCaptureClosedTimestamp;
    private int _captureOverlayActive;
    private int _captureOverlayRightButtonDown;
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
        CaptureTimingDiagnostics.Mark(
            "hotkey-manager-created",
            $"pid={Environment.ProcessId} base={AppContext.BaseDirectory}");
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
        _suppressedTransientMenuModifiers.Clear();
        _suppressedTransientMenuKeys.Clear();
        _earlyKeyboardHotKeys.Clear();
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
        _pendingImmediateMouseCaptures.Clear();
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
        if (binding.Action == HotKeyAction.CompleteCapture)
        {
            // Completion is scoped to the capture overlay. Keep the binding in
            // the configuration set for conflict validation and lifecycle
            // bookkeeping, but let the overlay consume the key locally so it
            // never steals the same key from other applications.
            _registeredBindings[identifier] = binding;
            registrationError = null;
            return true;
        }

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
            CaptureTimingDiagnostics.Mark(
                "hotkey-register-failed",
                $"action={binding.Action} gesture={binding.Gesture} error={errorCode}");
            registrationError = CreateRegistrationError(binding, errorCode);
            return false;
        }

        _registeredBindings[identifier] = binding;
        CaptureTimingDiagnostics.Mark(
            "hotkey-registered",
            $"action={binding.Action} gesture={binding.Gesture}");
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
        _suppressedMouseButtonsUntilUp.Clear();
        _suppressedTransientMenuModifiers.Clear();
        _suppressedTransientMenuKeys.Clear();
        _earlyKeyboardHotKeys.Clear();
        foreach (var continuation in _capturePointerContinuations.Values)
        {
            continuation.NotifyReleased();
        }
        _capturePointerContinuations.Clear();
        _pendingImmediateMouseCaptures.Clear();
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

        if (message == WindowMessageHotKey)
        {
            CaptureTimingDiagnostics.Mark(
                "wm-hotkey-received",
                $"id={wParam.ToInt32()}");
        }

        if (message == WindowMessageHotKey &&
            _registeredBindings.TryGetValue(wParam.ToInt32(), out var binding))
        {
            CaptureTimingDiagnostics.Mark(
                "wm-hotkey",
                $"action={binding.Action}");
            // A transient shell menu can consume Alt (and, depending on the
            // host, Ctrl/Shift) before RegisterHotKey posts WM_HOTKEY. In
            // that case the low-level hook has already started this action;
            // discard the later duplicate notification.
            if (_earlyKeyboardHotKeys.TryGetValue(
                    binding.Action,
                    out var earlyTimestamp))
            {
                _earlyKeyboardHotKeys.Remove(binding.Action);
                var elapsed = Stopwatch.GetTimestamp() - earlyTimestamp;
                if (elapsed >= 0 &&
                    elapsed <= Stopwatch.Frequency)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            var preCaptured = TakePreCapturedScreen(
                binding.Action,
                // The shell can dismiss a context menu while delivering the
                // hotkey.  For screenshot actions, keep using the frame that
                // was captured while the menu was still visible instead of
                // falling back to the now-clean desktop.
                allowImmediateContextMenuSnapshot:
                    IsTransientUiCaptureAction(binding.Action));
            RaiseHotKeyPressed(
                binding.Action,
                preCaptured);

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

            if (isKeyDown)
            {
                CaptureTimingDiagnostics.Mark(
                    "low-level-keydown",
                    $"vk=0x{keyboardData.VirtualKey:X2}");
                // Give an already-open shell/context menu a chance to reuse
                // the frame captured around the right-button release. This
                // method never captures an ordinary desktop frame from the
                // low-level keyboard hook.
                // Ordinary Ctrl/Alt shortcuts must stay on the lightest hook
                // path. Only a recent right-button gesture needs the menu
                // preservation check; it is the one case where a frame may
                // already be waiting for Explorer's popup.
                // Ordinary shortcut modifiers must stay completely passive.
                // A background full-desktop pre-capture here still competes
                // with DWM and causes the one-frame hitch reported when the
                // user presses Shift/Ctrl/Alt. Only a recent context-menu
                // gesture is allowed into the transient-menu path.
                if (HasRecentContextMenuGesture())
                {
                    TryPreCaptureTransientUi(keyboardData.VirtualKey);
                }
                // Ordinary keyboard shortcuts stay completely passive here.
                // Their screenshot is captured after the hotkey message has
                // returned; starting GDI work on the modifier edge causes the
                // exact one-frame hitch this hook is meant to avoid.

                if (TryHandleTransientMenuKeyboardInput(
                        keyboardData.VirtualKey,
                        GetCurrentModifiersIncluding(keyboardData.VirtualKey)))
                {
                    return new IntPtr(1);
                }

                if (!_isKeyboardCaptureActive &&
                    CaptureOverlayWindow.TryHandleGlobalCompletionKey(
                        keyboardData.VirtualKey,
                        GetCurrentModifiersIncluding(keyboardData.VirtualKey)))
                {
                    return new IntPtr(1);
                }
            }

            if (!isKeyDown &&
                TryReleaseTransientMenuKeyboardInput(
                    keyboardData.VirtualKey))
            {
                return new IntPtr(1);
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
            var message = wParam.ToInt32();
            // Keep the low-level hook a true pass-through for ordinary mouse
            // movement.  Avoid marshaling the hook payload unless a pending
            // hold needs coordinates; this is the hot path for high-report-
            // rate devices and does not affect button or wheel shortcuts.
            if (message == WindowMessageMouseMove &&
                _pendingMouseHolds.Count == 0 &&
                _modifierProbeHolds.Count == 0)
            {
                return NativeMethods.CallNextHookEx(
                    _mouseHook,
                    code,
                    wParam,
                    lParam);
            }

            var mouseData = Marshal.PtrToStructure<NativeMethods.LowLevelMouseData>(
                lParam);

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
                // Most mouse moves have no pending hold to cancel. Avoid
                // allocating snapshots of empty dictionaries on every raw
                // mouse packet, which is significant for high-report-rate
                // devices and while manual long-screenshot mode is active.
                if (_pendingMouseHolds.Count > 0 ||
                    _modifierProbeHolds.Count > 0)
                {
                    CancelMovedMouseHolds(mouseData.Point);
                }
            }
            else if (TryGetMouseButton(
                message,
                mouseData.MouseData,
                out var virtualKey,
                out var isButtonDown))
            {
                if (virtualKey == HotKeyGesture.VirtualKeyMouseRight &&
                    !isButtonDown &&
                    !ShouldSuppressContextMenuGesture(isButtonDown))
                {
                    MarkRecentContextMenuGesture(mouseData.Point);
                }

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
        if (virtualKey == HotKeyGesture.VirtualKeyMouseRight &&
            IsShellTaskbarOrContextMenuPoint(point))
        {
            var shellModifiers = GetCurrentModifiersIncluding(virtualKey);
            var hasMouseHoldBinding =
                FindMouseBinding(
                    _registeredBindings.Values,
                    virtualKey,
                    shellModifiers,
                    requiresHold: true) is not null;
            var hasActiveMouseGesture =
                _pendingMouseHolds.ContainsKey(virtualKey) ||
                _modifierProbeHolds.ContainsKey(virtualKey) ||
                _capturePointerContinuations.ContainsKey(virtualKey) ||
                _suppressedMouseButtonsUntilUp.Contains(virtualKey);
            if (hasMouseHoldBinding || hasActiveMouseGesture)
            {
                // Keep the native button sequence, but let the configured
                // long-press binding observe it and take over after the hold
                // threshold. Without this, tray and shell popups bypass the
                // mouse shortcut before it can ever become pending.
                return ProcessMouseButtonInput(
                    virtualKey,
                    isButtonDown,
                    point,
                    allowForegroundModifierProbe: true);
            }

            WriteInputDiagnostic(
                $"source={source} shell-ui-bypass key={FormatMouseKey(virtualKey)} " +
                $"event={(isButtonDown ? "down" : "up")} x={point.X} y={point.Y} " +
                $"target={GetTargetWindowDescription(point)}");
            return false;
        }

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
            if (_pendingImmediateMouseCaptures.Remove(
                    virtualKey,
                    out var pendingImmediateCapture) &&
                virtualKey == HotKeyGesture.VirtualKeyMouseRight)
            {
                MarkRecentContextMenuGesture(pendingImmediateCapture.StartPoint);
                QueueDeferredTransientMenuMouseCapture(
                    pendingImmediateCapture.Binding.Action,
                    pendingImmediateCapture.Continuation);
                // The physical button-up is intentionally passed through to
                // Explorer. It is what creates the context menu that the
                // deferred capture worker preserves.
                return false;
            }

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
                ClearImmediatePreCapturedScreen();
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
        var startedOnTransientMenu = IsTransientMenuPoint(point);
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
            if (virtualKey == HotKeyGesture.VirtualKeyMouseRight &&
                IsTransientUiCaptureAction(immediateBinding.Action) &&
                !startedOnTransientMenu)
            {
                _suppressedMouseButtonsUntilUp.Add(virtualKey);
                _pendingImmediateMouseCaptures[virtualKey] =
                    new PendingImmediateMouseCapture(
                        immediateBinding,
                        point,
                        CreateCapturePointerContinuation(
                            virtualKey,
                            immediateBinding.Action,
                            point));
                // Keep the down event native as well; only the matching
                // screenshot action is deferred until button-up.
                return false;
            }

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
                    var allowMenuSnapshot =
                        startedOnTransientMenu ||
                        HasRecentContextMenuGesture();
                    var snapshot = TakePreCapturedScreen(
                        immediateBinding.Action,
                        allowImmediateContextMenuSnapshot: allowMenuSnapshot);
                    if (snapshot is null &&
                        allowMenuSnapshot &&
                        IsTransientUiCaptureAction(immediateBinding.Action))
                    {
                        QueueDeferredTransientMenuMouseCapture(
                            immediateBinding.Action,
                            continuation);
                        return;
                    }

                    RaiseHotKeyPressed(
                        immediateBinding.Action,
                        snapshot,
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
                allowForegroundModifierProbe,
                startedOnTransientMenu &&
                    IsTransientUiCaptureAction(immediateBinding.Action));
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
            allowForegroundModifierProbe,
            startedOnTransientMenu &&
                IsTransientUiCaptureAction(holdBinding.Action));

        if (IsTransientUiCaptureAction(holdBinding.Action))
        {
            // A new mouse gesture must never inherit the previous gesture's
            // delayed menu frame. Clear it before optionally freezing the
            // menu that is visible at this button-down point.
            ClearPreCapturedScreen();
        }

        if (startedOnTransientMenu &&
            IsTransientUiCaptureAction(holdBinding.Action))
        {
            // Do not capture synchronously inside WH_MOUSE_LL. A full desktop
            // GDI capture can block the hook long enough for shell/XAML menus
            // to repaint or dismiss at button-down. The physical button is
            // already suppressed below, so the popup remains in place while
            // this best-effort frame is captured off the hook thread.
            QueueTransientMenuHoldSnapshot(holdBinding.Action);
            WriteInputDiagnostic(
                $"mouse-menu-pre-capture action={holdBinding.Action} " +
                $"point={point.X},{point.Y}");
        }

        if (holdBinding.Action == HotKeyAction.PinImage &&
            IsPrimaryMouseButton(virtualKey))
        {
            // Pinning with a primary button must own the button-down event.
            // Otherwise the target receives the click before the long-press
            // threshold and the pin frame is taken after that side effect.
            QueuePinPressSnapshot(startedOnTransientMenu, holdBinding.Action);
        }

        if ((virtualKey is HotKeyGesture.VirtualKeyMouseLeft or
                HotKeyGesture.VirtualKeyMouseRight) &&
            (modifiers == HotKeyModifiers.None ||
             !OpensCaptureSelection(holdBinding.Action)) &&
            !IsCaptionButton(point) &&
            !startedOnTransientMenu &&
            holdBinding.Action != HotKeyAction.PinImage)
        {
            // Preserve the existing physical button sequence for unmodified
            // gestures and non-selection actions. Modified capture gestures
            // must own the button from WM_*BUTTONDOWN; otherwise the
            // foreground window starts receiving the same drag while the
            // long-press gesture is waiting to trigger, which makes the later
            // native selection feel delayed and can leave the source window
            // in a competing drag state.
            _primaryButtonsPassedThroughForHold.Add(virtualKey);
            WriteInputDiagnostic(
                $"hold-pending key={FormatMouseKey(virtualKey)} action={holdBinding.Action} modifiers={modifiers} " +
                $"pass-through=true start={point.X},{point.Y}");
            _mouseHoldTimer.Start();
            return false;
        }

        _suppressedMouseButtonsUntilUp.Add(virtualKey);
        // Keep a short primary-button click indistinguishable from a normal
        // click. If the hold threshold is reached, TriggerMouseHold removes
        // this marker and the click is suppressed instead.
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
        if (virtualKey == HotKeyGesture.VirtualKeyMouseRight)
        {
            _ = ShouldSuppressContextMenuGesture(isButtonDown);
        }

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
        CapturedImage? preCapturedScreen = null;
        var deferTransientMenuCapture = false;

        _sideButtonsToReplayUntilUp.Remove(virtualKey);
        _primaryButtonsToReplayUntilUp.Remove(virtualKey);
        var passedThroughForHold =
            _primaryButtonsPassedThroughForHold.Remove(virtualKey);
        if (passedThroughForHold)
        {
            _suppressedMouseButtonsUntilUp.Add(virtualKey);
            if (pending.StartedOnTransientMenu)
            {
                preCapturedScreen = TakePreCapturedScreen(
                    pending.Binding.Action,
                    allowImmediateContextMenuSnapshot: true);
                WriteInputDiagnostic(
                    $"hold-trigger-menu-preserved key={FormatMouseKey(virtualKey)}");
            }

            ReplayPrimaryMouseButtonUp(virtualKey);
            WriteInputDiagnostic(
                $"hold-trigger-replay-up key={FormatMouseKey(virtualKey)}");
            if (!pending.StartedOnTransientMenu)
            {
                // For a native right-button hold, the context menu is created
                // by the button-up. Release it first, then wait until the
                // shell popup is actually visible before taking the frame.
                if (virtualKey == HotKeyGesture.VirtualKeyMouseRight &&
                    IsTransientUiCaptureAction(pending.Binding.Action))
                {
                    MarkRecentContextMenuGesture(pending.StartPoint);
                    // The popup is created by the replayed button-up. Waiting
                    // for it here blocks the WPF dispatcher/timer and makes
                    // the next interaction visibly lag. The worker started
                    // by MarkRecentContextMenuGesture will publish the frame;
                    // defer raising the hotkey until that frame is ready.
                    deferTransientMenuCapture = true;
                }
            }
        }

        if (preCapturedScreen is null &&
            virtualKey == HotKeyGesture.VirtualKeyMouseRight)
        {
            preCapturedScreen = TakePreCapturedScreen(
                pending.Binding.Action,
                allowImmediateContextMenuSnapshot:
                    pending.StartedOnTransientMenu ||
                    IsTransientMenuWindowVisible() ||
                    HasRecentContextMenuGesture());
            if (preCapturedScreen is null &&
                IsTransientUiCaptureAction(pending.Binding.Action) &&
                IsTransientMenuWindowVisible())
            {
                preCapturedScreen = CaptureTransientUiScreen();
            }
        }
            var continuation = CreateCapturePointerContinuation(
                virtualKey,
                pending.Binding.Action,
                pending.StartPoint,
                enterPickerWhenReleasedWithoutSelection:
                    virtualKey == HotKeyGesture.VirtualKeyMouseLeft);
            preCapturedScreen ??= TakePreCapturedScreen(
                pending.Binding.Action,
                allowImmediateContextMenuSnapshot:
                    pending.StartedOnTransientMenu ||
                    HasRecentContextMenuGesture());
            if (preCapturedScreen is not null)
            {
                WriteInputDiagnostic(
                    $"mouse-capture-source=pre-captured action={pending.Binding.Action}");
            }
            else
            {
                if (deferTransientMenuCapture)
                {
                    QueueDeferredTransientMenuMouseCapture(
                        pending.Binding.Action,
                        continuation);
                    return;
                }

                if (IsTransientUiCaptureAction(pending.Binding.Action))
                {
                    preCapturedScreen = CaptureCurrentScreen(
                        pending.Binding.Action);
                }
                WriteInputDiagnostic(
                    $"mouse-capture-source=fallback action={pending.Binding.Action}");
            }
            // A delayed pre-capture worker can finish after this mouse gesture
            // has already selected its frame. Invalidate and dispose every
            // remaining buffer so the next fast gesture cannot consume this
            // gesture's desktop image.
            ClearPreCapturedScreen();
            RaiseHotKeyPressed(
                pending.Binding.Action,
                preCapturedScreen,
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

    internal static bool IsShellTaskbarOrContextMenuClassForTest(
        string? className)
    {
        return IsShellTaskbarOrContextMenuClass(className);
    }

    private static bool IsShellTaskbarOrContextMenuPoint(
        NativeMethods.NativePoint point)
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
            return IsShellTaskbarOrContextMenuWindow(windowHandle) ||
                (rootWindowHandle != IntPtr.Zero &&
                 IsShellTaskbarOrContextMenuWindow(rootWindowHandle));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientMenuPoint(
        NativeMethods.NativePoint point)
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
            return IsTransientMenuWindowHandle(windowHandle) ||
                IsTransientPopupAtPoint(windowHandle, point) ||
                (rootWindowHandle != IntPtr.Zero &&
                 (IsTransientMenuWindowHandle(rootWindowHandle) ||
                  IsTransientPopupAtPoint(rootWindowHandle, point)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientMenuWindowHandle(
        IntPtr windowHandle)
    {
        var className = new System.Text.StringBuilder(128);
        _ = NativeMethods.GetClassName(
            windowHandle,
            className,
            className.Capacity);
        var classText = className.ToString();
        return IsTransientMenuClass(classText) ||
            IsExplorerPopupChild(windowHandle, classText) ||
            IsExplorerPopupWindow(windowHandle) ||
            (classText.StartsWith(
                "WindowsForms10.Window.",
                StringComparison.OrdinalIgnoreCase) &&
              (NativeMethods.GetWindowLongPtr(
                     windowHandle,
                     WindowStyleIndex).ToInt64() & WindowStylePopup) != 0);
    }

    private static bool IsExplorerPopupChild(
        IntPtr windowHandle,
        string className)
    {
        // Explorer's Windows 11 context menu may expose a DirectUI child
        // instead of the usual #32768/XAML popup class. Restrict this fallback
        // to popup-styled DirectUI windows owned by explorer.exe; the regular
        // file-list DirectUI child is not a popup and will not be captured.
        if (!className.Equals("DirectUIHWND", StringComparison.OrdinalIgnoreCase) &&
            !className.Equals(
                "Windows.UI.Input.InputSite.WindowClass",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((NativeMethods.GetWindowLongPtr(
                windowHandle,
                WindowStyleIndex).ToInt64() & WindowStylePopup) == 0)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(
                "explorer",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExplorerPopupWindow(IntPtr windowHandle)
    {
        // Explorer's modern context menu has changed its child class names
        // across Windows 11 builds. The stable part of the contract is that
        // it is a visible WS_POPUP owned by explorer.exe. Keep this fallback
        // process- and style-scoped so normal Explorer document windows are
        // never treated as transient menus.
        if ((NativeMethods.GetWindowLongPtr(
                windowHandle,
                WindowStyleIndex).ToInt64() & WindowStylePopup) == 0)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(
                "explorer",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientPopupAtPoint(
        IntPtr windowHandle,
        NativeMethods.NativePoint point)
    {
        if (windowHandle == IntPtr.Zero ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            (NativeMethods.GetWindowLongPtr(
                windowHandle,
                WindowStyleIndex).ToInt64() & WindowStylePopup) == 0 ||
            !NativeMethods.GetWindowRect(windowHandle, out var bounds))
        {
            return false;
        }

        return point.X >= bounds.Left && point.X < bounds.Right &&
            point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }

    private static bool IsShellTaskbarOrContextMenuWindow(
        IntPtr windowHandle)
    {
        var className = new System.Text.StringBuilder(128);
        _ = NativeMethods.GetClassName(
            windowHandle,
            className,
            className.Capacity);
        return IsShellTaskbarOrContextMenuClass(className.ToString());
    }

    private static bool IsShellTaskbarOrContextMenuClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        var normalized = className.Trim();
        return normalized is
            "#32768" or
            "Shell_TrayWnd" or
            "TrayNotifyWnd" or
            "TrayClockWClass" or
            "NotifyIconOverflowWindow" or
            "TopLevelWindowForOverflowXamlIsland" or
            "Xaml_WindowedPopupClass" or
            "CabinetWClass" or
            "ExploreWClass" ||
            normalized.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Overflow", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Taskbar", StringComparison.OrdinalIgnoreCase);
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
        // Modifier keys can auto-repeat while the capture overlay is already
        // active. Starting another full-desktop GDI capture from that path
        // competes with the native border/mask paint loop and is especially
        // visible while Ctrl is held during a drag. Pre-capture is only useful
        // before a capture session starts.
        if (_disposed ||
            Volatile.Read(ref _captureOverlayActive) != 0 ||
            _areMouseShortcutsSuspended)
        {
            return;
        }

        var modifiers = GetCurrentModifiersIncluding(virtualKey);
        var candidates = GetPreCaptureActions(
            _registeredBindings.Values,
            virtualKey,
            modifiers);
        // Do not walk every desktop window for each modifier callback. Ctrl/
        // Alt can repeat while held and this path runs inside WH_KEYBOARD_LL;
        // the recent right-click marker is enough until the actual character
        // key arrives. A real window walk is reserved for non-modifier keys.
        var recentMenuGesture = HasRecentContextMenuGesture();
        // This method runs inside WH_KEYBOARD_LL. Never enumerate desktop
        // windows here: EnumWindows/EnumChildWindows can block the input
        // chain while Ctrl/Alt is held. The right-button worker records the
        // transient-menu gesture asynchronously, and that marker is enough
        // for the hook to defer the capture safely.
        var transientMenuVisible = recentMenuGesture;
        if (candidates.Count == 0 && transientMenuVisible)
        {
            // Shell tray and XAML popups can deliver the modifier and the
            // character key through different input paths. In that case the
            // exact modifier set may be incomplete on this callback even
            // though the menu is visibly open. Prepare one shared frame for
            // every registered transient capture action so the matching
            // binding can still consume it below.
            candidates = _registeredBindings.Values
                .Where(binding => IsTransientUiCaptureAction(binding.Action))
                .Select(binding => binding.Action)
                .Distinct()
                .ToArray();
        }
        if (candidates.Count == 0)
        {
            return;
        }

        if (transientMenuVisible)
        {
            lock (_immediatePreCaptureLock)
            {
                if (_immediatePreCapturedScreen is not null &&
                    DateTimeOffset.UtcNow - _immediatePreCapturedAt <=
                        ContextMenuCaptureWindow &&
                    candidates.All(_immediatePreCapturedActions.Contains))
                {
                    // MarkRecentContextMenuGesture normally prepares this
                    // frame asynchronously after the right-button release.
                    // Reuse it instead of taking a second full-screen frame
                    // from the keyboard hook.
                    return;
                }
            }

            // Never capture synchronously from WH_KEYBOARD_LL. A full virtual
            // desktop GDI/PrintWindow pass can block the system input hook for
            // hundreds of milliseconds, especially while Ctrl/Alt is held.
            // The mouse-release worker owns transient-menu capture; if it has
            // not produced a frame yet, let the normal hotkey path continue
            // without stealing the key or stalling the foreground app.
            return;
        }

        // No ordinary desktop pre-capture is performed here. The only caller
        // that reaches this method is the context-menu path above; keeping
        // this guard makes the invariant explicit if the hook logic changes.
        return;
    }

    internal static IReadOnlyList<HotKeyAction> GetPreCaptureActions(
        IEnumerable<HotKeyBinding> bindings,
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        var isModifier = IsModifierKey(virtualKey);
        var isAltKey = virtualKey is
            VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt;
        var modifierKey = virtualKey switch
        {
            VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl =>
                HotKeyModifiers.Control,
            VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt =>
                HotKeyModifiers.Alt,
            VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift =>
                HotKeyModifiers.Shift,
            VirtualKeyLeftWindows or VirtualKeyRightWindows =>
                HotKeyModifiers.Windows,
            _ => HotKeyModifiers.None,
        };
        return bindings
            .Where(binding => IsTransientUiCaptureAction(binding.Action))
            .Where(binding => isModifier
                ? modifierKey != HotKeyModifiers.None &&
                    binding.Gesture.Modifiers.HasFlag(modifierKey)
                : isAltKey
                    ? binding.Gesture.Modifiers.HasFlag(HotKeyModifiers.Alt)
                    : binding.Gesture.Modifiers == modifiers)
            .Where(binding => isModifier || binding.Gesture.VirtualKey == virtualKey)
            .Select(binding => binding.Action)
            .Distinct()
            .ToArray();
    }

    private CapturedImage? TakePreCapturedScreen(
        HotKeyAction action,
        bool allowImmediateContextMenuSnapshot = true)
    {
        var hasRecentContextMenuGesture = HasRecentContextMenuGesture();

        // A pre-capture worker may still be completing while the hotkey is
        // delivered. Invalidate that worker before detaching the current
        // frame so it cannot publish the previous desktop image after this
        // gesture has already started.
        Interlocked.Increment(ref _preCaptureGeneration);

        lock (_immediatePreCaptureLock)
        {
            if (allowImmediateContextMenuSnapshot &&
                _immediatePreCapturedScreen is not null &&
                DateTimeOffset.UtcNow - _immediatePreCapturedAt <=
                    ContextMenuCaptureWindow &&
                _immediatePreCapturedActions.Contains(action))
            {
                // A delayed context-menu capture may still be running on the
                // worker thread. Invalidate it before handing the frame to the
                // overlay, so a post-dismissal desktop frame cannot become
                // the next capture's stale snapshot.
                Interlocked.Increment(ref _contextMenuCaptureGeneration);
                var immediate = _immediatePreCapturedScreen;
                _immediatePreCapturedScreen = null;
                _immediatePreCapturedActions.Clear();
                _immediatePreCapturedAt = default;
                ClearStandardPreCapturedScreen();
                return immediate;
            }

            if (_immediatePreCapturedScreen is not null)
            {
                Interlocked.Increment(ref _contextMenuCaptureGeneration);
                _immediatePreCapturedScreen.Dispose();
                _immediatePreCapturedScreen = null;
                _immediatePreCapturedActions.Clear();
                _immediatePreCapturedAt = default;
                ClearStandardPreCapturedScreen();
            }
        }

        if (!hasRecentContextMenuGesture && ShouldDiscardStandardPreCapture(
                Volatile.Read(ref _lastCaptureClosedTimestamp),
                Stopwatch.GetTimestamp()))
        {
            // The previous layered overlay may still be present in DWM's
            // desktop image for a short time after WPF reports it closed.
            // A rapid next hotkey can pre-capture that retired overlay and
            // make the next selection show the previous screenshot. Keep
            // immediate menu snapshots above, but discard an ordinary frame
            // here so the coordinator waits for composition and captures a
            // fresh desktop image.
            ClearStandardPreCapturedScreen();
            return null;
        }

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

    internal void NotifyCaptureClosed()
    {
        Volatile.Write(ref _captureOverlayActive, 0);
        Volatile.Write(
            ref _lastCaptureClosedTimestamp,
            Stopwatch.GetTimestamp());
        Volatile.Write(ref _lastRightButtonUpTimestamp, 0);
        Volatile.Write(ref _lastRightButtonUpLikelyExplorer, 0);
        Interlocked.Increment(ref _contextMenuCaptureGeneration);
        Interlocked.Increment(ref _preCaptureGeneration);
        ClearStandardPreCapturedScreen();
        ClearImmediatePreCapturedScreen();
    }

    internal void SetCaptureOverlayActive(bool active)
    {
        Volatile.Write(ref _captureOverlayActive, active ? 1 : 0);
        if (active)
        {
            return;
        }

        NotifyCaptureClosed();
    }

    private bool ShouldSuppressContextMenuGesture(bool isButtonDown)
    {
        if (isButtonDown)
        {
            if (Volatile.Read(ref _captureOverlayActive) != 0)
            {
                Volatile.Write(ref _captureOverlayRightButtonDown, 1);
            }
            else
            {
                // A new physical right-button gesture cannot be the release
                // of an older gesture that started in the overlay.
                Volatile.Write(ref _captureOverlayRightButtonDown, 0);
            }

            return false;
        }

        var startedInCapture = Interlocked.Exchange(
            ref _captureOverlayRightButtonDown,
            0) != 0;
        return Volatile.Read(ref _captureOverlayActive) != 0 ||
            startedInCapture;
    }

    internal bool ShouldSuppressContextMenuGestureForTest(bool isButtonDown) =>
        ShouldSuppressContextMenuGesture(isButtonDown);

    internal bool HasRecentContextMenuGestureForTest() =>
        HasRecentContextMenuGesture();

    internal static bool ShouldDiscardStandardPreCapture(
        long captureClosedTimestamp,
        long currentTimestamp)
    {
        if (captureClosedTimestamp <= 0 ||
            currentTimestamp < captureClosedTimestamp)
        {
            return false;
        }

        var elapsedTicks = currentTimestamp - captureClosedTimestamp;
        return elapsedTicks <=
            PostCaptureCompositionSettleWindow.TotalSeconds *
            Stopwatch.Frequency;
    }

    private static CapturedImage? CaptureCurrentScreen(HotKeyAction action)
    {
        if (!IsTransientUiCaptureAction(action))
        {
            return null;
        }

        try
        {
            return ScreenCaptureService.CaptureIncludingLayeredWindows(
                VirtualScreen.GetBounds());
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
        using var timing = CaptureTimingDiagnostics.Begin(
            "hotkey-raise",
            $"action={action} hasSnapshot={preCapturedScreen is not null}");
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
        Interlocked.Increment(ref _contextMenuCaptureGeneration);
        Interlocked.Increment(ref _preCaptureGeneration);
        ClearStandardPreCapturedScreen();
        lock (_immediatePreCaptureLock)
        {
            _immediatePreCapturedScreen?.Dispose();
            _immediatePreCapturedScreen = null;
            _immediatePreCapturedActions.Clear();
            _immediatePreCapturedAt = default;
        }
        _preCapturedAt = default;
        _preCaptureExpiryTimer.Stop();
    }

    private void ClearStandardPreCapturedScreen()
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
        return action is HotKeyAction.RegionCapture or
            HotKeyAction.RecognizeText or
            HotKeyAction.PinImage;
    }

    private bool TryHandleTransientMenuKeyboardInput(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        if (_disposed ||
            _isKeyboardCaptureActive ||
            Volatile.Read(ref _captureOverlayActive) != 0)
        {
            return false;
        }

        if (IsModifierKey(virtualKey) &&
            _suppressedTransientMenuModifiers.Contains(virtualKey))
        {
            return true;
        }

        if (!IsModifierKey(virtualKey) &&
            _suppressedTransientMenuKeys.Contains(virtualKey))
        {
            return true;
        }

        if (IsModifierKey(virtualKey))
        {
            // Modifier keys must remain visible to the foreground application.
            // In particular, swallowing Alt here leaves the shell and normal
            // applications with a stuck/inoperable Alt key. The pre-capture
            // work is already performed by TryPreCaptureTransientUi before
            // this method runs, so the modifier itself never needs to be
            // suppressed. The matching character key below can still consume
            // the configured screenshot gesture and use that saved frame.
            return false;
        }

        var binding = _registeredBindings.Values.FirstOrDefault(
            candidate =>
                !candidate.Gesture.IsMouseButton &&
                IsTransientUiCaptureAction(candidate.Action) &&
                candidate.Gesture.VirtualKey == virtualKey &&
                candidate.Gesture.Modifiers == modifiers);
        if (binding is null)
        {
            return false;
        }

        var recentMenuGesture = HasRecentContextMenuGesture();
        var hasImmediateMenuSnapshot = false;
        lock (_immediatePreCaptureLock)
        {
            hasImmediateMenuSnapshot = _immediatePreCapturedScreen is not null &&
                _immediatePreCapturedActions.Contains(binding.Action) &&
                DateTimeOffset.UtcNow - _immediatePreCapturedAt <=
                    ContextMenuCaptureWindow;
        }

        // The hook must not synchronously walk the desktop. A recent gesture
        // or an already captured frame is the non-blocking evidence that this
        // key belongs to a transient menu. Background probing remains in the
        // mouse-release worker.
        var menuWindowVisible = recentMenuGesture || hasImmediateMenuSnapshot;

        // A shell popup can be removed between the modifier and character
        // callbacks. The pre-captured frame is the authoritative indication
        // that this combination started while a menu was visible; accepting
        // it here prevents the character from reaching the shell and closing
        // the menu before the overlay opens.
        if (_suppressedTransientMenuModifiers.Count == 0 &&
            !menuWindowVisible &&
            !recentMenuGesture &&
            !hasImmediateMenuSnapshot)
        {
            return false;
        }

        var snapshot = TakePreCapturedScreen(
                binding.Action,
                allowImmediateContextMenuSnapshot: true);
        if (snapshot is null)
        {
            // The right-button worker may still be waiting for Explorer's
            // popup to appear. Consume the character key now so the shell
            // cannot dismiss the menu, then finish the capture off the hook
            // thread. This avoids both the old menu-disappears behavior and
            // the input stall caused by synchronous GDI capture.
            if (!menuWindowVisible && !recentMenuGesture)
            {
                return false;
            }

            _earlyKeyboardHotKeys[binding.Action] = Stopwatch.GetTimestamp();
            _suppressedTransientMenuKeys.Add(virtualKey);
            QueueDeferredTransientMenuKeyboardCapture(binding.Action);
            return true;
        }
        _earlyKeyboardHotKeys[binding.Action] = Stopwatch.GetTimestamp();
        _suppressedTransientMenuKeys.Add(virtualKey);
        // Enter the UI dispatcher synchronously for this already-captured
        // transient-menu gesture. Returning from the low-level hook first
        // leaves a compositor gap in which the shell menu disappears and the
        // desktop briefly shows through before the overlay is created.
        // RaiseHotKeyPressed only queues the normal capture workflow; it does
        // not perform a desktop capture on the hook thread.
        try
        {
            var raise = new Action(() =>
            {
                if (_disposed)
                {
                    snapshot?.Dispose();
                    return;
                }

                RaiseHotKeyPressed(binding.Action, snapshot);
            });
            if (_messageSource.Dispatcher.CheckAccess())
            {
                raise();
            }
            else
            {
                _messageSource.Dispatcher.Invoke(
                    DispatcherPriority.Send,
                    raise);
            }
        }
        catch
        {
            // The hook must never propagate dispatcher shutdown failures into
            // the system input chain. Dispose the frame if the event could
            // not be handed off.
            snapshot?.Dispose();
        }
        return true;
    }

    private void QueueDeferredTransientMenuKeyboardCapture(HotKeyAction action)
    {
        _ = Task.Run(() =>
        {
            CapturedImage? snapshot = null;
            try
            {
                var deadline = Stopwatch.GetTimestamp() +
                    (long)(TimeSpan.FromMilliseconds(350).TotalSeconds *
                        Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    snapshot = TakeImmediatePreCapturedScreen(action);
                    if (snapshot is not null)
                    {
                        break;
                    }

                    Thread.Sleep(8);
                }

                // The key is suppressed while this runs, so the popup is
                // still present when the fallback capture is needed.
                snapshot ??= CaptureTransientUiScreen();
                if (snapshot is null)
                {
                    _messageSource.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => _earlyKeyboardHotKeys.Remove(action)));
                    return;
                }

                var captured = snapshot!;
                snapshot = null;
                _messageSource.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() =>
                    {
                        if (_disposed)
                        {
                            captured.Dispose();
                            return;
                        }

                        RaiseHotKeyPressed(action, captured);
                    }));
            }
            catch
            {
                snapshot?.Dispose();
            }
        });
    }

    private void QueueDeferredTransientMenuMouseCapture(
        HotKeyAction action,
        CapturePointerContinuation? continuation)
    {
        _ = Task.Run(() =>
        {
            CapturedImage? snapshot = null;
            try
            {
                var deadline = Stopwatch.GetTimestamp() +
                    (long)(TimeSpan.FromMilliseconds(300).TotalSeconds *
                        Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    snapshot = TakeImmediatePreCapturedScreen(action);
                    if (snapshot is not null)
                    {
                        break;
                    }

                    Thread.Sleep(8);
                }

                snapshot ??= CaptureTransientUiScreen();
                if (snapshot is null)
                {
                    return;
                }

                var captured = snapshot;
                snapshot = null;
                _messageSource.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() =>
                    {
                        if (_disposed)
                        {
                            captured.Dispose();
                            return;
                        }

                        RaiseHotKeyPressed(action, captured, continuation);
                    }));
            }
            catch
            {
                snapshot?.Dispose();
            }
        });
    }

    private CapturedImage? TakeImmediatePreCapturedScreen(HotKeyAction action)
    {
        lock (_immediatePreCaptureLock)
        {
            if (_immediatePreCapturedScreen is null ||
                !_immediatePreCapturedActions.Contains(action) ||
                DateTimeOffset.UtcNow - _immediatePreCapturedAt >
                    ContextMenuCaptureWindow)
            {
                return null;
            }

            Interlocked.Increment(ref _contextMenuCaptureGeneration);
            var snapshot = _immediatePreCapturedScreen;
            _immediatePreCapturedScreen = null;
            _immediatePreCapturedActions.Clear();
            _immediatePreCapturedAt = default;
            _preCapturedScreen?.Dispose();
            _preCapturedScreen = null;
            _preCapturedActions.Clear();
            _preCapturedAt = default;
            return snapshot;
        }
    }

    private bool TryReleaseTransientMenuKeyboardInput(uint virtualKey)
    {
        if (IsModifierKey(virtualKey))
        {
            return _suppressedTransientMenuModifiers.Remove(virtualKey);
        }

        return _suppressedTransientMenuKeys.Remove(virtualKey);
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

    private static CapturedImage? CaptureTransientUiScreen()
    {
        var virtualBounds = VirtualScreen.GetBounds();
        CapturedImage? desktop = null;
        try
        {
            // CaptureBlt preserves most layered shell menus in the desktop
            // frame. Some Explorer/XAML context menus are separate popup HWNDs
            // and are not composited by CopyFromScreen until a later frame;
            // explicitly print only those transient windows into the same
            // bitmap so a right-click menu cannot disappear from the capture.
            desktop = ScreenCaptureService.CaptureIncludingLayeredWindows(
                virtualBounds);
            CaptureTransientMenuWindowsInto(desktop.Bitmap, virtualBounds);
            _ = desktop.WarmPreview();
            return desktop;
        }
        catch
        {
            desktop?.Dispose();
            try
            {
                var fallback = ScreenCaptureService.Capture(virtualBounds);
                _ = fallback.WarmPreview();
                return fallback;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void CaptureTransientMenuWindowsInto(
        Bitmap desktop,
        ScreenRegion virtualBounds)
    {
        foreach (var windowHandle in EnumerateTransientMenuWindows())
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(windowHandle) ||
                    !NativeMethods.GetWindowRect(windowHandle, out var windowRect))
                {
                    continue;
                }

                var bounds = ScreenRegion.FromCorners(
                    windowRect.Left,
                    windowRect.Top,
                    windowRect.Right,
                    windowRect.Bottom);
                var clipped = ScreenRegion.Intersect(bounds, virtualBounds);
                if (bounds.IsEmpty || clipped.IsEmpty)
                {
                    continue;
                }

                using var menuBitmap = new Bitmap(
                    bounds.Width,
                    bounds.Height,
                    PixelFormat.Format32bppPArgb);
                var printed = false;
                using (var graphics = Graphics.FromImage(menuBitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        printed = NativeMethods.PrintWindow(
                                windowHandle,
                                hdc,
                                PrintWindowRenderFullContent);
                        if (!printed)
                        {
                            printed = NativeMethods.PrintWindow(
                                windowHandle,
                                hdc,
                                0);
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                WriteInputDiagnostic(
                    $"transient-menu-print hwnd=0x{windowHandle.ToInt64():X} " +
                    $"bounds={bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height} " +
                    $"success={printed}");
                if (!printed)
                {
                    continue;
                }

                using var desktopGraphics = Graphics.FromImage(desktop);
                desktopGraphics.DrawImageUnscaled(
                    menuBitmap,
                    bounds.X - virtualBounds.X,
                    bounds.Y - virtualBounds.Y);
            }
            catch
            {
                // A popup can close between enumeration and PrintWindow. One
                // stale HWND must never invalidate the already captured
                // desktop frame or force a clean-desktop fallback.
            }
        }
    }

    private static List<IntPtr> EnumerateTransientMenuWindows()
    {
        var windows = new List<IntPtr>();
        var hasCursorPoint = NativeMethods.GetCursorPos(out var cursorPoint);
        if (hasCursorPoint)
        {
            AddTransientMenuWindowTree(
                windows,
                NativeMethods.WindowFromPoint(cursorPoint),
                cursorPoint);
        }

        AddTransientMenuWindowTree(
            windows,
            NativeMethods.GetForegroundWindow(),
            hasCursorPoint ? cursorPoint : null);

        _ = NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (NativeMethods.IsWindowVisible(windowHandle))
            {
                AddTransientMenuWindowTree(
                    windows,
                    windowHandle,
                    hasCursorPoint ? cursorPoint : null);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static void AddTransientMenuWindowTree(
        List<IntPtr> windows,
        IntPtr windowHandle,
        NativeMethods.NativePoint? cursorPoint = null)
    {
        AddTransientMenuWindow(windows, windowHandle, cursorPoint);
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        AddTransientMenuWindow(
            windows,
            NativeMethods.GetAncestor(windowHandle, AncestorRoot),
            cursorPoint);

        // Windows 11 Explorer and several Chromium-based apps host their
        // popup content in child windows instead of exposing the menu as a
        // separately enumerated top-level window.
        _ = NativeMethods.EnumChildWindows(
            windowHandle,
            (childHandle, _) =>
            {
                AddTransientMenuWindow(windows, childHandle, cursorPoint);
                return true;
            },
            IntPtr.Zero);
    }

    private static void AddTransientMenuWindow(
        List<IntPtr> windows,
        IntPtr windowHandle,
        NativeMethods.NativePoint? cursorPoint = null)
    {
        if (windowHandle == IntPtr.Zero ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            (!IsTransientMenuWindowHandle(windowHandle) &&
             (!cursorPoint.HasValue ||
              !IsTransientPopupAtPoint(windowHandle, cursorPoint.Value))) ||
            windows.Contains(windowHandle))
        {
            return;
        }

        windows.Add(windowHandle);
    }

    private void StoreImmediatePreCapturedScreen(
        CapturedImage? snapshot,
        IEnumerable<HotKeyAction> actions)
    {
        if (snapshot is null)
        {
            return;
        }

        lock (_immediatePreCaptureLock)
        {
            _immediatePreCapturedScreen?.Dispose();
            _immediatePreCapturedScreen = snapshot;
            _immediatePreCapturedAt = DateTimeOffset.UtcNow;
            _immediatePreCapturedActions.Clear();
            foreach (var action in actions)
            {
                _immediatePreCapturedActions.Add(action);
            }
        }
    }

    private void ClearImmediatePreCapturedScreen()
    {
        lock (_immediatePreCaptureLock)
        {
            Interlocked.Increment(ref _contextMenuCaptureGeneration);
            _immediatePreCapturedScreen?.Dispose();
            _immediatePreCapturedScreen = null;
            _immediatePreCapturedActions.Clear();
            _immediatePreCapturedAt = default;
        }
    }

    private void QueuePinPressSnapshot(
        bool startedOnTransientMenu,
        HotKeyAction action)
    {
        ClearPreCapturedScreen();
        var generation = Interlocked.Increment(
            ref _contextMenuCaptureGeneration);
        _ = Task.Run(() =>
        {
            var snapshot = startedOnTransientMenu
                ? CaptureTransientUiScreen()
                : CaptureCurrentScreen(action);
            if (generation != Volatile.Read(
                    ref _contextMenuCaptureGeneration))
            {
                snapshot?.Dispose();
                return;
            }

            StoreImmediatePreCapturedScreen(snapshot, [action]);
        });
    }

    private void QueueTransientMenuHoldSnapshot(HotKeyAction action)
    {
        var generation = Interlocked.Increment(
            ref _contextMenuCaptureGeneration);
        _ = Task.Run(() =>
        {
            CapturedImage? snapshot = null;
            try
            {
                snapshot = CaptureTransientUiScreen();
            }
            catch
            {
                // Menu pre-capture is best effort. The hold trigger has a
                // normal desktop-capture fallback when no frame is ready.
            }

            if (generation != Volatile.Read(
                    ref _contextMenuCaptureGeneration))
            {
                snapshot?.Dispose();
                return;
            }

            StoreImmediatePreCapturedScreen(snapshot, [action]);
        });
    }

    private static CapturedImage? CaptureContextMenuAfterMouseRelease()
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(ContextMenuMouseHoldWait.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (IsTransientMenuWindowVisible())
            {
                Thread.Sleep(ContextMenuCaptureDelay);
                if (IsTransientMenuWindowVisible())
                {
                    return CaptureTransientUiScreen();
                }
            }

            Thread.Sleep(ContextMenuCapturePollInterval);
        }

        return null;
    }

    private static bool IsTransientMenuWindowVisible()
    {
        try
        {
            var hasCursorPoint = NativeMethods.GetCursorPos(out var point);
            var handles = new List<IntPtr>
            {
                NativeMethods.GetForegroundWindow(),
            };
            if (hasCursorPoint)
            {
                var underCursor = NativeMethods.WindowFromPoint(point);
                handles.Add(underCursor);
                handles.Add(NativeMethods.GetAncestor(underCursor, AncestorRoot));
            }

            foreach (var handle in handles.Where(handle => handle != IntPtr.Zero))
            {
                if (IsTransientMenuWindowHandle(handle) ||
                    (hasCursorPoint && IsTransientPopupAtPoint(handle, point)))
                {
                    return true;
                }

                var childMenuVisible = false;
                _ = NativeMethods.EnumChildWindows(
                    handle,
                    (childHandle, _) =>
                    {
                        if (IsTransientMenuWindowHandle(childHandle) ||
                            (hasCursorPoint &&
                             IsTransientPopupAtPoint(childHandle, point)))
                        {
                            childMenuVisible = true;
                            return false;
                        }

                        return true;
                    },
                    IntPtr.Zero);
                if (childMenuVisible)
                {
                    return true;
                }
            }

            var menuVisible = false;
            _ = NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                if (IsTransientMenuWindowHandle(hwnd) ||
                    (hasCursorPoint && IsTransientPopupAtPoint(hwnd, point)))
                {
                    menuVisible = true;
                    return false;
                }

                NativeMethods.EnumChildWindows(
                    hwnd,
                    (childHandle, _) =>
                    {
                        if (IsTransientMenuWindowHandle(childHandle) ||
                            (hasCursorPoint &&
                             IsTransientPopupAtPoint(childHandle, point)))
                        {
                            menuVisible = true;
                            return false;
                        }

                        return true;
                    },
                    IntPtr.Zero);
                if (menuVisible)
                {
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return menuVisible;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientMenuClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        var normalized = className.Trim();
        return normalized is
            "#32768" or
            "NotifyIconOverflowWindow" or
            "TopLevelWindowForOverflowXamlIsland" or
            "Xaml_WindowedPopupClass" or
            "Windows.UI.Core.CoreWindow" or
            "Microsoft.UI.Content.PopupWindowSiteBridge" ||
            normalized.Contains("Overflow", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Popup", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasRecentContextMenuGesture()
    {
        var timestamp = Volatile.Read(ref _lastRightButtonUpTimestamp);
        if (timestamp == 0)
        {
            return false;
        }

        var elapsed = Stopwatch.GetTimestamp() - timestamp;
        return elapsed >= 0 &&
            elapsed <= ContextMenuCaptureWindow.TotalSeconds * Stopwatch.Frequency;
    }

    private void MarkRecentContextMenuGesture(
        NativeMethods.NativePoint? releasePoint = null)
    {
        var now = Stopwatch.GetTimestamp();
        // Classify the release before deduplicating the two physical input
        // sources. Raw Input and WH_MOUSE_LL commonly report the same button
        // release within a few milliseconds; the second report still carries
        // the useful Explorer target point and must not erase that evidence.
        var likelyExplorer = releasePoint.HasValue &&
            IsExplorerTargetPoint(releasePoint.Value);
        if (likelyExplorer)
        {
            Volatile.Write(ref _lastRightButtonUpLikelyExplorer, 1);
        }

        var previous = Interlocked.Exchange(
            ref _lastRightButtonUpTimestamp,
            now);
        var duplicateWindow =
            MouseSourceDeduplicationWindow.TotalSeconds * Stopwatch.Frequency;
        if (previous != 0 && now - previous <= duplicateWindow)
        {
            return;
        }

        Volatile.Write(
            ref _lastRightButtonUpLikelyExplorer,
            likelyExplorer ? 1 : 0);

        var actions = _registeredBindings.Values
            .Where(binding => IsTransientUiCaptureAction(binding.Action))
            .Select(binding => binding.Action)
            .Distinct()
            .ToArray();
        if (actions.Length == 0)
        {
            return;
        }

        var generation = Interlocked.Increment(
            ref _contextMenuCaptureGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                var deadline = Stopwatch.GetTimestamp() +
                    (long)(ContextMenuCaptureWait.TotalSeconds *
                        Stopwatch.Frequency);
                CapturedImage? snapshot = null;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (generation != Volatile.Read(
                            ref _contextMenuCaptureGeneration) ||
                        !HasRecentContextMenuGesture())
                    {
                        return;
                    }

                    if (IsTransientMenuWindowVisible() ||
                        Volatile.Read(ref _lastRightButtonUpLikelyExplorer) != 0)
                    {
                        // Give the shell/XAML popup one compositor pass to
                        // finish painting before taking the menu frame.
                        Thread.Sleep(ContextMenuCaptureDelay);
                        if (IsTransientMenuWindowVisible() ||
                            Volatile.Read(ref _lastRightButtonUpLikelyExplorer) != 0)
                        {
                            snapshot = CaptureTransientUiScreen();
                        }

                        break;
                    }

                    Thread.Sleep(ContextMenuCapturePollInterval);
                }

                if (snapshot is null)
                {
                    return;
                }

                if (generation != Volatile.Read(
                        ref _contextMenuCaptureGeneration))
                {
                    snapshot?.Dispose();
                    return;
                }

                StoreImmediatePreCapturedScreen(snapshot, actions);
            }
            catch
            {
                // A delayed best-effort context-menu snapshot must never
                // interfere with normal global input handling.
            }
        });
    }

    private static bool IsExplorerTargetPoint(
        NativeMethods.NativePoint point)
    {
        try
        {
            var window = NativeMethods.WindowFromPoint(point);
            if (window == IntPtr.Zero)
            {
                return false;
            }

            var root = NativeMethods.GetAncestor(window, AncestorRoot);
            if (root == IntPtr.Zero)
            {
                root = window;
            }

            var className = new System.Text.StringBuilder(128);
            _ = NativeMethods.GetClassName(root, className, className.Capacity);
            if (NativeMethods.GetWindowThreadProcessId(root, out var processId) == 0 ||
                processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)processId);
            if (!process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rootClass = className.ToString();
            return !rootClass.Equals("Progman", StringComparison.OrdinalIgnoreCase) &&
                !rootClass.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) &&
                !rootClass.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemMenuForeground()
    {
        try
        {
            var handles = new List<IntPtr> { NativeMethods.GetForegroundWindow() };
            if (NativeMethods.GetCursorPos(out var point))
            {
                var underCursor = NativeMethods.WindowFromPoint(point);
                if (underCursor != IntPtr.Zero)
                {
                    handles.Add(underCursor);
                    var root = NativeMethods.GetAncestor(underCursor, 2);
                    if (root != IntPtr.Zero) handles.Add(root);
                }
            }

            foreach (var hwnd in handles.Where(handle => handle != IntPtr.Zero))
            {
                var className = new System.Text.StringBuilder(128);
                _ = NativeMethods.GetClassName(hwnd, className, className.Capacity);
                var name = className.ToString();
                if (IsTransientMenuClass(name) ||
                    name.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Popup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var menuVisible = false;
            _ = NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                var className = new System.Text.StringBuilder(128);
                _ = NativeMethods.GetClassName(hwnd, className, className.Capacity);
                var name = className.ToString();
                if (IsTransientMenuClass(name) ||
                    name.Contains("TrayNotify", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Overflow", StringComparison.OrdinalIgnoreCase))
                {
                    menuVisible = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (menuVisible)
            {
                return true;
            }

            if (NativeMethods.GetCursorPos(out var cursor))
            {
                var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
                const int taskbarProximity = 96;
                return cursor.Y >= virtualScreen.Bottom - taskbarProximity ||
                    cursor.Y <= virtualScreen.Top + taskbarProximity ||
                    cursor.X >= virtualScreen.Right - taskbarProximity ||
                    cursor.X <= virtualScreen.Left + taskbarProximity;
            }

            return false;
        }
        catch
        {
            return false;
        }
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

        // Raw input also reports every physical movement.  Movement is only
        // relevant while a mouse-hold gesture is pending; otherwise parsing
        // and dispatching it on the hidden input window is pure overhead.
        // This is especially important during manual long-screenshot mode,
        // where the selection owns the pointer but the global manager remains
        // registered for button and wheel shortcuts.
        if (mouse.ButtonFlags == 0 &&
            _pendingMouseHolds.Count == 0 &&
            _modifierProbeHolds.Count == 0)
        {
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

        // Raw input arrives once per hardware report.  There is no reason to
        // query the cursor position or walk the pending-hold maps for ordinary
        // movement when no mouse-hold gesture is waiting to be resolved.  At
        // high report rates (and while manual long-screenshot mode is active)
        // this otherwise turns a no-op mouse move into a synchronous Win32
        // call on the input thread for every packet.
        if ((mouse.LastX != 0 || mouse.LastY != 0) &&
            (_pendingMouseHolds.Count > 0 ||
             _modifierProbeHolds.Count > 0))
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

        if (virtualKey == HotKeyGesture.VirtualKeyMouseRight &&
            !isButtonDown &&
            !ShouldSuppressContextMenuGesture(isButtonDown))
        {
            MarkRecentContextMenuGesture(point);
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

    [Conditional("SNAPCUT_INPUT_DIAGNOSTICS")]
    private static void WriteInputDiagnostic(string _)
    {
        // Intentionally disabled. Global input hooks must never perform
        // diagnostic formatting or synchronous disk I/O.
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
        bool AllowForegroundModifierProbe,
        bool StartedOnTransientMenu);

    private sealed record PendingImmediateMouseCapture(
        HotKeyBinding Binding,
        NativeMethods.NativePoint StartPoint,
        CapturePointerContinuation? Continuation);

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

        public delegate bool EnumWindowsProcedure(IntPtr windowHandle, IntPtr parameter);

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
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(
            IntPtr parentWindowHandle,
            EnumWindowsProcedure callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(
            IntPtr windowHandle,
            IntPtr deviceContext,
            uint flags);

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

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(
            IntPtr windowHandle,
            int index);

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
