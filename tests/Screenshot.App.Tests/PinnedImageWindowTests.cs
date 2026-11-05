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
    [DesktopFact]
    public void ClosingEditorKeepsAnnotationsAndRestoresWholeImageDragging()
    {
        WpfTestHost.Invoke(() =>
        {
            using var bitmap = new System.Drawing.Bitmap(180, 120);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(x, y, (x + y) % 256));
            using var original = new CapturedImage((System.Drawing.Bitmap)bitmap.Clone());
            var window = new PinnedImageWindow(original.Clone());
            try
            {
                window.Show();
                window.Width = 250;
                window.Height = 167;
                window.UpdateLayout();
                var bounds = window.GetPersistenceBounds();
                Assert.True(window.CanMoveImage);
                Assert.IsType<MenuItem>(window.FindName("EditMenuItem")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.False(window.CanMoveImage);
                var canvas = window.AnnotationCanvas;
                canvas.AddMosaicRegions([new System.Windows.Rect(10, 10, 80, 40)]);
                Assert.True(canvas.CanUndo);
                using var expected = CapturedImage.FromBitmapSource(canvas.RenderEditedImage());
                window.EditorToolbar!.Close();
                Assert.True(window.CanMoveImage);
                Assert.False(window.IsInlineEditorVisible);
                Assert.Null(window.EditorToolbar);
                Assert.Equal(bounds, window.GetPersistenceBounds());
                using var actual = window.CloneImage();
                ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected.Bitmap, actual.Bitmap);
                Assert.Null(window.FindName("PinnedHeader"));
                var surface = Assert.IsType<Border>(window.FindName("ImageSurface"));
                Assert.Equal((byte)0, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
                OpenCropFromToolbar(window);
                Assert.False(window.CanMoveImage);
                window.EditorToolbar!.Close();
                Assert.True(window.CanMoveImage);
            }
            finally { window.Close(); }
        });
    }

    private static void OpenCropFromToolbar(PinnedImageWindow window)
    {
        if (!window.IsInlineEditorVisible)
            Assert.IsType<MenuItem>(window.FindName("EditMenuItem")).RaiseEvent(
                new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        var crop = Assert.IsType<Button>(window.EditorToolbar!.FindName("CropToolButton"));
        crop.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    }

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

    [DesktopFact]
    public void SinglePinEditorZoomsWithTheMouseWheel()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(800, 600));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.IsType<MenuItem>(window.FindName("EditMenuItem")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                var editor = Assert.IsType<ImageEditorCanvas>(
                    window.FindName("InlineEditorCanvas"));
                var surface = Assert.IsType<Border>(window.FindName("ImageSurface"));
                var wheel = new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    120)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent,
                };

                surface.RaiseEvent(wheel);

                Assert.True(wheel.Handled);
                Assert.True(editor.Zoom > 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SinglePinStaysTransparentAcrossApplicationThemes()
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
                var dark = Assert.IsType<SolidColorBrush>(shell.Background).Color;

                AppThemeManager.ApplySettingsPalette(
                    System.Windows.Application.Current.Resources,
                    AppTheme.CoralSky);
                var light = Assert.IsType<SolidColorBrush>(shell.Background).Color;

                Assert.Equal((byte)0, dark.A);
                Assert.Equal((byte)0, light.A);
                Assert.Null(window.FindName("PinnedHeader"));
                Assert.Equal(new System.Windows.Thickness(PinnedImageWindow.ShadowMarginDip), shell.Margin);
                Assert.Equal(new System.Windows.Thickness(0), shell.BorderThickness);
                Assert.Null(shell.Effect); // Resampling the entire image causes white edge artifacts.
                var outline = Assert.IsType<PinnedImageOutline>(window.FindName("ImageOutline"));
                var shadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(outline.Effect);
                Assert.InRange(shadow.Opacity, 0.2, 0.5);
                Assert.True(shadow.BlurRadius > 0);
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
    public void ContextMenuOffersDirectEditingWithoutToolSubmenus()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new System.Drawing.Bitmap(80, 60));
            var window = new PinnedImageWindow(image.Clone());
            try
            {
                var shell = Assert.IsType<Border>(window.FindName("PinnedShell"));
                var edit = Assert.Single(shell.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "编辑"));
                Assert.False(edit.HasItems);
                Assert.Same(window.FindName("EditMenuItem"), edit);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DesktopFact]
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
                var shell = Assert.IsType<Border>(window.FindName("PinnedShell"));
                var editButton = Assert.Single(shell.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "编辑"));
                Assert.False(editButton.HasItems);

                editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    MenuItem.ClickEvent));

                Assert.True(window.IsInlineEditorVisible);
                Assert.False(window.IsInlineCropVisible);
                var toolbar = Assert.IsType<PinnedImageEditorToolbarWindow>(
                    window.EditorToolbar);
                Assert.Same(window, toolbar.Owner);
                Assert.True(toolbar.IsVisible);
                Assert.True(toolbar.ActualWidth > 0 && toolbar.ActualHeight > 0);
                Assert.True(MonitorGeometryService.TryGetWindowBounds(toolbar, out var toolbarBounds));
                var workArea = MonitorGeometryService.GetWorkArea(toolbarBounds);
                Assert.True(workArea.Contains(toolbarBounds));
                var opacityButton = Assert.IsType<Button>(toolbar.FindName("OpacityButton"));
                var opacity = opacityButton.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "80"));
                opacity.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(0.8, window.Opacity);
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

    [DesktopFact]
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
                var editButton = Assert.IsType<MenuItem>(window.FindName("EditMenuItem"));
                editButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    MenuItem.ClickEvent));
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

    [DesktopFact]
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
                Assert.IsType<MenuItem>(window.FindName("EditMenuItem")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
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

    [DesktopFact]
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
                OpenCropFromToolbar(window);
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

    [DesktopFact]
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
                OpenCropFromToolbar(window);
                window.UpdateLayout();

                foreach (var name in WindowResizeHandleNames)
                {
                    Assert.Null(window.FindName(name));
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
                Assert.True(window.CanMoveImage);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DesktopFact]
    public async Task NewPinHasNoAutomaticTextLayerAndWholeImageCanMove()
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
                Assert.Empty(overlay.Children);
                Assert.False(overlay.IsHitTestVisible);
                Assert.True(window.CanMoveImage);
                Assert.True(window.IsTranslationEnabled);
                Assert.False(PinnedImageWindow.IsSelectableTextSource(overlay));
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
        }
    }

    [DesktopFact]
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
                var statusText = Assert.IsType<MenuItem>(
                    window.FindName("StatusMenuItem"));
                Assert.Contains("已显示译文", Assert.IsType<string>(statusText.Header));
                Assert.Equal("原文", window.TranslationActionText);
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
                Assert.Equal("译文", window.TranslationActionText);
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

    [DesktopFact]
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
