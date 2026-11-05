using Screenshot.App.Editor;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private SharedColorPickerControl? _inlineSharedColorPicker;

    // Most captures never open the custom color editor. Its sliders, text
    // input/IME and color surface must not be part of the first toolbar frame.
    private SharedColorPickerControl InlineSharedColorPicker
    {
        get
        {
            if (_inlineSharedColorPicker is not null) return _inlineSharedColorPicker;
            var picker = new SharedColorPickerControl();
            picker.ColorCommitted += OnSharedColorCommitted;
            picker.PaletteChanged += OnSharedPaletteChanged;
            picker.CloseRequested += OnSharedColorPickerCloseRequested;
            InlineSharedColorHost.Content = picker;
            RegisterName("InlineSharedColorPicker", picker);
            _inlineSharedColorPicker = picker;
            return picker;
        }
    }
}
