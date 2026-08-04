using System.Runtime.InteropServices;

namespace SnapCut.Mac.Native;

internal static class ObjectiveC
{
    private const string Library = "/usr/lib/libobjc.A.dylib";
    private const string AppKit =
        "/System/Library/Frameworks/AppKit.framework/AppKit";
    private static readonly IntPtr AppKitHandle = LoadAppKit();

    [DllImport(Library)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Library)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MessageIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MessageIntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MessageBool(
        IntPtr receiver,
        IntPtr selector,
        IntPtr first,
        IntPtr second);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    private static extern nint MessageNInt(IntPtr receiver, IntPtr selector);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    private static extern void MessageVoid(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(Library, EntryPoint = "objc_msgSend")]
    private static extern void MessageVoid(
        IntPtr receiver,
        IntPtr selector,
        IntPtr first,
        IntPtr second);

    public static IntPtr GetClass(string name) => objc_getClass(name);

    public static IntPtr GetSelector(string name) => sel_registerName(name);

    public static IntPtr SendIntPtr(IntPtr receiver, string selector) =>
        MessageIntPtr(receiver, GetSelector(selector));

    public static IntPtr SendIntPtr(
        IntPtr receiver,
        string selector,
        IntPtr argument) =>
        MessageIntPtr(receiver, GetSelector(selector), argument);

    public static bool SendBool(
        IntPtr receiver,
        string selector,
        IntPtr first,
        IntPtr second) =>
        MessageBool(receiver, GetSelector(selector), first, second);

    public static nint SendNInt(IntPtr receiver, string selector) =>
        MessageNInt(receiver, GetSelector(selector));

    public static void SendVoid(
        IntPtr receiver,
        string selector,
        IntPtr argument) =>
        MessageVoid(receiver, GetSelector(selector), argument);

    public static void SendVoid(
        IntPtr receiver,
        string selector,
        IntPtr first,
        IntPtr second) =>
        MessageVoid(receiver, GetSelector(selector), first, second);

    public static IntPtr CreateString(string value)
    {
        var instance = SendIntPtr(GetClass("NSString"), "alloc");
        var bytes = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return SendIntPtr(instance, "initWithUTF8String:", bytes);
        }
        finally
        {
            Marshal.FreeCoTaskMem(bytes);
        }
    }

    public static string? ReadString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        var bytes = SendIntPtr(value, "UTF8String");
        return bytes == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(bytes);
    }

    public static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            _ = SendIntPtr(value, "release");
        }
    }

    private static IntPtr LoadAppKit()
    {
        return OperatingSystem.IsMacOS()
            ? NativeLibrary.Load(AppKit)
            : IntPtr.Zero;
    }
}
