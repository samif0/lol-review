#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Views;

/// <summary>Objectives page — manage improvement objectives and track progress.</summary>
public sealed partial class ObjectivesPage : Page
{
    public ObjectivesViewModel ViewModel { get; }
    public RulesViewModel RulesVM { get; }

    public ObjectivesPage()
    {
        ViewModel = App.GetService<ObjectivesViewModel>();
        RulesVM = App.GetService<RulesViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
        await RulesVM.LoadCommand.ExecuteAsync(null);
    }

    private async void MarkComplete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            await ViewModel.MarkCompleteCommand.ExecuteAsync(objectiveId);
        }
    }

    private async void DeleteObjective_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            // Show confirmation dialog
            var dialog = new ContentDialog
            {
                Title = "Delete Objective",
                Content = "Delete this objective? This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteObjectiveCommand.ExecuteAsync(objectiveId);
            }
        }
    }

    private async void SetPriority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            await ViewModel.SetPriorityCommand.ExecuteAsync(objectiveId);
        }
    }

    private void ViewObjectiveGames_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            ViewModel.ViewGamesCommand.Execute(objectiveId);
        }
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            await RulesVM.ToggleRuleCommand.ExecuteAsync(ruleId);
        }
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Guardrail",
                Content = "Delete this guardrail? This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await RulesVM.DeleteRuleCommand.ExecuteAsync(ruleId);
            }
        }
    }
}
