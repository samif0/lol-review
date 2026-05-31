#nullable enable

using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Revu.App.Styling;

namespace Revu.App.Converters;

/// <summary>
/// Bool → Brush for filter chip backgrounds. Selected = violet-tinted panel,
/// unselected = the standard input-surface. Lets the filter bar on the
/// Analytics page show selection state without a per-chip style.
/// </summary>
public sealed class ChipBgConverter : IValueConverter
{
    private static readonly SolidColorBrush SelectedBrush = AppSemanticPalette.Brush(AppSemanticPalette.AccentBlueDimHex);
    private static readonly SolidColorBrush UnselectedBrush = AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is bool b && b;
        return selected ? SelectedBrush : UnselectedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Bool → Brush for filter chip borders. Selected = accent stroke,
/// unselected = the subtle panel-border brush.
/// </summary>
public sealed class ChipBorderConverter : IValueConverter
{
    private static readonly SolidColorBrush SelectedBrush = AppSemanticPalette.Brush(AppSemanticPalette.AccentBlueHex);
    private static readonly SolidColorBrush UnselectedBrush = AppSemanticPalette.Brush(AppSemanticPalette.SubtleBorderHex);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is bool b && b;
        return selected ? SelectedBrush : UnselectedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
