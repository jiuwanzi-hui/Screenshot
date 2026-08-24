using Avalonia.Controls;

namespace SnapCut.Mac.Native;

internal static class MacNativeUi
{
    private const nint ModalResponseOk = 1;

    public static bool CopyPngFile(string path)
    {
        var pathString = ObjectiveC.CreateString(Path.GetFullPath(path));
        var typeString = ObjectiveC.CreateString("public.png");
        try
        {
            var data = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSData"),
                "dataWithContentsOfFile:",
                pathString);
            var pasteboard = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSPasteboard"),
                "generalPasteboard");
            if (data == IntPtr.Zero || pasteboard == IntPtr.Zero)
            {
                return false;
            }

            _ = ObjectiveC.SendNInt(pasteboard, "clearContents");
            return ObjectiveC.SendBool(
                pasteboard,
                "setData:forType:",
                data,
                typeString);
        }
        finally
        {
            ObjectiveC.Release(typeString);
            ObjectiveC.Release(pathString);
        }
    }

    public static string? SelectPngSavePath(string suggestedName)
    {
        var panel = ObjectiveC.SendIntPtr(
            ObjectiveC.GetClass("NSSavePanel"),
            "savePanel");
        var name = ObjectiveC.CreateString(suggestedName);
        try
        {
            ObjectiveC.SendVoid(panel, "setNameFieldStringValue:", name);
            if (ObjectiveC.SendNInt(panel, "runModal") != ModalResponseOk)
            {
                return null;
            }

            var url = ObjectiveC.SendIntPtr(panel, "URL");
            var path = ObjectiveC.SendIntPtr(url, "path");
            return ObjectiveC.ReadString(path);
        }
        finally
        {
            ObjectiveC.Release(name);
        }
    }

    public static bool OpenPath(string path)
    {
        var pathString = ObjectiveC.CreateString(Path.GetFullPath(path));
        try
        {
            var url = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSURL"),
                "fileURLWithPath:",
                pathString);
            var workspace = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSWorkspace"),
                "sharedWorkspace");
            return ObjectiveC.SendBool(workspace, "openURL:", url);
        }
        finally
        {
            ObjectiveC.Release(pathString);
        }
    }

    public static bool CopyText(string text)
    {
        var value = ObjectiveC.CreateString(text);
        var type = ObjectiveC.CreateString("public.utf8-plain-text");
        try
        {
            var pasteboard = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSPasteboard"),
                "generalPasteboard");
            if (pasteboard == IntPtr.Zero)
            {
                return false;
            }

            _ = ObjectiveC.SendNInt(pasteboard, "clearContents");
            return ObjectiveC.SendBool(
                pasteboard,
                "setString:forType:",
                value,
                type);
        }
        finally
        {
            ObjectiveC.Release(type);
            ObjectiveC.Release(value);
        }
    }

    public static bool OpenUrl(string address)
    {
        var addressString = ObjectiveC.CreateString(address);
        try
        {
            var url = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSURL"),
                "URLWithString:",
                addressString);
            var workspace = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSWorkspace"),
                "sharedWorkspace");
            return url != IntPtr.Zero &&
                ObjectiveC.SendBool(workspace, "openURL:", url);
        }
        finally
        {
            ObjectiveC.Release(addressString);
        }
    }

    public static void OpenPrivacySettings(string privacyPane)
    {
        // macOS 13+ uses the PrivacySecurity extension. Keep the legacy URL as
        // a fallback for Monterey/Ventura installations.
        var modern =
            $"x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?{privacyPane}";
        if (OpenUrl(modern))
        {
            return;
        }

        _ = OpenUrl($"x-apple.systempreferences:com.apple.preference.security?{privacyPane}");
    }

    public static void ExcludeFromScreenCapture(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            ObjectiveC.SendVoid(handle, "setSharingType:", IntPtr.Zero);
        }
    }
}
