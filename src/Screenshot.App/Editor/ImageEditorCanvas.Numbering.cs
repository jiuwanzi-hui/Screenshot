using System.Globalization;
using System.Windows;
using Point = System.Windows.Point;

namespace Screenshot.App.Editor;

public partial class ImageEditorCanvas
{
    private NumberingStyle _numberingStyle;
    private int? _nextNumberOverride;
    private Point? _pendingNumberEditPosition;

    internal int NextNumber => _nextNumberOverride ?? Math.Min(
        (_document.Annotations.OfType<NumberAnnotation>().LastOrDefault()?.Value ??
            _document.Annotations.OfType<NumberAnnotation>().Count()) + 1,
        AnnotationNumbering.Maximum(_numberingStyle));

    internal (NumberingStyle Style, int Value, bool Editing) GetNumberOptions()
    {
        if (_selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _document.Annotations.Count &&
            _document.Annotations[_selectedAnnotationIndex] is NumberAnnotation number)
            return (number.Style, number.Value ?? int.Parse(GetNumberLabel(number), CultureInfo.InvariantCulture), true);
        return (_numberingStyle, NextNumber, false);
    }

    internal void SetNumberOptions(NumberingStyle style, int value, bool editSelected)
    {
        if (!Enum.IsDefined(style) || value < 1 || value > AnnotationNumbering.Maximum(style))
            throw new ArgumentOutOfRangeException(nameof(value));
        _numberingStyle = style;
        if (editSelected && _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _document.Annotations.Count &&
            _document.Annotations[_selectedAnnotationIndex] is NumberAnnotation number)
        {
            ReplaceSelectedAnnotation(number, number with { Value = value, Style = style });
        }
        else
        {
            _nextNumberOverride = value;
        }
    }

    internal void AddNumberAnnotation(Point point)
    {
        var size = Math.Max(24, _strokeWidth * 9);
        _document.Add(new NumberAnnotation(new Point(point.X - size / 2, point.Y - size / 2),
            size, _selectedColor, NextNumber, _numberingStyle));
        _nextNumberOverride = null;
        RebuildCanvas();
        RaiseHistoryChanged();
    }

    private bool TryEditNumberAnnotation(Point point)
    {
        var index = HitTestAnnotation(point);
        if (index < 0 || _document.Annotations[index] is not NumberAnnotation) return false;
        if (_isEditingAnnotation) CancelAnnotationEdit();
        _selectedAnnotationIndex = index;
        RebuildCanvas();
        AnnotationSelectionChanged?.Invoke(this, EventArgs.Empty);
        _pendingNumberEditPosition = point;
        CaptureMouse();
        return true;
    }
}
