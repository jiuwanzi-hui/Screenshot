using System.Globalization;
using System.Windows.Data;

namespace Screenshot.App.Presentation;

internal sealed class EnumSelectionConverter : IValueConverter
{
    internal static readonly EnumSelectionConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : System.Windows.Data.Binding.DoNothing;
}
