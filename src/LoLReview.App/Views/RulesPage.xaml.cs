#nullable enable

using LoLReview.App.Helpers;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Rules page — personal gameplay rules and adherence tracking.</summary>
public sealed partial class RulesPage : Page
{
    public RulesViewModel ViewModel { get; }

    public RulesPage()
    {
        ViewModel = App.GetService<RulesViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            await ViewModel.ToggleRuleCommand.ExecuteAsync(ruleId);
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            ViewModel.StartEditingCommand.Execute(ruleId);
        }
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            // Show confirmation dialog
            var dialog = new ContentDialog
            {
                Title = "Delete Rule",
                Content = "Delete this rule? This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteRuleCommand.ExecuteAsync(ruleId);
            }
        }
    }
}
