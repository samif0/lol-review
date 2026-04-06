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
        new(ColorHelper.FromArgb(255, 120, 214, 174));  // #78D6AE (PositiveHex)

    private static readonly SolidColorBrush BlueBrush =
        new(ColorHelper.FromArgb(255, 137, 243, 199));  // #89F3C7 (AccentBlueHex)

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
