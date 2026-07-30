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
        CapturedImage? preCapturedScreen = null)
    {
        Action = action;
        _preCapturedScreen = preCapturedScreen;
    }

    public HotKeyAction Action { get; }

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
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
    }

    public uint VirtualKey { get; }

    public HotKeyModifiers Modifiers { get; }
}

public sealed class GlobalHotKeyManager : IDisposable
{
    private const int HotKeyAlreadyRegisteredError = 1409;
    private const int WindowMessageHotKey = 0x0312;
    private const int MessageOnlyWindow = -3;
    private const int LowLevelKeyboardHook = 13;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageKeyUp = 0x0101;
    private const int WindowMessageSystemKeyDown = 0x0104;
    private const int WindowMessageSystemKeyUp = 0x0105;
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
    private readonly DispatcherTimer _preCaptureExpiryTimer;
    private readonly Dictionary<int, HotKeyBinding> _registeredBindings = [];
    private readonly HashSet<HotKeyAction> _preCapturedActions = [];
    private readonly HashSet<uint> _capturedModifierKeysDown = [];
    private IntPtr _keyboardHook;
    private CapturedImage? _preCapturedScreen;
    private DateTimeOffset _preCapturedAt;
    private bool _isKeyboardCaptureActive;
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
        _keyboardProcedure = OnLowLevelKeyboardMessage;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            LowLevelKeyboardHook,
            _keyboardProcedure,
            NativeMethods.GetModuleHandle(moduleName: null),
            threadId: 0);
    }

    public event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    public event EventHandler<HotKeyCaptureInputEventArgs>? HotKeyCaptureInputReceived;

    public IReadOnlyList<HotKeyBinding> RegisteredBindings =>
        _registeredBindings.Values.OrderBy(binding => binding.Action).ToArray();

    internal bool IsKeyboardCaptureActive => _isKeyboardCaptureActive;

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
            _ = NativeMethods.UnregisterHotKey(_messageSource.Handle, identifier);
        }

        _registeredBindings.Clear();
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
            var eventArgs = new HotKeyPressedEventArgs(
                binding.Action,
                TakePreCapturedScreen(binding.Action));
            try
            {
                HotKeyPressed?.Invoke(this, eventArgs);
            }
            finally
            {
                eventArgs.DisposeUnusedPreCapturedScreen();
            }

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

    private static class NativeMethods
    {
        public delegate IntPtr LowLevelKeyboardProcedure(
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
