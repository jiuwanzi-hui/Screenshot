using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Screenshot.App.Editor;

public sealed class EmojiStickerImage : System.Windows.Controls.Image
{
    public static readonly DependencyProperty StickerProperty = DependencyProperty.Register(
        nameof(Sticker),
        typeof(string),
        typeof(EmojiStickerImage),
        new PropertyMetadata(EmojiStickerCatalog.Default, OnStickerChanged));

    public EmojiStickerImage()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => RefreshSource();
        RefreshSource();
    }

    public string Sticker
    {
        get => (string)GetValue(StickerProperty);
        set => SetValue(StickerProperty, value);
    }

    private static void OnStickerChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is EmojiStickerImage image &&
            eventArgs.NewValue is string sticker &&
            !string.IsNullOrWhiteSpace(sticker))
        {
            image.RefreshSource();
        }
    }

    /// <summary>
    /// Requests a rasterization matched to the displayed size (with headroom
    /// for display scaling) so small palette tiles stay as crisp as large
    /// placed stickers.
    /// </summary>
    private void RefreshSource()
    {
        var referenceSize = double.IsNaN(Width) || Width <= 0
            ? Math.Max(ActualWidth, ActualHeight)
            : Width;

        if (referenceSize <= 0)
        {
            referenceSize = 24;
        }

        Source = EmojiStickerRenderer.GetImage(
            Sticker,
            (int)Math.Ceiling(referenceSize * 2));
    }
}
