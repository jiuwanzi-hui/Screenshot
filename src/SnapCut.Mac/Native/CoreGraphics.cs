using System.Runtime.InteropServices;

namespace SnapCut.Mac.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;

    public CGRect(double x, double y, double width, double height)
    {
        Origin = new CGPoint { X = x, Y = y };
        Size = new CGSize { Width = width, Height = height };
    }

    public readonly double Left => Origin.X;

    public readonly double Top => Origin.Y;

    public readonly double Right => Origin.X + Size.Width;

    public readonly double Bottom => Origin.Y + Size.Height;
}

/// <summary>
/// CoreGraphics C bindings for display enumeration, screen capture and scroll
/// event taps. All of these are plain C ABI — no Objective-C runtime involved.
/// </summary>
internal static partial class CoreGraphics
{
    private const string Library =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // CGBitmapInfo
    public const uint BitmapAlphaInfoMask = 0x1F;
    public const uint BitmapByteOrderMask = 0x7000;
    public const uint BitmapByteOrder32Little = 2 << 12;
    public const uint BitmapByteOrder32Big = 4 << 12;
    public const uint ImageAlphaNone = 0;
    public const uint ImageAlphaPremultipliedLast = 1;
    public const uint ImageAlphaPremultipliedFirst = 2;
    public const uint ImageAlphaLast = 3;
    public const uint ImageAlphaFirst = 4;
    public const uint ImageAlphaNoneSkipLast = 5;
    public const uint ImageAlphaNoneSkipFirst = 6;

    // CGEventTap
    public const uint EventTapSession = 1; // kCGSessionEventTap
    public const uint EventTapHeadInsert = 0; // kCGHeadInsertEventTap
    public const uint EventTapOptionListenOnly = 1;
    public const uint EventKeyDown = 10;
    public const uint EventLeftMouseDown = 1;
    public const uint EventRightMouseDown = 3;
    public const uint EventScrollWheel = 22; // kCGEventScrollWheel
    public const uint EventTapDisabledByTimeout = 0xFFFFFFFE;
    public const uint EventTapDisabledByUserInput = 0xFFFFFFFF;
    public const int KeyboardEventAutorepeat = 8;
    public const int KeyboardEventKeycode = 9;
    public const int ScrollWheelEventDeltaAxis1 = 11;
    public const int ScrollWheelEventIsContinuous = 88;
    public const int ScrollWheelEventPointDeltaAxis1 = 96;
    public const uint WindowListOptionOnScreenOnly = 1;
    public const uint WindowListExcludeDesktopElements = 16;
    public const uint ScrollEventUnitPixel = 0;
    public const uint EventTapHid = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr EventTapCallback(
        IntPtr proxy,
        uint eventType,
        IntPtr cgEvent,
        IntPtr userInfo);

    [LibraryImport(Library)]
    public static partial int CGGetActiveDisplayList(
        uint maxDisplays,
        [Out] uint[] activeDisplays,
        out uint displayCount);

    [LibraryImport(Library)]
    public static partial CGRect CGDisplayBounds(uint display);

    [LibraryImport(Library)]
    public static partial nuint CGDisplayPixelsWide(uint display);

    [LibraryImport(Library)]
    public static partial nuint CGDisplayPixelsHigh(uint display);

    [LibraryImport(Library)]
    public static partial uint CGMainDisplayID();

    [LibraryImport(Library)]
    public static partial IntPtr CGDisplayCreateImageForRect(
        uint display,
        CGRect rect);

    [LibraryImport(Library)]
    public static partial nuint CGImageGetWidth(IntPtr image);

    [LibraryImport(Library)]
    public static partial nuint CGImageGetHeight(IntPtr image);

    [LibraryImport(Library)]
    public static partial nuint CGImageGetBytesPerRow(IntPtr image);

    [LibraryImport(Library)]
    public static partial nuint CGImageGetBitsPerPixel(IntPtr image);

    [LibraryImport(Library)]
    public static partial uint CGImageGetBitmapInfo(IntPtr image);

    [LibraryImport(Library)]
    public static partial IntPtr CGImageGetDataProvider(IntPtr image);

    [LibraryImport(Library)]
    public static partial IntPtr CGDataProviderCopyData(IntPtr provider);

    [LibraryImport(Library)]
    public static partial IntPtr CGColorSpaceCreateDeviceRGB();

    [LibraryImport(Library)]
    public static partial IntPtr CGDataProviderCreateWithData(
        IntPtr info,
        IntPtr data,
        nuint size,
        IntPtr releaseCallback);

    [LibraryImport(Library)]
    public static partial IntPtr CGImageCreate(
        nuint width,
        nuint height,
        nuint bitsPerComponent,
        nuint bitsPerPixel,
        nuint bytesPerRow,
        IntPtr colorSpace,
        uint bitmapInfo,
        IntPtr provider,
        IntPtr decode,
        [MarshalAs(UnmanagedType.I1)] bool shouldInterpolate,
        int intent);

    [DllImport(Library)]
    public static extern IntPtr CGEventTapCreate(
        uint tap,
        uint place,
        uint options,
        ulong eventsOfInterest,
        EventTapCallback callback,
        IntPtr userInfo);

    [LibraryImport(Library)]
    public static partial void CGEventTapEnable(
        IntPtr tap,
        [MarshalAs(UnmanagedType.I1)] bool enable);

    [LibraryImport(Library)]
    public static partial long CGEventGetIntegerValueField(
        IntPtr cgEvent,
        int field);

    [LibraryImport(Library)]
    public static partial ulong CGEventGetFlags(IntPtr cgEvent);

    [LibraryImport(Library)]
    public static partial IntPtr CGEventCreate(IntPtr source);

    [LibraryImport(Library)]
    public static partial CGPoint CGEventGetLocation(IntPtr cgEvent);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGPreflightScreenCaptureAccess();

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGRequestScreenCaptureAccess();

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGPreflightListenEventAccess();

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGRequestListenEventAccess();

    [LibraryImport(Library)]
    public static partial IntPtr CGWindowListCopyWindowInfo(
        uint option,
        uint relativeToWindow);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGRectMakeWithDictionaryRepresentation(
        IntPtr dictionary,
        out CGRect rectangle);

    [DllImport(Library)]
    public static extern IntPtr CGEventCreateScrollWheelEvent(
        IntPtr source,
        uint units,
        uint wheelCount,
        int wheel1);

    [LibraryImport(Library)]
    public static partial void CGEventPost(uint tap, IntPtr cgEvent);

    [LibraryImport(Library)]
    public static partial int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    public static IntPtr WindowBoundsKey { get; } = ResolveConstant("kCGWindowBounds");

    public static IntPtr WindowLayerKey { get; } = ResolveConstant("kCGWindowLayer");

    public static IntPtr WindowOwnerPidKey { get; } = ResolveConstant("kCGWindowOwnerPID");

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
