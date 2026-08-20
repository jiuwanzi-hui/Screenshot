using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Pin;
using Screenshot.App.Text;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using System.Windows.Controls;
using System.Windows.Media;

namespace Screenshot.App.Tests;

public sealed class PinnedImageWindowTests
{
    private static readonly string[] WindowResizeHandleNames =
    [
        "WindowResizeLeftThumb",
        "WindowResizeRightThumb",
        "WindowResizeTopThumb",
        "WindowResizeBottomThumb",
        "WindowResizeTopLeftThumb",
        "WindowResizeTopRightThumb",
        "WindowResizeBottomLeftThumb",
        "WindowResizeBottomRightThumb",
    ];

    [Fact]
    public void PinChromeChangesWithTheSelectedApplicationTheme()
    {
        WpfTestHost.Invoke(() =>
        {
            AppThemeManager.ApplySettingsPalette(
                System.Windows.Application.Current.Resources,
                AppTheme.ForestNight);
            using var image = new CapturedImage(new System.Drawing.Bitmap(80, 60));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                var shell = Assert.IsType<Border>(window.FindName("PinnedShell"));
                var dark = Assert.IsType<LinearGradientBrush>(shell.Background)
                    .GradientStops[0].Color;

                AppThemeManager.ApplySettingsPalette(
                    System.Windows.Application.Current.Resources,
                    AppTheme.CoralSky);
                var light = Assert.IsType<LinearGradientBrush>(shell.Background)
                    .GradientStops[0].Color;

                Assert.NotEqual(dark, light);
            }
            finally
            {
                window.Close();
                AppThemeManager.ApplySettingsPalette(
                    System.Windows.Application.Current.Resources,
                    AppTheme.AuroraMist);
            }
        });
    }

    [Fact]
    public void CropAndAnnotationCommandsAreAvailableInTheTopToolbar()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(80, 60));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                Assert.IsType<Button>(window.FindName("CropButton"));
                Assert.IsType<Button>(window.FindName("EditButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SinglePinEditingUsesAnAttachedToolbarWithoutOpeningAnEditorPage()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(160, 100));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                window.Show();
                window.UpdateLayout();
                var windowCount = System.Windows.Application.Current.Windows.Count;
                var editButton = Assert.IsType<Button>(window.FindName("EditButton"));

                editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    Button.ClickEvent));

                Assert.True(window.IsInlineEditorVisible);
                Assert.False(window.IsInlineCropVisible);
                var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                Assert.Same(window, toolbar.Owner);
                Assert.IsType<RadioButton>(toolbar.FindName("ShapeToolButton"));
                var arrowButton = Assert.IsType<RadioButton>(
                    toolbar.FindName("ArrowToolButton"));
                Assert.Contains(
                    arrowButton.ContextMenu.Items.OfType<MenuItem>(),
                    item => Equals(item.Tag, "CurvedArrow,Filled"));
                Assert.IsType<RadioButton>(toolbar.FindName("EmojiToolButton"));
                Assert.IsType<RadioButton>(toolbar.FindName("NumberToolButton"));
                Assert.IsType<RadioButton>(toolbar.FindName("BrushToolButton"));
                Assert.IsType<RadioButton>(toolbar.FindName("TextToolButton"));
                Assert.IsType<RadioButton>(toolbar.FindName("MosaicToolButton"));
                Assert.IsType<Button>(toolbar.FindName("SaveButton"));
                Assert.IsType<Button>(toolbar.FindName("OcrButton"));
                Assert.IsType<Button>(toolbar.FindName("CopyTextButton"));
                Assert.IsType<Button>(toolbar.FindName("TranslateActionButton"));
                Assert.IsType<Button>(toolbar.FindName("PrivacyButton"));
                Assert.IsType<Button>(toolbar.FindName("CropToolButton"));
                Assert.IsType<Button>(toolbar.FindName("UndoButton"));
                Assert.IsType<System.Windows.Shapes.Path>(
                    toolbar.FindName("ScissorsIcon"));
                Assert.Equal(
                    windowCount + 1,
                    System.Windows.Application.Current.Windows.Count);
                Assert.DoesNotContain(
                    System.Windows.Application.Current.Windows
                        .OfType<Screenshot.App.Editor.ImageEditorWindow>(),
                    editor => editor.Owner == window);
                Assert.DoesNotContain(
                    System.Windows.Application.Current.Windows
                        .OfType<Screenshot.App.Editor.ImageCropWindow>(),
                    crop => crop.Owner == window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SinglePinEditorRestoresTheSavedEllipseShapeVariant()
    {
        WpfTestHost.Invoke(() =>
        {
            var savedShapeMode = ShapeToolMode.Rectangle;
            using var image = new CapturedImage(new System.Drawing.Bitmap(160, 100));
            var window = new PinnedImageWindow(
                image.Clone(),
                settingsProvider: () => AppSettings.CreateDefault() with
                {
                    ShapeToolMode = savedShapeMode,
                },
                shapeToolModeChanged: mode => savedShapeMode = mode);
            try
            {
                window.Show();
                window.UpdateLayout();
                var editButton = Assert.IsType<Button>(window.FindName("EditButton"));
                editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    Button.ClickEvent));
                var firstToolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                var ellipseMenuItem = Assert.IsType<MenuItem>(
                    firstToolbar.FindName("EllipseShapeMenuItem"));
                ellipseMenuItem.RaiseEvent(new System.Windows.RoutedEventArgs(
                    MenuItem.ClickEvent));

                Assert.Equal(ShapeToolMode.Ellipse, savedShapeMode);

                firstToolbar.Close();
                var restoredToolbar = window.CreateEditorToolbar(window);
                restoredToolbar.ShowEdit();
                var shapeButton = Assert.IsType<RadioButton>(
                    restoredToolbar.FindName("ShapeToolButton"));

                Assert.Equal(EditorTool.Ellipse.ToString(), shapeButton.Tag);
                Assert.True(Assert.IsType<MenuItem>(
                    restoredToolbar.FindName("EllipseShapeMenuItem")).IsChecked);
                restoredToolbar.Close();
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SinglePinEditorRestoresAndUsesTheSavedCurvedArrowVariant()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(160, 100));
            var window = new PinnedImageWindow(
                image.Clone(),
                settingsProvider: () => AppSettings.CreateDefault() with
                {
                    ArrowToolMode = ArrowToolMode.Curved,
                    ArrowStyle = ArrowStyle.Hollow,
                });
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.IsType<Button>(window.FindName("EditButton")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));
                var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                var arrowButton = Assert.IsType<RadioButton>(
                    toolbar.FindName("ArrowToolButton"));
                EditorTool? selectedTool = null;
                toolbar.ToolSelected += tool => selectedTool = tool;

                arrowButton.IsChecked = true;

                Assert.Equal(EditorTool.CurvedArrow, selectedTool);
                Assert.Equal(EditorTool.CurvedArrow.ToString(), arrowButton.Tag);
                Assert.True(Assert.IsType<MenuItem>(
                    toolbar.FindName("CurvedHollowArrowMenuItem")).IsChecked);
                Assert.False(Assert.IsType<MenuItem>(
                    toolbar.FindName("StraightFilledArrowMenuItem")).IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SinglePinCropAppliesDirectlyToThePinnedImage()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(100, 80));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                window.Show();
                window.UpdateLayout();
                var cropButton = Assert.IsType<Button>(window.FindName("CropButton"));
                cropButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    Button.ClickEvent));
                var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                var applyButton = Assert.IsType<Button>(
                    toolbar.FindName("CropApplyButton"));

                applyButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    Button.ClickEvent));

                Assert.False(window.IsInlineCropVisible);
                Assert.Null(window.EditorToolbar);
                Assert.Equal(100, window.Preview.PixelWidth);
                Assert.Equal(80, window.Preview.PixelHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CropCornersReceiveDragInsteadOfTheWindowResizeHandles()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(200, 140));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.IsType<Button>(window.FindName("CropButton")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                foreach (var name in WindowResizeHandleNames)
                {
                    Assert.False(Assert.IsType<
                        System.Windows.Controls.Primitives.Thumb>(
                            window.FindName(name)).IsHitTestVisible);
                }

                var cropCorner = Assert.IsType<
                    System.Windows.Controls.Primitives.Thumb>(
                        window.FindName("CropTopLeftThumb"));
                Assert.True(cropCorner.IsHitTestVisible);
                cropCorner.RaiseEvent(new System.Windows.Controls.Primitives
                    .DragDeltaEventArgs(18, 14)
                {
                    RoutedEvent = System.Windows.Controls.Primitives
                        .Thumb.DragDeltaEvent,
                });

                var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                toolbar.UpdateLayout();
                var applyButton = Assert.IsType<Button>(
                    toolbar.FindName("CropApplyButton"));
                Assert.True(applyButton.ActualWidth >= 80);
                applyButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    Button.ClickEvent));

                Assert.InRange(window.Preview.PixelWidth, 1, 199);
                Assert.InRange(window.Preview.PixelHeight, 1, 139);
                Assert.True(Assert.IsType<
                    System.Windows.Controls.Primitives.Thumb>(
                        window.FindName("WindowResizeTopLeftThumb"))
                    .IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task AutomaticallyRecognizesAndDisplaysSelectableText()
    {
        PinnedImageWindow? window = null;
        Task? recognitionTask = null;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new PinnedImageWindow(
                    new CapturedImage(new System.Drawing.Bitmap(240, 120)),
                    _ => Task.FromResult(new OcrRecognitionResult(
                        true,
                        "Selectable text",
                        ErrorMessage: null)
                    {
                        Regions =
                        [
                            new OcrTextRegion(
                                "Selectable text",
                                20,
                                24,
                                140,
                                24),
                        ],
                    }),
                    _ => Task.FromResult(TranslationSegmentsResult.Failure(
                        "unused")));
                window.Show();
                window.UpdateLayout();
                recognitionTask = window.TextRecognitionTask;
            });

            await Assert.IsAssignableFrom<Task>(recognitionTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                var textBox = Assert.Single(overlay.Children.OfType<TextBox>());
                var translateButton = Assert.IsType<Button>(
                    window.FindName("TranslateButton"));
                Assert.Equal("Selectable text", textBox.Text);
                Assert.True(textBox.IsReadOnly);
                Assert.True(translateButton.IsEnabled);
                Assert.True(PinnedImageWindow.IsSelectableTextSource(textBox));
                Assert.False(PinnedImageWindow.IsSelectableTextSource(overlay));
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public async Task TranslationReplacesSelectableOverlayTextOnTheImage()
    {
        PinnedImageWindow? window = null;
        Task? recognitionTask = null;
        var translationRequestCount = 0;

        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new PinnedImageWindow(
                    new CapturedImage(new System.Drawing.Bitmap(240, 120)),
                    _ => Task.FromResult(new OcrRecognitionResult(
                        true,
                        "Hello",
                        ErrorMessage: null)
                    {
                        Regions = [new OcrTextRegion("Hello", 20, 24, 80, 24)],
                    }),
                    _ =>
                    {
                        translationRequestCount++;
                        return Task.FromResult(new TranslationSegmentsResult(
                            true,
                            ["你好"],
                            ErrorMessage: null));
                    });
                window.Show();
                window.UpdateLayout();
                recognitionTask = window.TextRecognitionTask;
            });

            await Assert.IsAssignableFrom<Task>(recognitionTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Task? translationTask = null;
            WpfTestHost.Invoke(
                () => translationTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(translationTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                var textBox = Assert.Single(overlay.Children.OfType<TextBox>());
                Assert.Equal("你好", textBox.Text);
                Assert.NotEqual(
                    System.Windows.Media.Brushes.Transparent,
                    textBox.Foreground);
                Assert.NotEqual(
                    System.Windows.Media.Brushes.Transparent,
                    textBox.Background);
                var statusText = Assert.IsType<TextBlock>(
                    window.FindName("HeaderStatusText"));
                Assert.Contains("译文可选择复制", statusText.Text);
                Assert.Equal("原文", Assert.IsType<Button>(
                    window.FindName("TranslateButton")).Content);
            });

            Task? showOriginalTask = null;
            WpfTestHost.Invoke(
                () => showOriginalTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(showOriginalTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                Assert.Equal("Hello", Assert.Single(
                    overlay.Children.OfType<TextBox>()).Text);
                Assert.Equal("译文", Assert.IsType<Button>(
                    window.FindName("TranslateButton")).Content);
            });

            Task? showCachedTranslationTask = null;
            WpfTestHost.Invoke(
                () => showCachedTranslationTask = window!.TranslateTextAsync());
            await Assert.IsAssignableFrom<Task>(showCachedTranslationTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<Canvas>(window!.FindName("TextOverlay"));
                Assert.Equal("你好", Assert.Single(
                    overlay.Children.OfType<TextBox>()).Text);
            });
            Assert.Equal(1, translationRequestCount);
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [Fact]
    public void IsTopmostAndHiddenFromTheTaskbar()
    {
        var isVisible = false;
        var isTopmost = false;
        var isHiddenFromTaskbar = false;

        WpfTestHost.Invoke(() =>
        {
            var virtualDesktop = VirtualScreen.GetBounds();
            CapturedImage? image = ScreenCaptureService.Capture(
                new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1));
            PinnedImageWindow? window = null;

            try
            {
                window = new PinnedImageWindow(image);
                image = null;
                window.Show();
                isVisible = window.IsVisible;
                isTopmost = window.Topmost;
                isHiddenFromTaskbar = !window.ShowInTaskbar;
            }
            finally
            {
                window?.Close();
                image?.Dispose();
            }
        });

        Assert.True(isVisible);
        Assert.True(isTopmost);
        Assert.True(isHiddenFromTaskbar);
    }
}
