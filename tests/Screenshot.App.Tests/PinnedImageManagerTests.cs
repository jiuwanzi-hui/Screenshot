using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class PinnedImageManagerTests
{
    [Fact]
    public void ShowsAndReleasesPinnedImages()
    {
        var countAfterPinning = 0;
        var countAfterDisposal = -1;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            CapturedImage? image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            using var manager = new PinnedImageManager();

            try
            {
                manager.Pin(image);
                image = null;
                countAfterPinning = manager.Count;
            }
            finally
            {
                image?.Dispose();
                manager.Dispose();
                countAfterDisposal = manager.Count;
            }
        });

        Assert.Equal(1, countAfterPinning);
        Assert.Equal(0, countAfterDisposal);
    }

    [Fact]
    public void GroupingCombinesPinsIntoOneWindowAndUngroupRestoresThem()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(160, 100)));
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(120, 140)));
            var members = manager.Windows;
            var firstPreview = members[0].Preview;
            var secondPreview = members[1].Preview;

            members[0].SetGroupedState(true);
            Assert.Null(manager.GroupWindow);
            members[1].SetGroupedState(true);

            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);
            Assert.True(group.IsVisible);
            Assert.All(members, member => Assert.False(member.IsVisible));
            Assert.Equal(2, group.Members.Count);
            var canvas = Assert.IsType<System.Windows.Controls.Grid>(
                group.FindName("GroupCanvas"));
            Assert.True(canvas.Children.Count >= 3);
            Assert.IsType<System.Windows.Controls.Image>(
                group.FindName("CompositeImage"));
            Assert.IsType<Screenshot.App.Editor.ImageEditorCanvas>(
                group.FindName("InlineEditorCanvas"));
            Assert.IsType<System.Windows.Controls.Canvas>(
                group.FindName("CropOverlay"));
            Assert.Null(group.EditorToolbar);
            Assert.Equal(235, group.CompositePreview.PixelWidth);
            Assert.Equal(112, group.CompositePreview.PixelHeight);
            var opacitySlider = Assert.IsType<System.Windows.Controls.Slider>(
                group.FindName("GroupOpacitySlider"));
            Assert.NotNull(opacitySlider.Template);

            manager.UngroupAll();

            Assert.Null(manager.GroupWindow);
            Assert.All(members, member =>
            {
                Assert.False(member.IsGrouped);
                Assert.True(member.IsVisible);
            });
            Assert.Same(firstPreview, members[0].Preview);
            Assert.Same(secondPreview, members[1].Preview);
        });
    }

    [Fact]
    public void GroupingKeepsTheOrderPinsWereAddedInsteadOfSortingByWindowPosition()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                80,
                40,
                System.Drawing.Color.Red)));
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                60,
                40,
                System.Drawing.Color.Blue)));
            var members = manager.Windows;
            var first = members.Single(member =>
                ReadPixel(member.Preview, 1, 1).Red >
                ReadPixel(member.Preview, 1, 1).Blue);
            var second = members.Single(member => !ReferenceEquals(member, first));

            first.Left = 900;
            second.Left = 100;
            first.SetGroupedState(true);
            second.SetGroupedState(true);

            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);
            Assert.Same(first, group.Members[0]);
            Assert.Same(second, group.Members[1]);
            var leftPixel = ReadPixel(group.CompositePreview, 10, 10);
            Assert.True(leftPixel.Red > leftPixel.Blue);
        });
    }

    [Fact]
    public void GroupLevelEditIsSplitBackIntoMembersWhenUngrouped()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                80,
                50,
                System.Drawing.Color.Red)));
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                60,
                70,
                System.Drawing.Color.Blue)));
            var members = manager.Windows;
            members[0].SetGroupedState(true);
            members[1].SetGroupedState(true);
            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);

            using var edited = new CapturedImage(CreateSolidBitmap(
                161,
                70,
                System.Drawing.Color.Green));
            group.SetCompositePreview(edited.Preview);

            Assert.Equal(161, group.CompositePreview.PixelWidth);
            Assert.Equal(70, group.CompositePreview.PixelHeight);
            manager.UngroupAll();
            var firstPixel = ReadPixel(members[0].Preview, 10, 10);
            var secondPixel = ReadPixel(members[1].Preview, 10, 10);
            Assert.True(firstPixel.Green > firstPixel.Red);
            Assert.True(secondPixel.Green > secondPixel.Blue);
        });
    }

    [Fact]
    public void GroupEditorZoomsWithTheMouseWheel()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(160, 100)));
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(120, 140)));
            var members = manager.Windows;
            members[0].SetGroupedState(true);
            members[1].SetGroupedState(true);
            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);
            group.UpdateLayout();
            Assert.IsType<System.Windows.Controls.Button>(group.FindName("GroupEditButton"))
                .RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Button.ClickEvent));
            var editor = Assert.IsType<Screenshot.App.Editor.ImageEditorCanvas>(
                group.FindName("InlineEditorCanvas"));
            var canvas = Assert.IsType<System.Windows.Controls.Grid>(
                group.FindName("GroupCanvas"));
            var wheel = new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                120)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent,
            };

            canvas.RaiseEvent(wheel);

            Assert.True(wheel.Handled);
            Assert.True(editor.Zoom > 1);
        });
    }

    [Fact]
    public void GroupCropIsPreservedAfterUngrouping()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                40,
                20,
                System.Drawing.Color.Red)));
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                20,
                30,
                System.Drawing.Color.Blue)));
            var members = manager.Windows;
            members[0].SetGroupedState(true);
            members[1].SetGroupedState(true);
            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);

            group.ApplyCompositeCrop(new System.Windows.Int32Rect(0, 10, 40, 10));
            manager.UngroupAll();

            Assert.Equal(40, members[0].Preview.PixelWidth);
            Assert.Equal(10, members[0].Preview.PixelHeight);
        });
    }

    [Fact]
    public void GroupEditAndCropUseTheSameAttachedToolbarOutsideTheGroupWindow()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                80,
                50,
                System.Drawing.Color.Red)));
            manager.Pin(new CapturedImage(CreateSolidBitmap(
                60,
                70,
                System.Drawing.Color.Blue)));
            var members = manager.Windows;
            members[0].SetGroupedState(true);
            members[1].SetGroupedState(true);
            var group = Assert.IsType<PinnedImageGroupWindow>(manager.GroupWindow);
            group.UpdateLayout();
            var windowCount = System.Windows.Application.Current.Windows.Count;
            var editButton = Assert.IsType<System.Windows.Controls.Button>(
                group.FindName("GroupEditButton"));
            var cropButton = Assert.IsType<System.Windows.Controls.Button>(
                group.FindName("GroupCropButton"));

            editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));
            Assert.True(group.IsInlineEditorVisible);
            Assert.False(group.IsInlineCropVisible);
            var editToolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                group.EditorToolbar);
            Assert.Same(group, editToolbar.Owner);
            Assert.Equal(
                windowCount + 1,
                System.Windows.Application.Current.Windows.Count);
            Assert.DoesNotContain(
                System.Windows.Application.Current.Windows
                    .OfType<Screenshot.App.Editor.ImageEditorWindow>(),
                window => window.Owner == group);
            Assert.DoesNotContain(
                System.Windows.Application.Current.Windows
                    .OfType<Screenshot.App.Editor.ImageCropWindow>(),
                window => window.Owner == group);

            group.MoveInlineToolbar(24, 18);
            Assert.True(group.ToolbarHasCustomPosition);
            group.ResetInlineToolbarPosition();
            Assert.False(group.ToolbarHasCustomPosition);

            cropButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));
            Assert.False(group.IsInlineEditorVisible);
            Assert.True(group.IsInlineCropVisible);
            var cropToolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                group.EditorToolbar);
            Assert.NotSame(editToolbar, cropToolbar);
            Assert.Equal(
                windowCount + 1,
                System.Windows.Application.Current.Windows.Count);
        });
    }

    [Fact]
    public void AttachedToolbarCanOverlapTransparentMarginsForATwoPixelVisualGap()
    {
        var position = PinnedImageEditorToolbarWindow.CalculateAttachedPosition(
            new System.Drawing.Rectangle(400, 300, 320, 180),
            new System.Drawing.Size(600, 84),
            new System.Drawing.Rectangle(0, 0, 1920, 1080),
            -16);

        Assert.Equal(464, position.Y);
        Assert.Equal(260, position.X);
        Assert.Equal(2, (position.Y + 6) - (480 - 12));
        Assert.True(position.X >= 0);
        Assert.True(position.X + 600 <= 1920);
    }

    [Fact]
    public void PinnedToolbarDragTracksThePointerInScreenPixels()
    {
        var position = PinnedImageEditorToolbarWindow.CalculateDraggedPosition(
            new System.Drawing.Rectangle(400, 300, 600, 84),
            new System.Drawing.Point(700, 500),
            new System.Drawing.Point(745, 468));

        Assert.Equal(new System.Drawing.Point(445, 268), position);
    }

    [Fact]
    public void PinnedToolbarUsesCaptureColorsFeaturesAndSeparatorVisibility()
    {
        WpfTestHost.Invoke(() =>
        {
            string? savedColor = null;
            int[]? savedPalette = null;
            var settings = AppSettings.CreateDefault() with
            {
                CustomStrokeColor = "#F2C94C",
                CustomColorPalette = [0xF2C94C, 0x2F80ED],
                DefaultStrokeWidth = 7,
                ToolbarScalePercent = 125,
                VisibleCaptureToolbarFeatures =
                [
                    CaptureToolbarFeature.Shape,
                    CaptureToolbarFeature.Save,
                ],
            };
            using var manager = new PinnedImageManager(
                settingsProvider: () => settings,
                customStrokeColorChanged: color => savedColor = color,
                customColorPaletteChanged: colors => savedPalette = colors);
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(100, 80)));
            var pin = Assert.Single(manager.Windows);
            var editButton = Assert.IsType<System.Windows.Controls.Button>(
                pin.FindName("EditButton"));

            editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));

            var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                pin.EditorToolbar);
            var toolbarSurface = Assert.IsType<System.Windows.Controls.Border>(
                toolbar.FindName("ToolbarSurface"));
            Assert.Null(toolbar.FindName("DragHandle"));
            Assert.Equal(
                System.Windows.Input.Cursors.SizeAll,
                toolbarSurface.Cursor);
            Assert.Null(toolbarSurface.ToolTip);
            var toolbarScale = Assert.IsType<System.Windows.Media.ScaleTransform>(
                toolbarSurface.LayoutTransform);
            Assert.Equal(1.25, toolbarScale.ScaleX);
            Assert.Equal(1.25, toolbarScale.ScaleY);
            Assert.Equal(
                System.Windows.Media.Color.FromRgb(0xF2, 0xC9, 0x4C),
                toolbar.SelectedColor);
            Assert.Equal([0xF2C94C, 0x2F80ED], toolbar.CustomColorPalette);
            Assert.Equal(
                7,
                Assert.IsType<System.Windows.Controls.Slider>(
                    toolbar.FindName("StrokeWidthSlider")).Value);
            Assert.Equal(
                System.Windows.Visibility.Visible,
                Assert.IsType<System.Windows.Controls.Button>(
                    toolbar.FindName("SaveButton")).Visibility);
            Assert.Equal(
                System.Windows.Visibility.Collapsed,
                Assert.IsType<System.Windows.Controls.Button>(
                    toolbar.FindName("OcrButton")).Visibility);
            Assert.Equal(
                System.Windows.Visibility.Collapsed,
                Assert.IsType<System.Windows.Controls.Button>(
                    toolbar.FindName("UndoButton")).Visibility);
            Assert.Equal(
                System.Windows.Visibility.Visible,
                Assert.IsType<System.Windows.Controls.Border>(
                    toolbar.FindName("ToolActionSeparator")).Visibility);
            Assert.Equal(
                System.Windows.Visibility.Collapsed,
                Assert.IsType<System.Windows.Controls.Border>(
                    toolbar.FindName("ActionHistorySeparator")).Visibility);

            var customColorButton = Assert.IsType<System.Windows.Controls.Button>(
                toolbar.FindName("CustomColorButton"));
            var paletteGlyph = Assert.IsType<System.Windows.Controls.TextBlock>(
                customColorButton.Content);
            Assert.Equal("\uE790", paletteGlyph.Text);
            customColorButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));
            var picker = Assert.Single(
                System.Windows.Application.Current.Windows
                    .OfType<Screenshot.App.Editor.ThemeColorPickerWindow>());
            Assert.True(picker.IsVisible);
            Assert.True(picker.Topmost);
            var sharedPicker = Assert.IsType<
                Screenshot.App.Editor.SharedColorPickerControl>(
                    picker.FindName("ColorPicker"));
            Assert.IsType<System.Windows.Controls.Slider>(
                sharedPicker.FindName("HueSlider")).Value = 120;
            Assert.IsType<System.Windows.Controls.Slider>(
                sharedPicker.FindName("SaturationSlider")).Value = 100;
            Assert.IsType<System.Windows.Controls.Slider>(
                sharedPicker.FindName("ValueSlider")).Value = 100;
            var alphaSlider = Assert.IsType<System.Windows.Controls.Slider>(
                sharedPicker.FindName("AlphaSlider"));
            alphaSlider.Value = 100;
            alphaSlider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
            });
            Assert.True(picker.IsVisible);
            var recentColors = Assert.IsType<
                System.Windows.Controls.Primitives.UniformGrid>(
                sharedPicker.FindName("RecentColorsPanel"));
            Assert.True(sharedPicker.TryHandlePaletteRightClick(
                recentColors.Children[0]));
            Assert.Equal("#00FF00", savedColor);
            Assert.NotNull(savedPalette);
            Assert.Contains(0x00FF00, savedPalette!);
            Assert.Equal(
                System.Windows.Media.Color.FromRgb(0, 255, 0),
                toolbar.SelectedColor);
            toolbar.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.PreviewMouseDownEvent,
            });
            Assert.False(picker.IsVisible);

            var historySettings = settings with
            {
                VisibleCaptureToolbarFeatures =
                [
                    CaptureToolbarFeature.Shape,
                    CaptureToolbarFeature.Arrow,
                    CaptureToolbarFeature.Save,
                    CaptureToolbarFeature.UndoRedo,
                ],
                CaptureToolbarFeatureOrder =
                [
                    CaptureToolbarFeature.Arrow,
                    CaptureToolbarFeature.Shape,
                    CaptureToolbarFeature.Save,
                    CaptureToolbarFeature.UndoRedo,
                ],
                CaptureToolbarRows = CaptureToolbarRowCount.Two,
            };
            var historyToolbar = new PinnedImageEditorToolbarWindow(
                pin,
                historySettings);
            try
            {
                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    Assert.IsType<System.Windows.Controls.Border>(
                        historyToolbar.FindName("ToolActionSeparator")).Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    Assert.IsType<System.Windows.Controls.Border>(
                        historyToolbar.FindName("ActionHistorySeparator")).Visibility);
                var firstRow = Assert.IsType<System.Windows.Controls.StackPanel>(
                    historyToolbar.FindName("EditToolsRow1"));
                var secondRow = Assert.IsType<System.Windows.Controls.StackPanel>(
                    historyToolbar.FindName("EditToolsRow2"));
                Assert.Equal(System.Windows.Visibility.Visible, secondRow.Visibility);
                var arrangedElements = firstRow.Children
                    .Cast<System.Windows.FrameworkElement>()
                    .Concat(secondRow.Children.Cast<System.Windows.FrameworkElement>())
                    .ToList();
                Assert.True(
                    arrangedElements.IndexOf(Assert.IsType<System.Windows.Controls.RadioButton>(
                        historyToolbar.FindName("ArrowToolButton"))) <
                    arrangedElements.IndexOf(Assert.IsType<System.Windows.Controls.RadioButton>(
                        historyToolbar.FindName("ShapeToolButton"))));
            }
            finally
            {
                historyToolbar.Close();
            }
        });
    }

    [Fact]
    public void PinnedToolbarExpandsEmojiPaletteAndRaisesSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(100, 80)));
            var pin = Assert.Single(manager.Windows);
            var editButton = Assert.IsType<System.Windows.Controls.Button>(
                pin.FindName("EditButton"));
            editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));

            var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                pin.EditorToolbar);
            var emojiButton = Assert.IsType<System.Windows.Controls.RadioButton>(
                toolbar.FindName("EmojiToolButton"));
            var palette = Assert.IsType<System.Windows.Controls.ScrollViewer>(
                toolbar.FindName("EmojiPalette"));
            var palettePanel = Assert.IsType<System.Windows.Controls.WrapPanel>(
                toolbar.FindName("EmojiPalettePanel"));
            var colorOptions = Assert.IsType<System.Windows.Controls.StackPanel>(
                toolbar.FindName("ColorOptions"));
            var strokeSlider = Assert.IsType<System.Windows.Controls.Slider>(
                toolbar.FindName("StrokeWidthSlider"));
            var redColorButton = Assert.IsType<System.Windows.Controls.Button>(
                toolbar.FindName("RedColorButton"));
            string? selectedEmoji = null;
            toolbar.EmojiSelected += emoji => selectedEmoji = emoji;

            emojiButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Primitives.ToggleButton.CheckedEvent));

            Assert.Equal(System.Windows.Visibility.Visible, palette.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, colorOptions.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, strokeSlider.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, redColorButton.Visibility);
            Assert.Equal(Editor.EmojiStickerCatalog.All.Count, palettePanel.Children.Count);

            var firstEmoji = Assert.IsType<System.Windows.Controls.Button>(
                palettePanel.Children[0]);
            firstEmoji.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.Controls.Button.ClickEvent));

            Assert.Equal(Editor.EmojiStickerCatalog.All[0], selectedEmoji);
        });
    }

    [Fact]
    public void HideAndShowStateTracksWhetherPinsAreHidden()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            var stateChanges = 0;
            manager.DisplayStateChanged += (_, _) => stateChanges++;
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(20, 20)));
            Assert.False(manager.HasHiddenWindows);

            manager.HideAll();
            Assert.True(manager.HasHiddenWindows);

            manager.ShowAll();
            Assert.False(manager.HasHiddenWindows);
            Assert.True(stateChanges >= 3);
        });
    }

    [Fact]
    public void NewPinRemainsReachableWhenExistingPinsAreMinimized()
    {
        WpfTestHost.Invoke(() =>
        {
            using var manager = new PinnedImageManager();
            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(40, 30)));
            manager.HideAll();

            manager.Pin(new CapturedImage(new System.Drawing.Bitmap(30, 20)));

            Assert.Equal(2, manager.Count);
            Assert.True(manager.Windows[0].IsMinimized);
            Assert.True(manager.Windows[0].IsVisible);
            Assert.True(manager.Windows[1].IsVisible);
            Assert.False(manager.Windows[1].IsMinimized);
        });
    }


    [Fact]
    public void GroupCopyCompositionContainsEveryMemberImage()
    {
        WpfTestHost.Invoke(() =>
        {
            using var first = new CapturedImage(CreateSolidBitmap(
                40,
                20,
                System.Drawing.Color.Red));
            using var second = new CapturedImage(CreateSolidBitmap(
                20,
                30,
                System.Drawing.Color.Blue));

            var combined = PinnedImageGroupWindow.ComposeImages(
                [first.Preview, second.Preview]);

            Assert.Equal(61, combined.PixelWidth);
            Assert.Equal(30, combined.PixelHeight);
            var leftPixel = ReadPixel(combined, 10, 15);
            var rightPixel = ReadPixel(combined, 60, 15);
            Assert.True(leftPixel.Red > leftPixel.Blue);
            Assert.True(rightPixel.Blue > rightPixel.Red);
        });
    }

    [Fact]
    public void GroupCompositionKeepsEachPinDisplayScaleAndUsesTwoColumnWaterfallLayout()
    {
        WpfTestHost.Invoke(() =>
        {
            using var wide = new CapturedImage(CreateSolidBitmap(
                100,
                40,
                System.Drawing.Color.Red));
            using var tall = new CapturedImage(CreateSolidBitmap(
                20,
                80,
                System.Drawing.Color.Blue));
            using var shortImage = new CapturedImage(CreateSolidBitmap(
                30,
                20,
                System.Drawing.Color.Green));

            var combined = PinnedImageGroupWindow.ComposeImages(
                [wide.Preview, tall.Preview, shortImage.Preview]);

            Assert.Equal(121, combined.PixelWidth);
            Assert.Equal(80, combined.PixelHeight);
            var lowerLeftPixel = ReadPixel(combined, 10, 50);
            var rightPixel = ReadPixel(combined, 110, 50);
            Assert.True(lowerLeftPixel.Green > lowerLeftPixel.Red);
            Assert.True(rightPixel.Blue > rightPixel.Red);
        });
    }

    [Fact]
    public void GroupCompositionBakesTheCheckerboardIntoItsEmptyAreas()
    {
        WpfTestHost.Invoke(() =>
        {
            using var first = new CapturedImage(CreateSolidBitmap(
                30,
                20,
                System.Drawing.Color.Red));
            using var second = new CapturedImage(CreateSolidBitmap(
                30,
                60,
                System.Drawing.Color.Blue));
            using var third = new CapturedImage(CreateSolidBitmap(
                20,
                20,
                System.Drawing.Color.Green));

            var combined = PinnedImageGroupWindow.ComposeImages(
                [first.Preview, second.Preview, third.Preview]);

            var emptyAreaPixel = ReadPixel(combined, 2, 50);
            Assert.Equal(emptyAreaPixel.Red, emptyAreaPixel.Green);
            Assert.Equal(emptyAreaPixel.Green, emptyAreaPixel.Blue);
            Assert.NotEqual(0, emptyAreaPixel.Red);
        });
    }

    private static System.Drawing.Bitmap CreateSolidBitmap(
        int width,
        int height,
        System.Drawing.Color color)
    {
        var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static (byte Blue, byte Green, byte Red, byte Alpha) ReadPixel(
        System.Windows.Media.Imaging.BitmapSource source,
        int x,
        int y)
    {
        var pixels = new byte[4];
        source.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return (pixels[0], pixels[1], pixels[2], pixels[3]);
    }
}
