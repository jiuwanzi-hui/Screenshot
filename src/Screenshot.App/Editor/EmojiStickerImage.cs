using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Screenshot.App.Editor;

public sealed class EmojiStickerImage : System.Windows.Controls.Image
{
    public static readonly DependencyProperty StickerProperty = DependencyProperty.Register(
        nameof(Sticker),
        typeof(EmojiSticker),
        typeof(EmojiStickerImage),
        new PropertyMetadata(EmojiSticker.Smile, OnStickerChanged));

    public EmojiStickerImage()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        Source = EmojiStickerRenderer.GetImage(Sticker);
    }

    public EmojiSticker Sticker
    {
        get => (EmojiSticker)GetValue(StickerProperty);
        set => SetValue(StickerProperty, value);
    }

    private static void OnStickerChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is EmojiStickerImage image &&
            eventArgs.NewValue is EmojiSticker sticker)
        {
            image.Source = EmojiStickerRenderer.GetImage(sticker);
        }
    }
}
