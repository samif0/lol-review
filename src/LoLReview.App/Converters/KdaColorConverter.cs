#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using LoLReview.App.Styling;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a KDA value (double) to a color brush:
/// >= 3.0 → green, >= 2.0 → gold, else → default text color.
/// </summary>
public sealed class KdaColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = AppSemanticPalette.Brush(AppSemanticPalette.PositiveHex);
    private static readonly SolidColorBrush GoldBrush = AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldHex);
    private static readonly SolidColorBrush DefaultBrush = AppSemanticPalette.Brush(AppSemanticPalette.PrimaryTextHex);

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
