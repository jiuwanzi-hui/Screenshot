using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Screenshot.App.Core;
using Screenshot.App.Editor;

namespace Screenshot.App.Presentation;

/// <summary>Renders the same icon geometry used by the toolbar previews.</summary>
public sealed class ToolbarFeatureIcon : Grid
{
    public static readonly DependencyProperty FeatureProperty =
        DependencyProperty.Register(nameof(Feature), typeof(CaptureToolbarFeature),
            typeof(ToolbarFeatureIcon), new PropertyMetadata(CaptureToolbarFeature.Shape, OnIconPropertyChanged));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ToolbarFeatureIcon),
            new PropertyMetadata(string.Empty, OnIconPropertyChanged));

    public CaptureToolbarFeature Feature
    {
        get => (CaptureToolbarFeature)GetValue(FeatureProperty);
        set => SetValue(FeatureProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public ToolbarFeatureIcon()
    {
        Width = 20;
        Height = 20;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        VerticalAlignment = System.Windows.VerticalAlignment.Center;
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ToolbarFeatureIcon)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (Feature == CaptureToolbarFeature.Emoji)
        {
            Children.Add(new EmojiStickerImage { Width = 20, Height = 20, Sticker = "😊",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center });
            return;
        }

        if (Feature == CaptureToolbarFeature.Number)
        {
            Children.Add(AnnotationToolIcons.Create(AnnotationToolIcons.Number));
            return;
        }

        if (Feature is CaptureToolbarFeature.TextRecognition or CaptureToolbarFeature.CopyRecognizedText
            or CaptureToolbarFeature.Translation or CaptureToolbarFeature.PrivacyRedaction or CaptureToolbarFeature.Watermark)
        {
            var text = new TextBlock
            {
                Text = Glyph, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "EditorToolbarIconBrush");
            Children.Add(text);
            return;
        }

        var key = Feature switch
        {
            CaptureToolbarFeature.Shape => "M 2,2 H 16 V 16 H 2 Z",
            CaptureToolbarFeature.DoubleArrow => DoubleArrowGeometry.OutlineIcon,
            CaptureToolbarFeature.Arrow => "M 2,9 H 15 M 10,4 L 15,9 L 10,14",
            CaptureToolbarFeature.Line => "M 2,16 L 16,2",
            CaptureToolbarFeature.Brush => "M 3,14 L 4.2,9.8 L 12.6,1.4 L 16.6,5.4 L 8.2,13.8 Z M 11.2,3.8 L 14.2,6.8",
            CaptureToolbarFeature.Text => "M 3,3 H 15 M 9,3 V 16",
            CaptureToolbarFeature.Mosaic => AnnotationToolIcons.Mosaic,
            CaptureToolbarFeature.VideoRecording => "M 2,4 H 12 V 14 H 2 Z M 12,7 L 16,4 V 14 L 12,11 Z",
            CaptureToolbarFeature.AspectRatio => "M 1,3 H 17 V 15 H 1 Z M 5,11 L 13,7 M 5,8 V 11 H 8 M 10,7 H 13 V 10",
            CaptureToolbarFeature.Save => "M 3,2 H 13 L 16,5 V 16 H 3 Z M 5,2 V 7 H 13 V 2 M 6,11 H 13 V 16 H 6 Z",
            CaptureToolbarFeature.ScrollCapture => "M 9,2 V 16 M 6,5 L 9,2 L 12,5 M 6,13 L 9,16 L 12,13",
            CaptureToolbarFeature.CopyTable => "M 2,2 H 16 V 16 H 2 Z M 2,6 H 16 M 7,6 V 16 M 12,6 V 16 M 7,11 H 16",
            CaptureToolbarFeature.PinImage => "M 6,2 H 12 L 11.5,6 L 14,8.5 L 10,9.5 L 9,16 L 8,9.5 L 4,8.5 L 6.5,6 Z",
            CaptureToolbarFeature.UndoRedo when Glyph == "↷" => "M 12,2 L 18,8 L 12,14 V 11 C 7,11 4,13 3,17 C 3,10 7,6 12,6 Z",
            CaptureToolbarFeature.UndoRedo => "M 8,2 L 2,8 L 8,14 V 11 C 13,11 16,13 17,17 C 17,10 13,6 8,6 Z",
            _ => null,
        };

        if (key is null) return;
        var path = new Path { Data = Geometry.Parse(key), Width = 18, Height = 18, Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Fill = System.Windows.Media.Brushes.Transparent, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(Shape.StrokeProperty, "EditorToolbarIconBrush");
        Children.Add(path);
    }
}
