using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

public sealed record HotKeyRegistrationResult(bool IsSuccess, string? ErrorMessage)
{
    public static HotKeyRegistrationResult Success { get; } = new(true, ErrorMessage: null);
}

public sealed class HotKeyPressedEventArgs : EventArgs
{
    public HotKeyPressedEventArgs(HotKeyAction action)
    {
        Action = action;
    }

    public HotKeyAction Action { get; }
}

public sealed class GlobalHotKeyManager : IDisposable
{
    private const int HotKeyAlreadyRegisteredError = 1409;
    private const int WindowMessageHotKey = 0x0312;
    private const int MessageOnlyWindow = -3;

    private readonly HwndSource _messageSource;
    private readonly Dictionary<int, HotKeyBinding> _registeredBindings = [];
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
    }

    public event EventHandler<HotKeyPressedEventArgs>? HotKeyPressed;

    public IReadOnlyList<HotKeyBinding> RegisteredBindings =>
        _registeredBindings.Values.OrderBy(binding => binding.Action).ToArray();

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
        UnregisterAll();
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
            HotKeyPressed?.Invoke(this, new HotKeyPressedEventArgs(binding.Action));
            handled = true;
        }

        return IntPtr.Zero;
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
    }
}
