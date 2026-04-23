#nullable enable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Revu.App.Controls;

/// <summary>
/// Container that holds <see cref="StatCell"/>s in a horizontal connected strip.
/// Cells are evenly sized via star columns and have their Position assigned
/// automatically (First / Middle / Last) so border + corner radius merge cleanly.
/// </summary>
[ContentProperty(Name = nameof(Cells))]
public sealed partial class StatStrip : UserControl
{
    public StatStrip()
    {
        InitializeComponent();
        Cells.CollectionChanged += OnCellsChanged;
        Loaded += (_, _) => Rebuild();
    }

    public ObservableCollection<StatCell> Cells { get; } = new();

    private void OnCellsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (LayoutRoot is null) return;
        LayoutRoot.Children.Clear();
        LayoutRoot.ColumnDefinitions.Clear();

        var count = Cells.Count;
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            LayoutRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = Cells[i];
            cell.Position = count == 1
                ? StatCellPosition.Only
                : i == 0
                    ? StatCellPosition.First
                    : i == count - 1
                        ? StatCellPosition.Last
                        : StatCellPosition.Middle;

            Grid.SetColumn(cell, i);
            LayoutRoot.Children.Add(cell);
        }
    }
}
