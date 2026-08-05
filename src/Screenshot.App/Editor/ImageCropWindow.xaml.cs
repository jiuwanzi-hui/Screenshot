using System.Windows;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Editor;

public partial class ImageCropWindow : Window
{
    private readonly BitmapSource _source;
    private bool _isInitialized;
    private bool _isUpdating;

    public ImageCropWindow(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        InitializeComponent();

        LeftCropSlider.Maximum = Math.Max(0, source.PixelWidth - 1);
        RightCropSlider.Maximum = Math.Max(0, source.PixelWidth - 1);
        TopCropSlider.Maximum = Math.Max(0, source.PixelHeight - 1);
        BottomCropSlider.Maximum = Math.Max(0, source.PixelHeight - 1);
        _isInitialized = true;
        UpdatePreview();
    }

    public BitmapSource? CroppedImage { get; private set; }

    internal static Int32Rect CalculateCropRect(
        int width,
        int height,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (width <= 0 || height <= 0)
        {
            return Int32Rect.Empty;
        }

        left = Math.Clamp(left, 0, width - 1);
        top = Math.Clamp(top, 0, height - 1);
        right = Math.Clamp(right, 0, width - left - 1);
        bottom = Math.Clamp(bottom, 0, height - top - 1);
        return new Int32Rect(
            left,
            top,
            width - left - right,
            height - top - bottom);
    }

    private void OnCropValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitialized)
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        try
        {
            var rect = GetCropRect();
            LeftCropSlider.Maximum = Math.Max(
                0,
                _source.PixelWidth - (int)RightCropSlider.Value - 1);
            RightCropSlider.Maximum = Math.Max(
                0,
                _source.PixelWidth - (int)LeftCropSlider.Value - 1);
            TopCropSlider.Maximum = Math.Max(
                0,
                _source.PixelHeight - (int)BottomCropSlider.Value - 1);
            BottomCropSlider.Maximum = Math.Max(
                0,
                _source.PixelHeight - (int)TopCropSlider.Value - 1);

            var preview = new CroppedBitmap(_source, rect);
            preview.Freeze();
            CropPreviewImage.Source = preview;
            CropSizeText.Text = $"{rect.Width} x {rect.Height} px";
            LeftCropText.Text = $"{rect.X} px";
            TopCropText.Text = $"{rect.Y} px";
            RightCropText.Text =
                $"{_source.PixelWidth - rect.X - rect.Width} px";
            BottomCropText.Text =
                $"{_source.PixelHeight - rect.Y - rect.Height} px";
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private Int32Rect GetCropRect()
    {
        return CalculateCropRect(
            _source.PixelWidth,
            _source.PixelHeight,
            (int)Math.Round(LeftCropSlider.Value),
            (int)Math.Round(TopCropSlider.Value),
            (int)Math.Round(RightCropSlider.Value),
            (int)Math.Round(BottomCropSlider.Value));
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        LeftCropSlider.Value = 0;
        TopCropSlider.Value = 0;
        RightCropSlider.Value = 0;
        BottomCropSlider.Value = 0;
        UpdatePreview();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var cropped = new CroppedBitmap(_source, GetCropRect());
        cropped.Freeze();
        CroppedImage = cropped;
        DialogResult = true;
    }
}
