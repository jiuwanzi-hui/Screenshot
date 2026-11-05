using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class PinnedToolbarOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndGroupPinUseUnselectedInlineToolsThreeEmojiRowsAndVariantIcons(bool group)
    {
        WpfTestHost.Invoke(() =>
        {
            var saved = AnnotationToolPreferences.Snapshot();
            using var image = new CapturedImage(new System.Drawing.Bitmap(120, 80));
            var pin = new PinnedImageWindow(image);
            Window owner = group ? new PinnedImageGroupWindow([pin]) : pin;
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            var window = new PinnedImageEditorToolbarWindow(owner, AppSettings.CreateDefault());
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassInk);
                window.PrepareEdit();
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources = window.Resources;
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden pinned toolbar")
                    { WindowStyle = unchecked((int)0x80000000), Width = 1500, Height = 600 });
                source.RootVisual = root;
                void Layout() { root.Measure(new Size(1500, 600)); root.Arrange(new Rect(root.DesiredSize)); root.UpdateLayout(); }
                Layout();
                var canvas = group ? ((PinnedImageGroupWindow)owner).AnnotationCanvas : pin.AnnotationCanvas;
                Assert.False(canvas.IsAnnotationCreationEnabled);
                var colors = (StackPanel)window.FindName("ColorOptions");
                var row = (ToolbarOptionsRow)window.FindName("ToolAdjustments");
                Assert.Equal(Visibility.Collapsed, colors.Visibility);
                Assert.Empty(row.Children);
                var toolbar = (Border)window.FindName("ToolbarSurface");
                var initialWidth = toolbar.ActualWidth;
                var shortcut = new ToolbarVariantShortcut();
                foreach (var (name, count) in new[] { ("ShapeToolButton", 2), ("ArrowToolButton", 4),
                    ("DoubleArrowToolButton", 2), ("LineToolButton", 2), ("MosaicToolButton", 2), ("TextToolButton", 2) })
                {
                    var button = (RadioButton)window.FindName(name);
                    button.IsChecked = true;
                    Assert.True(canvas.IsAnnotationCreationEnabled);
                    Assert.Equal(count, row.Children.Count);
                    var icons = new HashSet<string>();
                    for (var i = 0; i < count; i++)
                    {
                        icons.Add(button.Content is System.Windows.Shapes.Path path ? path.Data.ToString(CultureInfo.InvariantCulture) + path.Fill + path.Stroke : ((Grid)button.Content).Tag?.ToString() ?? "");
                        shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, false, row);
                        Assert.True(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
                    }
                    Assert.Equal(count, icons.Count);
                }
                ((RadioButton)window.FindName("EmojiToolButton")).IsChecked = true;
                Layout();
                var palette = (ToolbarEmojiStrip)window.FindName("EmojiPalette");
                var line = (ToolbarAdjustmentPanel)window.FindName("AdjustmentLine");
                Assert.Equal(initialWidth, toolbar.ActualWidth);
                Assert.Equal(102, palette.ViewportHeight);
                Assert.Equal(0, palette.ScrollableWidth);
                Assert.True(palette.ScrollableHeight > 0);
                Assert.True(colors.TranslatePoint(new Point(0, 0), line).X >= palette.ActualWidth);
                palette.ScrollToBottom(); Layout();
                var emojis = (WrapPanel)window.FindName("EmojiPalettePanel");
                var last = (Button)emojis.Children[^1];
                Assert.True(last.TranslatePoint(new Point(0, last.ActualHeight), palette).Y <= palette.ViewportHeight);
                last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(last.Tag, ((EmojiStickerImage)window.FindName("EmojiToolIcon")).Sticker);
                var translation = (Button)window.FindName("TranslateActionButton");
                Assert.True(TranslationLanguageMenu.GetInlineOptions(translation));
                window.SetTranslationActionState(true, "显示原文", "翻译完成");
                Assert.Contains("右键", Assert.IsType<string>(translation.ToolTip));
                Assert.Contains("显示原文", Assert.IsType<string>(translation.ToolTip));
                Assert.DoesNotContain("F4", Assert.IsType<string>(translation.ToolTip));
                toolbar.Resources = window.Resources;
                palette.ScrollToTop(); Layout();
                source.RootVisual = null;
                ToolbarOptionsRowTests.RenderToolbar(toolbar, group ? "group-pin-emoji" : "single-pin-emoji");
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); owner.Close(); if (group) pin.Close(); AnnotationToolPreferences.Configure(saved, null); }
        });
    }

    [Fact]
    public void ClosingToolbarDetachesOwnerShiftHandlers()
    {
        WpfTestHost.Invoke(() =>
        {
            var host = new Border();
            var row = new ToolbarOptionsRow();
            var menu = new ContextMenu();
            var clicks = 0;
            for (var i = 0; i < 2; i++)
            {
                var item = new MenuItem { Header = "choice", IsCheckable = true, IsChecked = i == 0 };
                item.Click += (_, _) => clicks++;
                menu.Items.Add(item);
            }
            row.Show(new RadioButton { ContextMenu = menu }, () => null);
            var detach = ToolbarVariantShortcut.Attach(host, () => row);
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden detached keys")
                { WindowStyle = unchecked((int)0x80000000), Width = 10, Height = 10 });
            source.RootVisual = host;
            void Tap()
            {
                host.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.LeftShift) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                host.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.LeftShift) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            }
            Tap(); Assert.Equal(1, clicks);
            detach(); Tap(); Assert.Equal(1, clicks);
        });
    }
}
