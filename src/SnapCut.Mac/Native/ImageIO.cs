using System.Runtime.InteropServices;

namespace SnapCut.Mac.Native;

/// <summary>ImageIO C bindings used to write PNG files.</summary>
internal static partial class ImageIO
{
    private const string Library =
        "/System/Library/Frameworks/ImageIO.framework/ImageIO";

    [LibraryImport(Library)]
    public static partial IntPtr CGImageDestinationCreateWithURL(
        IntPtr url,
        IntPtr type,
        nuint count,
        IntPtr options);

    [LibraryImport(Library)]
    public static partial void CGImageDestinationAddImage(
        IntPtr destination,
        IntPtr image,
        IntPtr properties);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool CGImageDestinationFinalize(IntPtr destination);
}
