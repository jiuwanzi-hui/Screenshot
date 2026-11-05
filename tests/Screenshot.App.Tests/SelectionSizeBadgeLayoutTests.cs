using System.Windows;
using Screenshot.App.Capture;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Screenshot.App.Tests;

public sealed class SelectionSizeBadgeLayoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(2.5)]
    public void BadgeAvoidsToolbarAtAllSelectionEdgesAndDisplayScales(double scale)
    {
        foreach (var origin in new[] { new Point(0, 0), new Point(-1600, -900), new Point(1920, -400) })
        foreach (var dimensions in new[] { new Size(92, 24), new Size(800, 500), new Size(1600, 900) })
        foreach (var location in new[] { new Point(0, 0), new Point(1, 0), new Point(0, 1), new Point(1, 1), new Point(.5, .5) })
        {
            var screen = new Rect(origin.X * scale, origin.Y * scale, 1600 * scale, 900 * scale);
            var selection = new Rect(screen.X + (1600 - dimensions.Width) * location.X * scale,
                screen.Y + (900 - dimensions.Height) * location.Y * scale,
                dimensions.Width * scale, dimensions.Height * scale);
            var toolbarWidth = 850 * scale;
            var toolbarHeight = 90 * scale;
            var toolbarX = Math.Clamp(selection.X + (selection.Width - toolbarWidth) / 2,
                screen.Left, screen.Right - toolbarWidth);
            var toolbarY = selection.Bottom + 10 * scale;
            if (toolbarY + toolbarHeight > screen.Bottom)
                toolbarY = Math.Max(screen.Top, selection.Top - toolbarHeight - 10 * scale);
            var toolbar = new Rect(toolbarX, toolbarY, toolbarWidth, toolbarHeight);
            var badge = SelectionSizeBadgeLayout.Place(selection, new Size(72 * scale, 22 * scale), screen,
                [toolbar], 6 * scale);
            AssertAvailable(badge, screen, toolbar);
        }
    }

    [Fact]
    public void ExpandedOptionsAndColorPanelAreBothAvoided()
    {
        var screen = new Rect(0, 0, 1024, 768);
        var selection = new Rect(380, 3, 92, 24);
        var toolbar = new Rect(40, 37, 920, 180);
        var colorPanel = new Rect(350, 0, 290, 300);
        var badge = SelectionSizeBadgeLayout.Place(selection, new Size(58, 22), screen, [toolbar, colorPanel]);
        AssertAvailable(badge, screen, toolbar, colorPanel);
    }

    [Fact]
    public void BadgeRepositionsAsCustomToolbarMovesAcrossIt()
    {
        var screen = new Rect(0, 0, 1280, 720);
        var selection = new Rect(200, 100, 380, 280);
        var initial = SelectionSizeBadgeLayout.Place(selection, new Size(72, 22), screen, []);
        Assert.Equal(72, initial.Top);
        for (var y = 0; y <= 620; y += 20)
        {
            var toolbar = new Rect(40, y, 1000, 100);
            var badge = SelectionSizeBadgeLayout.Place(selection, new Size(72, 22), screen, [toolbar]);
            AssertAvailable(badge, screen, toolbar);
        }
    }

    [Fact]
    public void SelectionOnAnUpperMonitorUsesThatMonitorsTopEdge()
    {
        var screen = new Rect(-1600, -900, 1600, 900);
        var selection = new Rect(-1400, -800, 380, 280);
        var badge = SelectionSizeBadgeLayout.Place(selection, new Size(72, 22), screen, []);
        Assert.Equal(new Rect(-1400, -828, 72, 22), badge);
    }

    [Fact]
    public void VerySmallDisplayUsesRemainingStripWithoutCoveringToolbar()
    {
        var screen = new Rect(0, 0, 320, 240);
        var toolbar = new Rect(0, 30, 320, 210);
        var badge = SelectionSizeBadgeLayout.Place(screen, new Size(72, 22), screen, [toolbar]);
        AssertAvailable(badge, screen, toolbar);
    }

    [Fact]
    public void NoFreeSpaceHidesBadgeInsteadOfCoveringActions()
    {
        var screen = new Rect(0, 0, 320, 240);
        Assert.True(SelectionSizeBadgeLayout.Place(screen, new Size(72, 22), screen, [screen]).IsEmpty);
    }

    private static void AssertAvailable(Rect badge, Rect screen, params Rect[] obstacles)
    {
        Assert.False(badge.IsEmpty);
        Assert.True(screen.Contains(badge), $"Badge outside monitor: {badge} / {screen}");
        foreach (var obstacle in obstacles)
        {
            var overlap = Rect.Intersect(badge, obstacle);
            Assert.True(overlap.IsEmpty || overlap.Width == 0 || overlap.Height == 0,
                $"Badge overlaps controls: {badge} / {obstacle}");
        }
    }
}
