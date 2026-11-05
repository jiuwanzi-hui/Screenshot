using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Screenshot.App.Presentation;

// One template for the capture canvas and the pin's small owned window.
public sealed class ImageQuestionEntry : System.Windows.Controls.Button
{
    public ImageQuestionEntry()
    {
        Width = 38; Height = 100; Content = "AI\n问\n图";
        ToolTip = "AI 问图：总结、提取文字、翻译或自由提问";
        FontSize = 13; FontWeight = FontWeights.SemiBold;
        SetResourceReference(BackgroundProperty, "AppInputBackgroundBrush");
        SetResourceReference(ForegroundProperty, "AppTextPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "AppAccentBrush");
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                <Border x:Name="Surface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource AppAccentMutedBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    }
}
