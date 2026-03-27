#nullable enable

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.Converters;

/// <summary>
/// Converts a boolean (win = true) to the appropriate color brush.
/// true (win) → WinGreen (#22c55e), false (loss) → LossRed (#ef4444).
/// </summary>
public sealed class WinLossColorConverter : IValueConverter
{
    private static readonly SolidColorBrush WinBrush =
        new(ColorHelper.FromArgb(255, 34, 197, 94));   // #22c55e

    private static readonly SolidColorBrush LossBrush =
        new(ColorHelper.FromArgb(255, 239, 68, 68));    // #ef4444

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
