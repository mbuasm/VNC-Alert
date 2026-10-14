using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MaszynyAlert.App.Converters;

/// <summary>Zamienia null/pusty string na <see cref="Visibility.Collapsed"/>, inaczej <see cref="Visibility.Visible"/>.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Zamienia bool na <see cref="Visibility.Visible"/>/<see cref="Visibility.Collapsed"/> (bez opcji "Hidden").</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
