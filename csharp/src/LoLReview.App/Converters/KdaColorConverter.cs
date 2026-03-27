#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a KDA value (double) to a color brush:
/// >= 3.0 → green, >= 2.0 → gold, else → default text color.
/// </summary>
public sealed class KdaColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush =
        new(ColorHelper.FromArgb(255, 34, 197, 94));    // #22c55e

    private static readonly SolidColorBrush GoldBrush =
        new(ColorHelper.FromArgb(255, 200, 155, 60));   // #c89b3c

    private static readonly SolidColorBrush DefaultBrush =
        new(ColorHelper.FromArgb(255, 232, 232, 240));  // #e8e8f0

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kda = value switch
        {
            double d => d,
            float f => (double)f,
            int i => (double)i,
            _ => 0.0
        };

        if (kda >= 3.0) return GreenBrush;
        if (kda >= 2.0) return GoldBrush;
        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
