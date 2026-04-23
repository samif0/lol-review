#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Views;

/// <summary>Analytics page — charts, trends, and player profiling.</summary>
public sealed partial class AnalyticsPage : Page
{
    public AnalyticsViewModel ViewModel { get; }

    public AnalyticsPage()
    {
        ViewModel = App.GetService<AnalyticsViewModel>();
        InitializeComponent();

        WeakReferenceMessenger.Default.Register<AnalyticsPage, GameDeletedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Helper for x:Bind — returns Visible when count > 0.</summary>
    public Visibility HasItems(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
