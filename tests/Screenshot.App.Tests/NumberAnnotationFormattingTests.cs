using System.Reflection;
using Screenshot.App.Editor;
using Point = System.Windows.Point;

namespace Screenshot.App.Tests;

public sealed class NumberAnnotationFormattingTests
{
    [DesktopFact]
    public void NumberOptionsPopupStepsAndChangesFormatImmediatelyWithoutClosing()
    {
        WpfTestHost.Invoke(() =>
        {
            var canvas = new ImageEditorCanvas();
            canvas.InitializeAnnotationOverlay(400, 200, 400, 200);
            canvas.SetNumberOptions(NumberingStyle.Digits, 26, false);
            var host = new System.Windows.Window { Width = 420, Height = 260, ShowActivated = false, Content = canvas };
            System.Windows.Controls.Primitives.Popup? popup = null;
            try
            {
                host.Show();
                var selections = 0;
                popup = NumberAnnotationOptions.Show(canvas, canvas, selectTool: () => selections++);
                host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var panel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)popup.Child).Child;
                var format = panel.Children.OfType<System.Windows.Controls.ComboBox>().Single();
                var (decrease, current, increase) = Stepper(panel);
                format.SelectedValue = NumberingStyle.UpperLetters;
                Assert.Equal((NumberingStyle.UpperLetters, 26, false), canvas.GetNumberOptions());
                Assert.Equal("Z", current.Text);
                Click(increase);
                Assert.Equal((NumberingStyle.UpperLetters, 27, false), canvas.GetNumberOptions());
                Assert.Equal("AA", current.Text);
                Click(decrease);
                Assert.Equal("Z", current.Text);
                format.SelectedValue = NumberingStyle.LowerRoman;
                Assert.Equal((NumberingStyle.LowerRoman, 26, false), canvas.GetNumberOptions());
                Assert.Equal("xxvi", current.Text);
                Assert.True(popup.IsOpen);
                Assert.Equal(1, selections);
                Assert.Empty(panel.Children.OfType<System.Windows.Controls.TextBox>());
                Assert.Empty(panel.Children.OfType<System.Windows.Controls.Button>());
                popup.IsOpen = false;
                canvas.AddNumberAnnotation(new Point(40, 40));
                Assert.Equal(27, canvas.NextNumber);

                canvas.SetNumberOptions(NumberingStyle.Digits, 9999, false);
                popup = NumberAnnotationOptions.Show(canvas, canvas);
                panel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)popup.Child).Child;
                format = panel.Children.OfType<System.Windows.Controls.ComboBox>().Single();
                (decrease, current, increase) = Stepper(panel);
                Assert.False(increase.IsEnabled);
                format.SelectedValue = NumberingStyle.UpperRoman;
                Assert.Equal((NumberingStyle.UpperRoman, 3999, false), canvas.GetNumberOptions());
                Assert.Equal("MMMCMXCIX", current.Text);
                Assert.False(increase.IsEnabled);
                Click(decrease);
                Assert.True(increase.IsEnabled);
                popup.IsOpen = false;

                canvas.SetNumberOptions(NumberingStyle.Digits, 1, false);
                popup = NumberAnnotationOptions.Show(canvas, canvas);
                panel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)popup.Child).Child;
                (decrease, current, increase) = Stepper(panel);
                Assert.False(decrease.IsEnabled);
                Click(increase);
                Assert.True(decrease.IsEnabled);
                Assert.Equal((NumberingStyle.Digits, 2, false), canvas.GetNumberOptions());
            }
            finally { if (popup is not null) popup.IsOpen = false; host.Close(); canvas.Reset(); }
        });
    }

    [DesktopFact]
    public void NumberOptionsPopupEditsExistingNumberImmediatelyAndSupportsUndo()
    {
        WpfTestHost.Invoke(() =>
        {
            var canvas = new ImageEditorCanvas();
            canvas.InitializeAnnotationOverlay(400, 200, 400, 200);
            canvas.SetNumberOptions(NumberingStyle.Digits, 9, false);
            canvas.AddNumberAnnotation(new Point(40, 40));
            typeof(ImageEditorCanvas).GetField("_selectedAnnotationIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, 0);
            var host = new System.Windows.Window { Width = 420, Height = 260, ShowActivated = false, Content = canvas };
            System.Windows.Controls.Primitives.Popup? popup = null;
            try
            {
                host.Show();
                var selected = false;
                popup = NumberAnnotationOptions.Show(canvas, canvas, selectTool: () => selected = true);
                var panel = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)popup.Child).Child;
                var format = panel.Children.OfType<System.Windows.Controls.ComboBox>().Single();
                var (_, current, increase) = Stepper(panel);
                format.SelectedValue = NumberingStyle.UpperRoman;
                Assert.Equal((NumberingStyle.UpperRoman, 9, true), canvas.GetNumberOptions());
                Assert.Equal("IX", current.Text);
                Click(increase);
                Assert.Equal((NumberingStyle.UpperRoman, 10, true), canvas.GetNumberOptions());
                Assert.Equal("X", current.Text);
                Assert.True(popup.IsOpen);
                Assert.False(selected);
                popup.IsOpen = false;
                var document = (EditorDocument)typeof(ImageEditorCanvas).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
                canvas.Undo();
                Assert.Equal("IX", Label(canvas, document.Annotations.Single()));
                canvas.Undo();
                Assert.Equal("9", Label(canvas, document.Annotations.Single()));
                canvas.Redo();
                Assert.Equal("IX", Label(canvas, document.Annotations.Single()));
            }
            finally { if (popup is not null) popup.IsOpen = false; host.Close(); canvas.Reset(); }
        });
    }

    private static (System.Windows.Controls.Primitives.RepeatButton Decrease, System.Windows.Controls.TextBlock Current,
        System.Windows.Controls.Primitives.RepeatButton Increase) Stepper(System.Windows.Controls.StackPanel panel)
    {
        var grid = panel.Children.OfType<System.Windows.Controls.Grid>().Single();
        var buttons = grid.Children.OfType<System.Windows.Controls.Primitives.RepeatButton>().ToArray();
        var current = (System.Windows.Controls.TextBlock)grid.Children.OfType<System.Windows.Controls.Viewbox>().Single().Child;
        return (buttons[0], current, buttons[1]);
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Theory]
    [InlineData(1, NumberingStyle.Digits, "1")]
    [InlineData(125, NumberingStyle.Digits, "125")]
    [InlineData(26, NumberingStyle.UpperLetters, "Z")]
    [InlineData(27, NumberingStyle.UpperLetters, "AA")]
    [InlineData(703, NumberingStyle.LowerLetters, "aaa")]
    [InlineData(4, NumberingStyle.UpperRoman, "IV")]
    [InlineData(49, NumberingStyle.LowerRoman, "xlix")]
    [InlineData(3999, NumberingStyle.UpperRoman, "MMMCMXCIX")]
    public void FormatsAndParsesSequenceLabels(int value, NumberingStyle style, string label)
    {
        Assert.Equal(label, AnnotationNumbering.Format(value, style));
        Assert.True(AnnotationNumbering.TryParse(label, style, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("IIII", NumberingStyle.UpperRoman)]
    [InlineData("IC", NumberingStyle.UpperRoman)]
    [InlineData("4000", NumberingStyle.UpperRoman)]
    [InlineData("0", NumberingStyle.Digits)]
    [InlineData("-1", NumberingStyle.Digits)]
    [InlineData("A!", NumberingStyle.UpperLetters)]
    [InlineData("ZZZZZZZZZZZZZZZ", NumberingStyle.UpperLetters)]
    public void RejectsInvalidOrOutOfRangeLabels(string label, NumberingStyle style) =>
        Assert.False(AnnotationNumbering.TryParse(label, style, out _));

    [Fact]
    public void CustomStartEditUndoAndRedoKeepNumbersAndFormats()
    {
        WpfTestHost.Invoke(() =>
        {
            var canvas = new ImageEditorCanvas();
            using var image = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(400, 200));
            canvas.Initialize(image, 400, 200);
            canvas.SetNumberOptions(NumberingStyle.UpperLetters, 26, false);
            canvas.AddNumberAnnotation(new Point(40, 40));
            canvas.AddNumberAnnotation(new Point(100, 40));
            var document = (EditorDocument)typeof(ImageEditorCanvas).GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
            Assert.Equal(new int?[] { 26, 27 }, document.Annotations.Cast<NumberAnnotation>().Select(marker => marker.Value));
            canvas.Undo();
            Assert.Equal(27, canvas.NextNumber);
            canvas.Redo();
            Assert.Equal(28, canvas.NextNumber);
            typeof(ImageEditorCanvas).GetField("_selectedAnnotationIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, 0);
            canvas.SetNumberOptions(NumberingStyle.LowerRoman, 49, true);
            Assert.Equal((NumberingStyle.LowerRoman, 49, true), canvas.GetNumberOptions());
            Assert.Equal("xlix", Label(canvas, document.Annotations[0]));
            Assert.Equal("AA", Label(canvas, document.Annotations[1]));
            canvas.Undo();
            Assert.Equal("Z", Label(canvas, document.Annotations[0]));
            canvas.Redo();
            Assert.Equal("xlix", Label(canvas, document.Annotations[0]));
            Assert.NotNull(canvas.RenderEditedImage());
            canvas.Reset();
        });
    }

    private static string Label(ImageEditorCanvas canvas, EditorAnnotation annotation) =>
        (string)typeof(ImageEditorCanvas).GetMethod("GetNumberLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [annotation])!;
}
