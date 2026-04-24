#nullable enable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Converters;

/// <summary>
/// Bool → Brush for filter chip backgrounds. Selected = violet-tinted panel,
/// unselected = the standard input-surface. Lets the filter bar on the
/// Analytics page show selection state without a per-chip style.
/// </summary>
public sealed class ChipBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is bool b && b;
        var key = selected ? "AccentBlueDimBrush" : "InputBackgroundBrush";
        return (Brush)Application.Current.Resources[key];
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
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var selected = value is bool b && b;
        var key = selected ? "AccentBlueBrush" : "SubtleBorderBrush";
        return (Brush)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
