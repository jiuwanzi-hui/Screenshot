using System.Drawing;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class SelectionMaskPresentationTests
{
    [DesktopFact]
    public void HiddenHelpersNeverActivateWhenCreatedOrTheirHandlesAreRecreated()
    {
        WpfTestHost.Invoke(() =>
        {
            var foreground = GetForegroundWindow();
            using var frame = new NativeSelectionFrameWindow();
            using var mask = new NativeSelectionMaskWindow();
            using var size = new NativeSelectionSizeWindow();
            Assert.Equal(foreground, GetForegroundWindow());
            var recreate = typeof(System.Windows.Forms.Control).GetMethod("RecreateHandle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var form in new System.Windows.Forms.Form[] { frame, mask, size })
            {
                Assert.False(IsWindowVisible(form.Handle));
                Assert.NotEqual(0L, GetWindowLongPtrW(form.Handle, -20).ToInt64() & 0x08000000L);
                recreate.Invoke(form, null);
                Assert.False(IsWindowVisible(form.Handle));
                Assert.NotEqual(0L, GetWindowLongPtrW(form.Handle, -20).ToInt64() & 0x08000000L);
                Assert.Equal(foreground, GetForegroundWindow());
            }
        });
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DesktopFact]
    public void CoveredMaskCannotBeRaisedAboveTheAlreadyDimmedResidentFrame()
    {
        WpfTestHost.Invoke(() =>
        {
            using var owner = new System.Windows.Forms.Form { ShowInTaskbar = false, TopMost = true };
            using var cover = new System.Windows.Forms.Form { ShowInTaskbar = false, TopMost = true };
            using var mask = new NativeSelectionMaskWindow();
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            var surface = new Rectangle(desktop.Right + 100, desktop.Bottom + 100, 320, 240);
            const uint noActivateShow = 0x0010 | 0x0040;
            Assert.True(SetWindowPos(owner.Handle, new(-1), surface.X, surface.Y, surface.Width, surface.Height, noActivateShow));
            _ = SetWindowLongPtr(cover.Handle, -8, owner.Handle);
            Assert.True(SetWindowPos(cover.Handle, new(-1), surface.X, surface.Y, surface.Width, surface.Height, noActivateShow));
            mask.SetOwner(owner.Handle);
            mask.SetPresentationCover(cover.Handle);
            mask.Update(surface, new(surface.X + 20, surface.Y + 20, 100, 90));
            Assert.True(IsAbove(cover.Handle, mask.Handle));

            // A late owner/WinForms show can ask to raise this layered window.
            // It must be constrained before commit, without a repair next frame.
            Assert.True(SetWindowPos(mask.Handle, new(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002));
            Assert.True(IsAbove(cover.Handle, mask.Handle));
            mask.RefreshPresentation();
            Assert.True(IsAbove(cover.Handle, mask.Handle));

            mask.CompleteCoveredPresentation(IntPtr.Zero);
            Assert.Equal(owner.Handle, GetWindow(cover.Handle, 4));
            Assert.True(SetWindowPos(mask.Handle, new(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002));
            Assert.True(IsAbove(mask.Handle, cover.Handle));
            mask.SetPresentationCover(cover.Handle);
            mask.Dispose();
            Assert.True(IsWindow(cover.Handle));
            Assert.Equal(owner.Handle, GetWindow(cover.Handle, 4));
        });
    }

    private static bool IsAbove(IntPtr above, IntPtr below)
    {
        for (var window = GetWindow(above, 2); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == below) return true;
        return false;
    }

    // Native HWND tests stay opt-in. All windows are outside the virtual
    // desktop, have no activation, and neither capture nor inject input.
    [DesktopFact]
    public void CompletedSelectionRepublishesUnchangedMaskWithoutMovingTheToolbar()
    {
        WpfTestHost.Invoke(() =>
        {
            using var mask = new NativeSelectionMaskWindow();
            using var frame = new NativeSelectionFrameWindow();
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            var surface = new Rectangle(desktop.Right + 100, desktop.Bottom + 100, 320, 240);
            var selection = new Rectangle(surface.X + 20, surface.Y + 20, 160, 90);
            var toolbar = new Rectangle(surface.X + 30, surface.Y + 150, 190, 40);
            frame.AttachMaskWindow(mask, surface);
            frame.Update(selection);
            frame.SetMaskExcludedRegions([toolbar]);
            frame.SetMaskEnabled(true);

            // Model a completed owner paint: the mask has no pending damage,
            // and the cached selection/toolbox geometry is still identical.
            Assert.True(ValidateRect(mask.Handle, IntPtr.Zero));
            Assert.False(GetUpdateRect(mask.Handle, IntPtr.Zero, false));
            frame.RefreshMask();
            Assert.False(GetUpdateRect(mask.Handle, IntPtr.Zero, false));

            mask.RefreshPresentation();
            Assert.True(IsWindowVisible(mask.Handle));
            Assert.True(GetUpdateRect(mask.Handle, IntPtr.Zero, false));
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(mask.Handle, region));
                Assert.True(PtInRegion(region, 5, 5));
                Assert.False(PtInRegion(region, 50, 50));
                Assert.False(PtInRegion(region, 80, 170));
            }
            finally { _ = DeleteObject(region); }

            // Repeated idle updates still do no painting. The explicit
            // handoff must not turn into a per-frame full-screen redraw.
            Assert.True(ValidateRect(mask.Handle, IntPtr.Zero));
            for (var i = 0; i < 30; i++) frame.RefreshMask();
            Assert.False(GetUpdateRect(mask.Handle, IntPtr.Zero, false));
            mask.HideMask();
            mask.RefreshPresentation();
            Assert.False(IsWindowVisible(mask.Handle));
        });
    }

    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
