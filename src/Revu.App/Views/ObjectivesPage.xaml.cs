#nullable enable

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

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

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ObjectivesViewModel.ShowCelebration)
                && ViewModel.ShowCelebration)
            {
                CelebrationEnterStoryboard.Begin();
            }
        };

        // Practice counts / objective levels are derived from games — a
        // delete elsewhere invalidates them, so reload on notification.
        WeakReferenceMessenger.Default.Register<ObjectivesPage, GameDeletedMessage>(
            this, async (r, _) => await r.ViewModel.LoadCommand.ExecuteAsync(null));
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        await ViewModel.LoadCommand.ExecuteAsync(null);
        await RulesVM.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// When the Dashboard's "SET OBJECTIVE" prompt navigates here with an
    /// <see cref="ObjectiveSuggestion"/>, pre-fill the create form so the
    /// user sees the suggested objective instead of a blank page.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ObjectiveSuggestion suggestion)
        {
            ViewModel.NewTitle = suggestion.Title;
            ViewModel.NewSkillArea = suggestion.SkillArea;
            ViewModel.NewCriteria = suggestion.CompletionCriteria;
            ViewModel.NewDescription = suggestion.Description;
            ViewModel.NewTypeIndex = string.Equals(suggestion.Type, "mental", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (!ViewModel.IsCreating)
            {
                ViewModel.ToggleCreateFormCommand.Execute(null);
            }
        }
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

    private async void EditObjective_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            await ViewModel.BeginEditObjectiveCommand.ExecuteAsync(objectiveId);
        }
    }

    private void ViewObjectiveGames_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            ViewModel.ViewGamesCommand.Execute(objectiveId);
        }
    }

    private void ViewObjectiveNotes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long objectiveId)
        {
            ViewModel.ViewNotesCommand.Execute(objectiveId);
        }
    }

    private async void ObjectivePhase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not ObjectiveDisplayItem item)
        {
            return;
        }

        var phase = Revu.Core.Data.Repositories.ObjectivePhases.FromIndex(combo.SelectedIndex);
        if (string.Equals(
                phase,
                Revu.Core.Data.Repositories.ObjectivePhases.Normalize(item.Phase),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ViewModel.UpdateObjectivePhaseCommand.ExecuteAsync(new ObjectivePhaseUpdateRequest(item.Id, phase));
    }

    private async void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            await RulesVM.ToggleRuleCommand.ExecuteAsync(ruleId);
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long ruleId)
        {
            RulesVM.StartEditingCommand.Execute(ruleId);
        }
    }

    private void RemovePromptDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PromptDraftItem draft)
        {
            ViewModel.RemovePromptCommand.Execute(draft);
        }
    }

    // v2.15.0: picked-chip remove handler (click an already-added chip to remove it).
    private void RemoveChampionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string champ)
        {
            ViewModel.RemoveChampionCommand.Execute(champ);
        }
    }

    // v2.15.1: AutoSuggestBox type-to-filter replaces the champion-grid UI.
    private void ChampionSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only update the dropdown list when the text change is from user typing,
        // not programmatic (e.g. when we clear after a selection).
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = (sender.Text ?? "").Trim();
        var source = ViewModel.AllChampionNames;
        if (source.Count == 0)
        {
            sender.ItemsSource = Array.Empty<string>();
            return;
        }

        // Hide champs the user already picked; filter the rest by substring
        // (case-insensitive). Prefix matches sort first so "Kai" shows Kai'Sa
        // above Kayle.
        var already = ViewModel.NewChampions
            .Select(n => n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> filtered = source
            .Where(n => !already.Contains(n));

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered
                .Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            filtered = filtered.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        sender.ItemsSource = filtered.Take(50).ToList();
    }

    private void ChampionSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string champ)
        {
            ViewModel.AddChampionCommand.Execute(champ);
            sender.Text = "";
        }
    }

    private void ChampionSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter pressed. If a suggestion was chosen use that; otherwise fall
        // back to the literal query (user typing a champ they haven't played).
        var name = args.ChosenSuggestion as string
                   ?? (args.QueryText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        ViewModel.AddChampionCommand.Execute(name);
        sender.Text = "";
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
