using System.Drawing;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Tests;

public sealed class ResidentSelectionInputTests
{
    [Theory]
    [InlineData(HotKeyAction.RegionCapture, true)]
    [InlineData(HotKeyAction.PinImage, true)]
    [InlineData(HotKeyAction.VideoRecording, true)]
    [InlineData(HotKeyAction.RealtimeTranslation, true)]
    [InlineData(HotKeyAction.ScrollCapture, false)]
    [InlineData(HotKeyAction.EndVideoRecording, false)]
    [InlineData(HotKeyAction.OpenSettings, false)]
    public void ColdSelectionRoutesPinAndVideoThroughResidentSurface(HotKeyAction action, bool expected) =>
        Assert.Equal(expected, App.CanUseResidentSelection(new("command") { HotKey = action }));

    [Fact]
    public void ExistingPointerAndPixelOwnershipStayOnOriginalPaths()
    {
        using var image = new CapturedImage(new Bitmap(4, 4));
        foreach (var action in new[] { HotKeyAction.RegionCapture, HotKeyAction.PinImage, HotKeyAction.VideoRecording })
        {
            Assert.False(App.CanUseResidentSelection(new("command") { HotKey = action, Image = image }));
            Assert.False(App.CanUseResidentSelection(new("command") { HotKey = action, Button = CapturePointerButton.Right }));
        }
    }

    // Hidden HWNDs only; no activation, language switching or global key injection.
    [DesktopTheory]
    [InlineData(0x43)]
    [InlineData(0x1B)]
    public void PickerKeysDoNotUseImeOrChangeOtherWindowsInputContext(int key)
    {
        WpfTestHost.Invoke(() =>
        {
            var layout = GetKeyboardLayout(0);
            var other = new WinForms.NativeWindow();
            other.CreateHandle(new WinForms.CreateParams { Caption = "IME regression (hidden)", Width = 1, Height = 1 });
            var context = ImmCreateContext();
            Assert.NotEqual(IntPtr.Zero, context);
            var previous = ImmAssociateContext(other.Handle, context);
            try
            {
                using var frame = new NativeDesktopFrame(new(0, 0, 8, 8));
                for (var i = 0; i < 64; i++) Marshal.WriteInt32(frame.Pixels, i * 4, 0x123456);
                var cancelled = false;
                using (var picker = new ResidentSelectionWindow(frame, AppSettings.CreateDefault(), () => cancelled = true))
                {
                    Assert.Equal(IntPtr.Zero, ImmGetContext(picker.Handle));
                    AssertContext(other.Handle, context);
                    _ = SendMessageW(picker.Handle, 0x100, new IntPtr(key), IntPtr.Zero);
                    Assert.True(picker.Selection.IsCompletedSuccessfully);
                    Assert.Equal(key == 0x1B, cancelled);
                    Assert.Equal(key == 0x43 ? "#123456" : null, picker.Selection.Result?.Color);
                }
                AssertContext(other.Handle, context);
                Assert.Equal(layout, GetKeyboardLayout(0));
            }
            finally
            {
                _ = ImmAssociateContext(other.Handle, previous);
                other.DestroyHandle();
                _ = ImmDestroyContext(context);
            }
        });
    }

    private static void AssertContext(IntPtr window, IntPtr expected)
    {
        var context = ImmGetContext(window);
        try { Assert.Equal(expected, context); }
        finally { if (context != IntPtr.Zero) _ = ImmReleaseContext(window, context); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("imm32.dll")] private static extern IntPtr ImmCreateContext();
    [DllImport("imm32.dll")] private static extern bool ImmDestroyContext(IntPtr context);
    [DllImport("imm32.dll")] private static extern IntPtr ImmAssociateContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
}
