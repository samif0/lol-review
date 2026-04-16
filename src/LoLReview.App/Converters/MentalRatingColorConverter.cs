#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a mental rating (int 1-10) to a color brush:
/// >= 8 → green, >= 5 → blue, else → red.
/// </summary>
public sealed class MentalRatingColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush =
        new(ColorHelper.FromArgb(255, 126, 201, 160));  // #7EC9A0 (PositiveHex)

    private static readonly SolidColorBrush BlueBrush =
        new(ColorHelper.FromArgb(255, 167, 139, 250));  // #A78BFA (AccentBlueHex — violet)

    private static readonly SolidColorBrush RedBrush =
        new(ColorHelper.FromArgb(255, 211, 140, 144));  // #D38C90 (NegativeHex)

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var rating = value switch
        {
            int i => i,
            double d => (int)d,
            long l => (int)l,
            _ => 5
        };

        if (rating >= 8) return GreenBrush;
        if (rating >= 5) return BlueBrush;
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
