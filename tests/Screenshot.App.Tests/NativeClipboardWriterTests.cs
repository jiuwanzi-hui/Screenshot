using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class NativeClipboardWriterTests
{
    [Fact]
    public void FileDropContainsUnicodePathAndDoubleTerminator()
    {
        const string path = @"D:\视频文件\测试.mp4";
        var payload = ClipboardFileService.CreateFileDropPayload(path);
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(16)));
        Assert.Equal(path + "\0\0", Encoding.Unicode.GetString(payload, 20, payload.Length - 20));
    }

    [ClipboardFact]
    public async Task AllFormatsReleaseExclusiveLockAndSurviveOwnerDestruction()
    {
        using var saved = new SavedClipboard();
        var path = Path.GetTempFileName();
        try
        {
            await ClipboardTextService.SetTextAsync("转录文本 Clipboard ✓");
            AssertAvailableToOtherThread();
            ReadClipboard(() => Assert.Equal("转录文本 Clipboard ✓", Clipboard.GetText()));

            await ClipboardTextService.SetHtmlAsync("<b>测试</b>", "测试");
            AssertAvailableToOtherThread();
            ReadClipboard(() =>
            {
                Assert.Equal("测试", Clipboard.GetText());
                Assert.Contains("<b>测试</b>", (string)Clipboard.GetData(DataFormats.Html));
            });

            var pixels = new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 };
            var bitmap = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            bitmap.Freeze();
            await ClipboardImageService.SetImageAsync(bitmap);
            AssertAvailableToOtherThread();
            ReadClipboard(() =>
            {
                var image = Clipboard.GetImage();
                Assert.NotNull(image);
                Assert.Equal(1, image.PixelWidth);
                Assert.Equal(2, image.PixelHeight);
                var copy = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                var actual = new byte[8];
                copy.CopyPixels(actual, 4, 0);
                Assert.Equal(pixels, actual);
            });

            await ClipboardFileService.SetFileAsync(path);
            AssertAvailableToOtherThread();
            ReadClipboard(() => Assert.Equal(path, Assert.Single(Clipboard.GetFileDropList().Cast<string>())));

            // Mixed formats must remain ordered; image preparation must not race
            // and overwrite a later transcription/text copy.
            var first = ClipboardImageService.SetImageAsync(bitmap);
            var second = ClipboardFileService.SetFileAsync(path);
            var last = ClipboardTextService.SetTextAsync("last transcription");
            await Task.WhenAll(first, second, last);
            AssertAvailableToOtherThread();
            ReadClipboard(() => Assert.Equal("last transcription", Clipboard.GetText()));
            Assert.False(ClipboardTextService.HasPendingWrites);
        }
        finally { File.Delete(path); }
    }

    [ClipboardFact]
    public async Task BusyClipboardHasBoundedRetriesCancellationAndRecovery()
    {
        using var saved = new SavedClipboard();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            var window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, window);
            try
            {
                OpenWithRetry(window);
                try
                {
                    entered.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                finally { CloseClipboard(); }
            }
            finally { _ = DestroyWindow(window); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<Win32Exception>(() => ClipboardTextService.SetTextAsync("busy").WaitAsync(TimeSpan.FromSeconds(3)));
            using var cancellation = new CancellationTokenSource(35);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardTextService.SetTextAsync("cancelled", cancellation.Token));
            Assert.False(ClipboardTextService.HasPendingWrites);
        }
        finally { release.Set(); await holder; }
        await ClipboardTextService.SetTextAsync("recovered");
        AssertAvailableToOtherThread();
        ReadClipboard(() => Assert.Equal("recovered", Clipboard.GetText()));
    }

    [ClipboardFact]
    public async Task PublishingFailureStillClosesClipboardAndNextCopyWorks()
    {
        using var saved = new SavedClipboard();
        await Assert.ThrowsAsync<Win32Exception>(() => NativeClipboardWriter.WriteAsync(() =>
        {
            using var invalid = new ClipboardPayload(0, [0, 0]);
            NativeClipboardWriter.Publish(CancellationToken.None, invalid);
        }, CancellationToken.None));
        AssertAvailableToOtherThread();
        await ClipboardTextService.SetTextAsync("recovered after failure");
        AssertAvailableToOtherThread();
    }

    private static void ReadClipboard(Action assertion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { WpfTestHost.Invoke(assertion); return; }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x800401D0) && attempt < 49)
            {
                // External clipboard synchronization can briefly open it
                // between the successful exclusive probe and this read.
                Thread.Sleep(20);
            }
        }
    }

    private static void OpenWithRetry(IntPtr window)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(window)) return;
            if (attempt >= 49) throw new Win32Exception(Marshal.GetLastWin32Error());
            Thread.Sleep(20);
        }
    }

    private static void AssertAvailableToOtherThread()
    {
        Task.Factory.StartNew(() =>
        {
            var open = false;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                open = OpenClipboard(IntPtr.Zero);
                if (open) break;
                var opener = GetOpenClipboardWindow();
                _ = GetWindowThreadProcessId(opener, out var openerPid);
                Assert.NotEqual((uint)Environment.ProcessId, openerPid);
                // Windows history/other clipboard managers also react to a
                // changed payload. Only tolerate contention from another process.
                Thread.Sleep(20);
            }
            Assert.True(open, "Another process kept the clipboard busy for one second.");
            try
            {
                // A rendered native payload does not depend on a live SnapCut
                // owner window or on its UI message pump after the write.
                var owner = GetClipboardOwner();
                _ = GetWindowThreadProcessId(owner, out var ownerPid);
                Assert.NotEqual((uint)Environment.ProcessId, ownerPid);
            }
            finally { Assert.True(CloseClipboard()); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
    }

    private sealed class SavedClipboard : IDisposable
    {
        private readonly List<(uint Format, byte[] Bytes)> _snapshot = [];
        internal SavedClipboard()
        {
            OpenWithRetry(IntPtr.Zero);
            try
            {
                for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                {
                    // CF_BITMAP is a GDI handle, but DIB carries the same pixels.
                    if (format == 2 && (IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17))) continue;
                    if (format is 2 or 3 or 9 or 14)
                        throw new NotSupportedException("This clipboard contains non-memory GDI data; leave it unchanged.");
                    var name = new char[256];
                    var nameLength = GetClipboardFormatName(format, name, name.Length);
                    if (new string(name, 0, nameLength) is "Ole Private Data" or "DataObject") continue;
                    var handle = GetClipboardData(format);
                    var size = checked((int)GlobalSize(handle));
                    if (handle == IntPtr.Zero || size == 0)
                        throw new NotSupportedException("Cannot safely snapshot clipboard format " + format);
                    var memory = GlobalLock(handle);
                    if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var bytes = new byte[size];
                    try { Marshal.Copy(memory, bytes, 0, size); }
                    finally { _ = GlobalUnlock(handle); }
                    _snapshot.Add((format, bytes));
                }
            }
            finally { CloseClipboard(); }
        }
        public void Dispose()
        {
            var payloads = new List<ClipboardPayload>();
            try
            {
                foreach (var item in _snapshot) payloads.Add(new ClipboardPayload(item.Format, item.Bytes));
                NativeClipboardWriter.Publish(CancellationToken.None, payloads.ToArray());
            }
            finally { foreach (var payload in payloads) payload.Dispose(); }
        }
    }

    private sealed class ClipboardFactAttribute : FactAttribute
    {
        public ClipboardFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_CLIPBOARD_TESTS") != "1")
                Skip = "Opt-in real clipboard test; restores the previous clipboard contents.";
        }
    }

    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, [Out] char[] name, int length);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
}
