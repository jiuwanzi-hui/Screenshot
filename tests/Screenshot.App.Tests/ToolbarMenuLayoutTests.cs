using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Pin;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Screenshot.App.Tests;

public sealed class ToolbarMenuLayoutTests
{
    [Fact]
    public void PreparingDuringSelectionKeepsNoVisibleWindowOrCaptureAndPreservesPreferences()
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            var windows = System.Windows.Application.Current.Windows.Cast<Window>().ToArray();
            var visible = System.Windows.Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible).ToArray();
            using var pins = new PinnedImageManager();
            using var http = new System.Net.Http.HttpClient();
            var settings = Core.AppSettings.CreateDefault();
            var coordinator = new RegionCaptureCoordinator(() => settings, new CaptureHistoryService(), pins,
                new Text.DpapiTranslationCredentialStore(), http, _ => { });
            coordinator.PrepareResidentCaptureToolbar();
            coordinator.PrepareResidentCaptureToolbar();
            Assert.Equal(visible, System.Windows.Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible));
            Assert.Equal(preferences, AnnotationToolPreferences.Snapshot());
            Assert.Null(typeof(CaptureOverlayWindow).GetField("_activeInteractiveOverlay",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
            coordinator.DiscardPreparedCaptureToolbar();
            Assert.Equal(windows, System.Windows.Application.Current.Windows.Cast<Window>());
        });
    }

    private static readonly string[] DropDownNames = ["InlineShapeToolButton", "InlineArrowToolButton", "InlineDoubleArrowToolButton",
        "InlineLineToolButton", "InlineMosaicToolButton", "InlineTextToolButton", "InlineNumberToolButton"];
    private static readonly double[] HitTestOffsets = [2, 14, 26];

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void CaptureAnnotationButtonsUseTheirWholeSurfaceWithoutDropdownStrips(double scale)
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                foreach (var name in DropDownNames)
                {
                    var button = (System.Windows.Controls.Primitives.ButtonBase)overlay.FindName(name);
                    button.ApplyTemplate();
                    Assert.Empty(Descendants(button).OfType<ToolbarDropDownButton>());
                    Assert.NotNull(button.ToolTip);
                    CheckButtonSurface(button, scale);
                }
                var translate = (System.Windows.Controls.Button)overlay.FindName("TranslateButton");
                translate.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Empty(Descendants(translate).OfType<ToolbarDropDownButton>());
                CheckButtonSurface(translate, scale);
                CheckButtonSurface((System.Windows.Controls.Button)overlay.FindName("AspectRatioButton"), scale);
            }
            finally { overlay.Close(); }
        });
    }

    private static void CheckButtonSurface(System.Windows.Controls.Primitives.ButtonBase button, double scale)
    {
        var owner = Window.GetWindow(button);
        ((Panel)button.Parent).Children.Remove(button);
        button.Visibility = Visibility.Visible;
        button.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        var host = new Border { Child = button, Resources = owner!.Resources };
        host.Measure(new Size(150, 150));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        foreach (var x in new[] { 2d, button.ActualWidth / 2, button.ActualWidth - 2 })
        {
            var point = button.TranslatePoint(new Point(x, button.ActualHeight / 2), host);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(host, point)?.VisualHit;
            Assert.True(hit is not null && (ReferenceEquals(hit, button) || button.IsAncestorOf(hit)),
                $"{button.Name}: x={x}, scale={scale}");
        }
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1.5)]
    public void PinUsesInlineChoicesWhileImageEditorRetainsItsDropdownHitSurface(double scale)
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(32, 32));
            var pin = new PinnedImageWindow(image);
            _ = new System.Windows.Interop.WindowInteropHelper(pin).EnsureHandle();
            var toolbar = new PinnedImageEditorToolbarWindow(pin);
            var editor = new ImageEditorWindow(image.Clone(), System.IO.Path.GetTempPath());
            try
            {
                foreach (var window in new Window[] { toolbar, editor })
                {
                    foreach (var name in DropDownNames)
                    {
                        var tool = (System.Windows.Controls.Primitives.ButtonBase)window.FindName(name[6..]);
                        if (window == toolbar) { Assert.Empty(Descendants(tool).OfType<ToolbarDropDownButton>()); CheckButtonSurface(tool, scale); }
                        else CheckArrowStrip(tool, scale);
                    }
                    var translate = (System.Windows.Controls.Button)window.FindName(window == toolbar ? "TranslateActionButton" : "TranslateTextButton");
                    translate.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    if (window == toolbar) { Assert.Empty(Descendants(translate).OfType<ToolbarDropDownButton>()); CheckButtonSurface(translate, scale); }
                    else CheckArrowStrip(translate, scale);
                }
            }
            finally { editor.Close(); toolbar.Close(); pin.Close(); }
        });
    }

    private static void CheckArrowStrip(System.Windows.Controls.Primitives.ButtonBase button, double scale)
    {
        ((Panel)button.Parent).Children.Remove(button);
        button.Visibility = Visibility.Visible;
        AppThemeManager.ApplySettingsPalette(button.Resources, Core.AppTheme.NeonDeep);
        button.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        // Reparenting under a visible off-screen root resumes layout after
        // removal from the initially collapsed capture toolbar.
        var host = new Border { Child = button };
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Offscreen toolbar input")
        { WindowStyle = unchecked((int)0x80000000), Width = 150, Height = 150 });
        source.RootVisual = host;
        host.Measure(new Size(150, 150));
        host.Arrange(new Rect(0, 0, 150, 150));
        button.UpdateLayout();
        var arrow = Assert.Single(Descendants(button).OfType<ToolbarDropDownButton>());
        Assert.Equal(System.Windows.Controls.ClickMode.Release, arrow.ClickMode);
        Assert.False(arrow.Focusable);
        Assert.True(arrow.ActualWidth >= 14 && arrow.ActualHeight >= 28, button.Name);
        foreach (var y in HitTestOffsets)
        {
            var point = arrow.TranslatePoint(new Point(arrow.ActualWidth / 2, y), host);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(host, point)?.VisualHit;
            Assert.True(hit is not null && (ReferenceEquals(hit, arrow) || arrow.IsAncestorOf(hit)), $"{button.Name}: arrow y={y}, scale={scale}");
            var input = Assert.IsAssignableFrom<DependencyObject>(host.InputHitTest(point));
            Assert.True(ReferenceEquals(input, arrow) || input is Border && arrow.IsAncestorOf(input),
                $"{button.Name}: glyph must share the button surface, got {input.GetType().Name}");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(value, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public void CaptureDropDownMenusLayOutTheirItemsOffscreen()
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            AppThemeManager.ApplySettingsPalette(overlay.Resources, Core.AppTheme.NeonDeep);
            try
            {
                foreach (var name in DropDownNames.Where(name => name != "InlineNumberToolButton"))
                {
                    var button = (System.Windows.Controls.Primitives.ToggleButton)overlay.FindName(name);
                    var menu = button.ContextMenu;
                    // Render the actual template without opening a top-level menu or taking input.
                    menu.PlacementTarget = button;
                    menu.ApplyTemplate();
                    menu.Measure(new Size(600, 600));
                    menu.Arrange(new Rect(menu.DesiredSize));
                    menu.UpdateLayout();
                    Assert.True(menu.DesiredSize.Width > 30, name + " has no menu width");
                    Assert.True(menu.DesiredSize.Height > 20, name + " has no menu height");
                    Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.True(item.ActualHeight > 0, name + " has invisible items"));
                }
            }
            finally { overlay.Close(); }
        });
    }
}
