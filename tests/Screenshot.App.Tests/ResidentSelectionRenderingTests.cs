using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ResidentSelectionRenderingTests
{
    [Fact]
    public void FailedBorderDrawInvalidatesThePartialFrameAndRecoveryRepaintsEverything()
    {
        using var source = CreateSource();
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, 1);
        var visual = Visual(new(40, 40));
        renderer.Render(visual);
        var border = typeof(ResidentSelectionRenderer).GetField("_selectionBorder",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var brush = border.GetValue(renderer);
        var next = visual with { Cursor = new(90, 80), Selected = true, Compact = true };
        try
        {
            border.SetValue(renderer, IntPtr.Zero); // Simulate a failed GDI drawing resource.
            Assert.Throws<System.ComponentModel.Win32Exception>(() => renderer.Render(next));
            Assert.Equal(new Rectangle(0, 0, 800, 500), renderer.GetDamage(next));
        }
        finally { border.SetValue(renderer, brush); }
        renderer.Render(next);
        using var fresh = new ResidentSelectionRenderer(source, AppTheme.GlassInk, 1);
        fresh.Render(next);
        Assert.Equal(Pixels(fresh.Buffer), Pixels(renderer.Buffer));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ColdDragBadgeStaysAboveSelectionAndDoesNotFollowReleasePointer(double scale)
    {
        using var source = CreateSource();
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, scale);
        var visual = Visual(new(500, 370)) with { Selected = true, Compact = true,
            Region = new(40, 140, 460, 230) };
        var bounds = renderer.GetBadgeBounds(visual);
        Assert.Equal(visual.Region.X - source.Region.X, bounds.X);
        Assert.Equal(visual.Region.Y - source.Region.Y - (int)Math.Ceiling(6 * scale), bounds.Bottom);
        Assert.InRange(bounds.Width / scale, 63, 100);
        Assert.Equal(Math.Ceiling(22 * scale), bounds.Height);
        Assert.Equal(bounds, renderer.GetBadgeBounds(visual with { Cursor = new(-90, -40) }));
        renderer.Render(visual);
        var beforeRelease = Pixels(renderer.Buffer);
        renderer.Render(visual with { Cursor = new(699, 449) });
        Assert.Equal(beforeRelease, Pixels(renderer.Buffer));
    }

    [Fact]
    public void ColdBadgeFitsScreenEdgesAndGrowsWithTheDimensions()
    {
        using var source = CreateSource();
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, 1);
        var visual = Visual(new(699, 449)) with { Selected = true, Compact = true,
            Region = new(-100, -50, 92, 24) };
        var small = renderer.GetBadgeBounds(visual);
        var large = renderer.GetBadgeBounds(visual with { Region = new(-100, -50, 16384, 10000) });
        var surface = new Rectangle(0, 0, source.Region.Width, source.Region.Height);
        Assert.True(surface.Contains(small));
        Assert.True(surface.Contains(large));
        Assert.InRange(small.Width, 52, 75);
        Assert.True(large.Width > small.Width);
    }

    [Theory]
    [InlineData(AppTheme.GlassInk, 1)]
    [InlineData(AppTheme.GlassMist, 1.5)]
    [InlineData(AppTheme.AuroraMist, 2)]
    public void FirstHoverKeepsMaskAndThemeBorderWhileDragRevealsOriginal(AppTheme theme, double scale)
    {
        using var source = CreateSource();
        var original = Pixels(source);
        using var renderer = new ResidentSelectionRenderer(source, theme, scale);
        var visual = Visual(new(130, 90));
        renderer.Render(visual);
        var accent = (uint)ColorTranslator.FromHtml(AppThemeManager.GetSelectionBorderColor(theme)).ToArgb() & 0xFFFFFF;
        Assert.Equal(accent, renderer.Buffer.PixelAt(visual.Region.X + 1, visual.Region.Y + 1));
        Assert.Equal(accent, renderer.Buffer.PixelAt(visual.Region.X + 2, visual.Region.Y + 2));
        AssertDimmed(source, renderer.Buffer, 120, 220); // Hover interior stays masked.
        AssertDimmed(source, renderer.Buffer, -90, -40); // Outside also masked.
        SavePreview(renderer.Buffer, $"hover-{theme}-{scale}.png");

        renderer.Render(visual with { Selected = true, Compact = true });
        Assert.Equal(source.PixelAt(120, 220), renderer.Buffer.PixelAt(120, 220));
        AssertDimmed(source, renderer.Buffer, -90, -40);
        Assert.Equal(accent, renderer.Buffer.PixelAt(visual.Region.X + 2, visual.Region.Y + 2));
        Assert.Equal(original, Pixels(source)); // No mask, border or picker enters captured pixels.
        SavePreview(renderer.Buffer, $"drag-{theme}-{scale}.png");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    [InlineData(2.5)]
    [InlineData(3)]
    public void MovingPickerAndSelectionProducesSamePixelsAsFreshCompleteFrame(double scale)
    {
        using var source = CreateSource();
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, scale);
        var visual = Visual(new(40, 40));
        renderer.Render(visual);
        var damage = renderer.GetDamage(visual with { Cursor = new(50, 48) });
        if (scale == 1) Assert.True(damage.Width * damage.Height < source.Region.Width * source.Region.Height / 3);
        for (var step = 0; step < 32; step++)
        {
            visual = visual with { Cursor = new(-100 + step * 25, -50 + step * 15),
                Region = step < 12 ? visual.Region : new(-40 + step, 10, 230 + step * 4, 160),
                Selected = step >= 12 && step < 26, Compact = step >= 12 && step < 26 };
            renderer.Render(visual);
            using var fresh = new ResidentSelectionRenderer(source, AppTheme.GlassInk, scale);
            fresh.Render(visual);
            Assert.Equal(Pixels(fresh.Buffer), Pixels(renderer.Buffer)); // Old badge/border fully erased, no accumulation.
            Assert.Equal(Rectangle.Empty, renderer.GetDamage(visual));
        }
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void FourKDirtyPresentationRestoresPickerTrailsAndKeepsSelectionBorder(double scale)
    {
        var surface = new ScreenRegion(-3840, -240, 3840, 2160);
        using var source = new NativeDesktopFrame(surface);
        var original = new int[surface.Width * surface.Height];
        for (var i = 0; i < original.Length; i++) original[i] = 0xC0C080 + i % 127;
        Marshal.Copy(original, 0, source.Pixels, original.Length);
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, scale);
        using var presented = new NativeDesktopFrame(surface);
        var expected = new int[original.Length];
        var actual = new int[original.Length];
        var visual = new ResidentSelectionVisual(new(-3400, 50, 2200, 1400), new(-3300, 300),
            new(surface.X, surface.Y, surface.Width, surface.Height), false, false);
        var states = new[]
        {
            visual,
            visual with { Cursor = new(-3260, 321) },
            visual with { Cursor = new(-2600, 700) },
            visual with { Cursor = new(-3839, -239) },
            visual with { Cursor = new(-1, 1919) },
            visual with { Cursor = new(-900, 1600), Region = new(-3400, 50, 2500, 1550), Selected = true, Compact = true },
            visual with { Cursor = new(-1500, 1000), Region = new(-3400, 50, 1900, 950), Selected = true, Compact = true },
        };
        foreach (var state in states)
        {
            var invalidated = renderer.GetDamage(state);
            Assert.Equal(invalidated, renderer.Render(state));
            // Present only the invalidated rectangle, just as WM_PAINT does.
            Assert.True(NativeDesktopFrame.BitBlt(presented.DeviceContext,
                invalidated.X, invalidated.Y, invalidated.Width, invalidated.Height,
                renderer.Buffer.DeviceContext, invalidated.X, invalidated.Y, 0x00CC0020));
            Assert.True(GdiFlush());
            using var fresh = new ResidentSelectionRenderer(source, AppTheme.GlassInk, scale);
            fresh.Render(state);
            Marshal.Copy(fresh.Buffer.Pixels, expected, 0, expected.Length);
            Marshal.Copy(presented.Pixels, actual, 0, actual.Length);
            Assert.True(expected.AsSpan().SequenceEqual(actual),
                $"4K partial presentation differs from a fresh frame at scale {scale}: {state}.");
            if (state.Selected)
            {
                var region = new Rectangle(state.Region.X, state.Region.Y, state.Region.Width, state.Region.Height);
                var accent = (uint)ColorTranslator.FromHtml(AppThemeManager.GetSelectionBorderColor(AppTheme.GlassInk)).ToArgb() & 0xFFFFFF;
                foreach (var point in new[] { new Point(region.X + 1, region.Y + 1),
                    new Point(region.Right - 2, region.Y + 1), new Point(region.X + 1, region.Bottom - 2),
                    new Point(region.Right - 2, region.Bottom - 2) })
                    Assert.Equal(accent, presented.PixelAt(point.X, point.Y));
            }
        }
        Marshal.Copy(source.Pixels, actual, 0, actual.Length);
        Assert.True(original.AsSpan().SequenceEqual(actual));
    }

    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [Fact]
    public void MagnifierCenterMatchesSampleEvenAtDesktopEdgesAndBuffersAreReleased()
    {
        using var source = CreateSource();
        var renderer = new ResidentSelectionRenderer(source, AppTheme.GlassInk, 1);
        var buffer = renderer.Buffer;
        foreach (var cursor in new[] { new Point(-100, -50), new Point(699, 449), new Point(0, 0) })
        {
            var visual = Visual(cursor);
            renderer.Render(visual);
            var badge = renderer.GetBadgeBounds(visual);
            Assert.Equal(source.PixelAt(cursor.X, cursor.Y), buffer.PixelAt(
                source.Region.X + badge.X + 63, source.Region.Y + badge.Y + 63));
        }
        renderer.Dispose();
        renderer.Dispose();
        Assert.Equal(IntPtr.Zero, buffer.Pixels);
        Assert.Equal(IntPtr.Zero, buffer.DeviceContext);
        Assert.Throws<ObjectDisposedException>(() => renderer.Render(Visual(new(0, 0))));
        Assert.NotEqual(IntPtr.Zero, source.Pixels); // Source is borrowed, owned by capture session.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ExpandedPickerUsesHorizontalLayoutAndFitsMonitorEdges(double scale)
    {
        using var source = CreateSource();
        using var renderer = new ResidentSelectionRenderer(source, AppTheme.AuroraMist, scale);
        var visual = Visual(new(699, 449));
        var bounds = renderer.GetBadgeBounds(visual);
        Assert.True(bounds.Width > bounds.Height * 1.7);
        Assert.Equal(Math.Round(124 * scale), bounds.Height);
        Assert.True(new Rectangle(0, 0, 800, 500).Contains(bounds));
        renderer.Render(visual);
        var rgbBounds = new Rectangle(bounds.X + (int)Math.Round(126 * scale), bounds.Y + (int)Math.Round(55 * scale),
            bounds.Width - (int)Math.Round(135 * scale), (int)Math.Round(17 * scale));
        var colors = new HashSet<uint>();
        for (var y = rgbBounds.Top; y < rgbBounds.Bottom; y++)
            for (var x = rgbBounds.Left; x < rgbBounds.Right; x++)
                colors.Add(renderer.Buffer.PixelAt(source.Region.X + x, source.Region.Y + y));
        Assert.True(colors.Count > 2, "RGB text must occupy the right side of the magnifier.");
    }

    private static ResidentSelectionVisual Visual(Point cursor) => new(new(-40, 10, 400, 280),
        cursor, new(-100, -50, 800, 500), false, false);

    private static NativeDesktopFrame CreateSource()
    {
        var frame = new NativeDesktopFrame(new(-100, -50, 800, 500));
        var pixels = new int[800 * 500];
        for (var y = 0; y < 500; y++)
            for (var x = 0; x < 800; x++)
                pixels[y * 800 + x] = ((90 + x % 160) << 16) | ((90 + y % 160) << 8) | 180;
        Marshal.Copy(pixels, 0, frame.Pixels, pixels.Length);
        return frame;
    }

    private static int[] Pixels(NativeDesktopFrame frame)
    {
        var result = new int[frame.Region.Width * frame.Region.Height];
        Marshal.Copy(frame.Pixels, result, 0, result.Length);
        return result;
    }

    private static void AssertDimmed(NativeDesktopFrame source, NativeDesktopFrame rendered, int x, int y)
    {
        var before = source.PixelAt(x, y);
        var after = rendered.PixelAt(x, y);
        for (var shift = 0; shift <= 16; shift += 8)
            Assert.InRange((after >> shift) & 255, 1u, ((before >> shift) & 255) * 3 / 4);
    }

    private static void SavePreview(NativeDesktopFrame frame, string name)
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RENDER_REPORT") is not { Length: > 0 } directory) return;
        System.IO.Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(frame.Region.Width, frame.Region.Height, frame.Stride,
            PixelFormat.Format32bppRgb, frame.Pixels);
        bitmap.Save(System.IO.Path.Combine(directory, name), ImageFormat.Png);
    }
}
