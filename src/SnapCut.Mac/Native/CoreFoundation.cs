using System.Runtime.InteropServices;

namespace SnapCut.Mac.Native;

/// <summary>
/// Minimal CoreFoundation bindings. Every returned CF object follows the
/// Create/Copy rule and must be released with <see cref="CFRelease"/>.
/// </summary>
internal static partial class CoreFoundation
{
    private const string Library =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint KCFStringEncodingUtf8 = 0x08000100;
    public const int KCFURLPosixPathStyle = 0;

    [LibraryImport(Library)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        string value,
        uint encoding);

    [LibraryImport(Library)]
    public static partial IntPtr CFURLCreateWithFileSystemPath(
        IntPtr allocator,
        IntPtr filePath,
        int pathStyle,
        [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [LibraryImport(Library)]
    public static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(Library)]
    public static partial long CFDataGetLength(IntPtr data);

    [LibraryImport(Library)]
    public static partial IntPtr CFMachPortCreateRunLoopSource(
        IntPtr allocator,
        IntPtr port,
        long order);

    [LibraryImport(Library)]
    public static partial IntPtr CFRunLoopGetCurrent();

    [LibraryImport(Library)]
    public static partial void CFRunLoopAddSource(
        IntPtr runLoop,
        IntPtr source,
        IntPtr mode);

    [LibraryImport(Library)]
    public static partial void CFRunLoopRun();

    [LibraryImport(Library)]
    public static partial void CFRunLoopStop(IntPtr runLoop);

    /// <summary>kCFRunLoopCommonModes constant, resolved from the framework.</summary>
    public static IntPtr RunLoopCommonModes { get; } = ResolveConstant("kCFRunLoopCommonModes");

    private static IntPtr ResolveConstant(string symbol)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return IntPtr.Zero;
        }

        var handle = NativeLibrary.Load(Library);
        try
        {
            return NativeLibrary.TryGetExport(handle, symbol, out var export)
                ? Marshal.ReadIntPtr(export)
                : IntPtr.Zero;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
