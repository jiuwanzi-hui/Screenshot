using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Screenshot.App.Tests;

public sealed class CaptureAspectRatioToolbarTests
{
    public static IEnumerable<object[]> Presets => Enum.GetValues<CaptureAspectRatio>()
        .Where(ratio => ratio != CaptureAspectRatio.Free).Select(ratio => new object[] { ratio });

    [Theory]
    [MemberData(nameof(Presets))]
    public void PresetsFitInsideSelectionAndKeepItsCenter(CaptureAspectRatio ratio)
    {
        var selection = new Rectangle(-1450, 130, 801, 601);
        Assert.True(CaptureAspectRatioHelper.TryFitSelection(selection,
            new Rectangle(-1920, 0, 3840, 1080), ratio, null, out var result));
        Assert.True(selection.Contains(result));
        Assert.InRange(Math.Abs(result.Width - result.Height * CaptureAspectRatioHelper.GetValue(ratio)!.Value), 0, 1.5);
        Assert.InRange(Math.Abs(result.Left + result.Width / 2d - (selection.Left + selection.Width / 2d)), 0, 0.5);
        Assert.InRange(Math.Abs(result.Top + result.Height / 2d - (selection.Top + selection.Height / 2d)), 0, 0.5);
    }

    [Fact]
    public void FreeRatioDoesNotMoveOrResizeTheSelection()
    {
        var selection = new Rectangle(103, 89, 401, 271);
        Assert.True(CaptureAspectRatioHelper.TryFitSelection(selection,
            new Rectangle(0, 0, 1920, 1080), CaptureAspectRatio.Free, null, out var result));
        Assert.Equal(selection, result);
    }

    [Fact]
    public void ExistingAnnotationsStayInsideTheNewBoundsAtScreenEdge()
    {
        var surface = new Rectangle(-1920, -400, 1920, 1080);
        var selection = new Rectangle(-500, 450, 500, 200);
        var annotations = new Rectangle(-490, 470, 480, 160);
        Assert.True(CaptureAspectRatioHelper.TryFitSelection(selection, surface,
            CaptureAspectRatio.Ratio1x1, annotations, out var result));
        Assert.True(surface.Contains(result));
        Assert.True(result.Contains(annotations));
        Assert.Equal(result.Width, result.Height);
    }

    [Fact]
    public void ImpossibleRatioKeepsTheCurrentRegionInsteadOfClippingAnnotations()
    {
        var surface = new Rectangle(0, 0, 1920, 1080);
        var selection = new Rectangle(0, 0, 1900, 1000);
        Assert.False(CaptureAspectRatioHelper.TryFitSelection(selection, surface,
            CaptureAspectRatio.Ratio9x16, selection, out var result));
        Assert.Equal(selection, result);
    }

    [Fact]
    public void UpgradeAddsRatioOnceWithoutChangingExistingFeatureOrderOrVisibility()
    {
        var oldOrder = new[] { CaptureToolbarFeature.Text, CaptureToolbarFeature.Save,
            CaptureToolbarFeature.VideoRecording, CaptureToolbarFeature.PinImage };
        var oldSettings = AppSettings.CreateDefault() with
        {
            SettingsVersion = 18,
            VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Text, CaptureToolbarFeature.Save],
            CaptureToolbarFeatureOrder = oldOrder,
        };
        var upgraded = oldSettings.Normalize();
        Assert.Equal(19, upgraded.SettingsVersion);
        Assert.Equal(new[] { CaptureToolbarFeature.Text, CaptureToolbarFeature.Save, CaptureToolbarFeature.AspectRatio },
            upgraded.VisibleCaptureToolbarFeatures);
        Assert.Equal(oldOrder, upgraded.CaptureToolbarFeatureOrder.Where(oldOrder.Contains));
        Assert.Equal(Array.IndexOf(upgraded.CaptureToolbarFeatureOrder, CaptureToolbarFeature.VideoRecording) - 1,
            Array.IndexOf(upgraded.CaptureToolbarFeatureOrder, CaptureToolbarFeature.AspectRatio));
        Assert.Equal(upgraded.CaptureToolbarFeatureOrder, upgraded.Normalize().CaptureToolbarFeatureOrder);
        var hidden = upgraded with { VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Text] };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(hidden))!.Normalize();
        Assert.DoesNotContain(CaptureToolbarFeature.AspectRatio, restored.VisibleCaptureToolbarFeatures);
    }

    [Theory]
    [InlineData(CaptureToolbarRowCount.One)]
    [InlineData(CaptureToolbarRowCount.Two)]
    public void SettingsLayoutPreviewAndCaptureShareOrderingAndVisibility(CaptureToolbarRowCount rows)
    {
        WpfTestHost.Invoke(() =>
        {
            var viewModel = new SettingsViewModel(AppSettings.CreateDefault() with { CaptureToolbarRows = rows });
            var designer = new CaptureToolbarLayoutEditor { DataContext = viewModel };
            try
            {
                var ratio = Assert.Single(viewModel.CaptureToolbarFeatureItems,
                    item => item.Feature == CaptureToolbarFeature.AspectRatio);
                Assert.Equal(CaptureToolbarFeatureGroup.Action, ratio.Group);
                Assert.True(ratio.IsVisible);
                Assert.True(viewModel.MoveCaptureToolbarFeature(CaptureToolbarFeature.AspectRatio, CaptureToolbarFeature.Save, true));

                var layout = (StackPanel)designer.FindName("EditorRowsHost");
                var preview = (StackPanel)designer.FindName("PreviewRowsHost");
                Assert.Equal((int)rows, layout.Children.Count);
                Assert.Equal((int)rows, preview.Children.Count);
                Assert.Single(Buttons(layout).OfType<ToggleButton>(), b => ReferenceEquals(b.Tag, ratio));
                Assert.Single(Buttons(preview), b => b.ToolTip?.ToString() == ratio.Label);
                var previewButtons = Buttons(preview).ToList();
                Assert.Equal(previewButtons.FindIndex(b => b.ToolTip?.ToString() == UiLocalization.Get("保存图片")) + 1,
                    previewButtons.FindIndex(b => b.ToolTip?.ToString() == ratio.Label));

                using var pins = new PinnedImageManager();
                using (var overlay = CreateOverlay(pins, viewModel.CreateSettings()))
                {
                    var arranged = CaptureButtons(overlay);
                    var button = (Button)overlay.FindName("AspectRatioButton");
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.Equal(arranged.IndexOf((Button)overlay.FindName("SaveButton")) + 1, arranged.IndexOf(button));
                }

                ratio.IsVisible = false;
                Assert.DoesNotContain(Buttons(preview), b => b.ToolTip?.ToString() == ratio.Label);
                Assert.False(Assert.Single(Buttons(layout).OfType<ToggleButton>(), b => ReferenceEquals(b.Tag, ratio)).IsChecked);
                using var hidden = CreateOverlay(pins, viewModel.CreateSettings());
                Assert.Equal(Visibility.Collapsed, ((Button)hidden.FindName("AspectRatioButton")).Visibility);
            }
            finally { designer.DataContext = null; }
        });
    }

    [Fact]
    public void RatioMenuUsesSettingsPresetsAndAllInterfaceLanguages()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            var settings = AppSettings.CreateDefault() with { CaptureAspectRatio = CaptureAspectRatio.Ratio4x3 };
            using var overlay = CreateOverlay(pins, settings);
            var viewModel = new SettingsViewModel(settings);
            try
            {
                foreach (var language in UiLocalization.Languages)
                {
                    UiLocalization.Apply(language.Code);
                    var items = overlay.CreateAspectRatioMenu().Items.Cast<MenuItem>().ToArray();
                    Assert.Equal(viewModel.CaptureAspectRatioOptions.Count, items.Length);
                    Assert.Equal(CaptureAspectRatio.Ratio4x3, Assert.Single(items, i => i.IsChecked).Tag);
                    foreach (var item in items)
                    {
                        var source = CaptureAspectRatioHelper.GetLabel((CaptureAspectRatio)item.Tag);
                        Assert.Equal(UiLocalization.Get(source), item.Header);
                        if (language.Code != "zh-Hans") Assert.NotEqual(source, item.Header);
                    }
                }
            }
            finally { UiLocalization.Apply("zh-Hans"); }
        });
    }

    private static IEnumerable<Control> Buttons(StackPanel host) => host.Children.OfType<StackPanel>()
        .SelectMany(row => row.Children.OfType<Control>());

    private static List<FrameworkElement> CaptureButtons(CaptureOverlayWindow overlay) =>
        ((StackPanel)overlay.FindName("InlineEditorToolsRow1")).Children.Cast<FrameworkElement>()
            .Concat(((StackPanel)overlay.FindName("InlineEditorToolsRow2")).Children.Cast<FrameworkElement>()).ToList();

    private static CaptureOverlayWindow CreateOverlay(PinnedImageManager pins, AppSettings settings) => new(
        new CaptureOverlayOptions
        {
            SaveDirectory = Path.GetTempPath(), KeepHistory = false, HistoryLimit = 0,
            HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
            StartOcrAsync = image => { image.Dispose(); return Task.CompletedTask; },
            CaptureAspectRatio = settings.CaptureAspectRatio,
            VisibleToolbarFeatures = settings.VisibleCaptureToolbarFeatures,
            ToolbarFeatureOrder = settings.CaptureToolbarFeatureOrder,
            ToolbarRows = settings.CaptureToolbarRows,
        }, initialScreenSnapshot: new CapturedImage(new Bitmap(800, 600)),
        deferInitialColorPickerActivation: true);
}
