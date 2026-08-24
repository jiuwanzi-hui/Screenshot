using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Screenshot.App.Capture;

public static class ClipboardTextService
{
    private const uint GlobalMoveable = 0x0002;
    private const uint ClipboardFormatUnicodeText = 13;
    private const int ClipboardOpenAttempts = 10;
    private const int ClipboardRetryDelayMilliseconds = 20;

    public static Task SetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("剪贴板文字不能为空。", nameof(text));
        }

        return Task.Run(
            () => SetNativeClipboardText(text, cancellationToken),
            cancellationToken);
    }

    private static void SetNativeClipboardText(
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var memoryHandle = NativeMethods.GlobalAlloc(
            GlobalMoveable,
            (nuint)bytes.Length);
        if (memoryHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法分配剪贴板文字内存。");
        }

        try
        {
            var memory = NativeMethods.GlobalLock(memoryHandle);
            if (memory == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法锁定剪贴板文字内存。");
            }

            try
            {
                Marshal.Copy(bytes, 0, memory, bytes.Length);
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(memoryHandle);
            }

            for (var attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    if (attempt + 1 < ClipboardOpenAttempts)
                    {
                        Thread.Sleep(ClipboardRetryDelayMilliseconds);
                        continue;
                    }

                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "剪贴板正被其他程序使用。");
                }

                try
                {
                    if (!NativeMethods.EmptyClipboard())
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "无法清空剪贴板。");
                    }

                    if (NativeMethods.SetClipboardData(
                            ClipboardFormatUnicodeText,
                            memoryHandle) == IntPtr.Zero)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "无法写入剪贴板文字。");
                    }

                    memoryHandle = IntPtr.Zero;
                    return;
                }
                finally
                {
                    _ = NativeMethods.CloseClipboard();
                }
            }
        }
        finally
        {
            if (memoryHandle != IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(memoryHandle);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr memoryHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr memoryHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr memoryHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr ownerWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(
            uint format,
            IntPtr memoryHandle);
    }
}
