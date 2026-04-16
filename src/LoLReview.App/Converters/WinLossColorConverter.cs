#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using LoLReview.App.Styling;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a boolean (win = true) to the appropriate color brush.
/// true (win) → Positive (#7EC9A0), false (loss) → Negative (#D38C90).
/// </summary>
public sealed class WinLossColorConverter : IValueConverter
{
    private static readonly SolidColorBrush WinBrush = AppSemanticPalette.Brush(AppSemanticPalette.PositiveHex);
    private static readonly SolidColorBrush LossBrush = AppSemanticPalette.Brush(AppSemanticPalette.NegativeHex);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool win)
        {
            return win ? WinBrush : LossBrush;
        }

        return LossBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
