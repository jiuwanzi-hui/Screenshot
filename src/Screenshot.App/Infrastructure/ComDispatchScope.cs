using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Screenshot.App.Infrastructure;

// Task Scheduler is used briefly at startup. IDispatch reflection avoids loading
// the C# dynamic compiler/binder and its permanent call-site caches into the tray.
internal sealed class ComDispatchScope : IDisposable
{
    private readonly List<object> _objects = [];

    public object Track(object value)
    {
        if (Marshal.IsComObject(value)) _objects.Add(value);
        return value;
    }

    public object Get(object target, string name, params object?[] args) =>
        Invoke(target, name, BindingFlags.GetProperty, args)!;

    public object Call(object target, string name, params object?[] args) =>
        Invoke(target, name, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding, args)!;

    public void Set(object target, string name, object value) =>
        Invoke(target, name, BindingFlags.SetProperty, [value]);

    private object? Invoke(object target, string name, BindingFlags flags, object?[] args)
    {
        try
        {
            var result = target.GetType().InvokeMember(name, flags, null, target, args,
                System.Globalization.CultureInfo.InvariantCulture);
            return result is null ? null : Track(result);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        for (var index = _objects.Count - 1; index >= 0; index--)
        {
            if (Marshal.IsComObject(_objects[index])) Marshal.ReleaseComObject(_objects[index]);
        }
        _objects.Clear();
    }
}
