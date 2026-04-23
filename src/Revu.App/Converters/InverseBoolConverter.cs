#nullable enable

using Microsoft.UI.Xaml.Data;

namespace Revu.App.Converters;

/// <summary>
/// Inverts a boolean value: true → false, false → true.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return !b;
        }

        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return !b;
        }

        return false;
    }
}
