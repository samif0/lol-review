#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Revu.App.Converters;

/// <summary>
/// Inverts a boolean to Visibility: true → Collapsed, false → Visible.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return v == Visibility.Collapsed;
        return false;
    }
}
