#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a boolean to Visibility: true → Visible, false → Collapsed.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            // Support ConverterParameter="Inverse" to negate the boolean
            if (parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
            {
                b = !b;
            }

            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Visible;
        }

        return false;
    }
}
