using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BastionFlow.App.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>If true, null → Visible. Otherwise null → Collapsed.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = Invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
