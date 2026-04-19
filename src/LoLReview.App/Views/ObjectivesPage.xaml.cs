#nullable enable

using System;
using System.Collections.Generic;
using LoLReview.App.Helpers;
using LoLReview.App.Services;
using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ObjectivesViewModel.ShowCelebration)
                && ViewModel.ShowCelebration)
            {
                CelebrationEnterStoryboard.Begin();
            }
        };
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AnimationHelper.AnimatePageEnter(RootGrid);
        GenerateWithCoachButton.Visibility = CoachFeatureFlag.IsEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;
        await ViewModel.LoadCommand.ExecuteAsync(null);
        await RulesVM.LoadCommand.ExecuteAsync(null);
    }

    private async void OnGenerateObjectiveClick(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn is not null) { btn.IsEnabled = false; btn.Content = "Starting coach..."; }

        try
        {
            // Lazy-start the sidecar if it isn't running yet.
            var sidecar = App.GetService<CoachSidecarService>();
            if (!sidecar.IsHealthy)
            {
                var started = await sidecar.EnsureSidecarRunningAsync();
                if (!started)
                {
                    await ShowInfoDialog(
                        "Coach not available",
                        "Couldn't start the coach sidecar. Make sure the Python environment is set up under coach/.venv and the app has an API key in Settings → AI Coach.");
                    return;
                }
            }

            if (btn is not null) btn.Content = "Thinking...";
            var api = App.GetService<ICoachApiClient>();
            var result = await api.GenerateObjectiveAsync();
            if (result is null || result.Proposals.Count == 0)
            {
                await ShowInfoDialog(
                    "Not enough data yet",
                    "The coach doesn't see enough patterns to propose a new objective. Try after playing more games with reviews written, or check that the sidecar + API key are configured in Settings.");
                return;
            }

            await ShowProposalsDialog(result);
        }
        catch (Exception ex)
        {
            await ShowInfoDialog("Generate failed", ex.Message);
        }
        finally
        {
            if (btn is not null) { btn.IsEnabled = true; btn.Content = "Generate with coach"; }
        }
    }

    private async System.Threading.Tasks.Task ShowProposalsDialog(CoachGenerateObjectiveResponse result)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = $"[{result.Provider} / {result.Model} / {result.LatencyMs}ms]",
            FontSize = 10,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.6,
        });

        CoachObjectiveProposal? chosen = null;
        ContentDialog? dialogRef = null;

        foreach (var proposal in result.Proposals)
        {
            var card = new Border
            {
                BorderBrush = (SolidColorBrush)Application.Current.Resources["SubtleBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(12),
            };
            var cardStack = new StackPanel { Spacing = 6 };

            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Rationale,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = $"confidence: {proposal.Confidence:P0}",
                FontSize = 10,
                Opacity = 0.6,
            });

            var useBtn = new Button
            {
                Content = "Use this",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(12, 4, 12, 4),
            };
            var localProposal = proposal;
            useBtn.Click += (_, _) =>
            {
                chosen = localProposal;
                dialogRef?.Hide();
            };
            cardStack.Children.Add(useBtn);

            card.Child = cardStack;
            panel.Children.Add(card);
        }

        var dialog = new ContentDialog
        {
            Title = "Coach proposals",
            Content = new ScrollViewer { Content = panel, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        dialogRef = dialog;
        await dialog.ShowAsync();

        if (chosen is not null)
        {
            // Pre-fill the create form with the chosen proposal and open it.
            ViewModel.NewTitle = chosen.Title;
            ViewModel.NewDescription = chosen.Rationale;
            if (!ViewModel.IsCreating)
            {
                ViewModel.ToggleCreateFormCommand.Execute(null);
            }
        }
    }

    private async System.Threading.Tasks.Task ShowInfoDialog(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
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

    private async void ObjectivePhase_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not ObjectiveDisplayItem item)
        {
            return;
        }

        var phase = LoLReview.Core.Data.Repositories.ObjectivePhases.FromIndex(combo.SelectedIndex);
        if (string.Equals(
                phase,
                LoLReview.Core.Data.Repositories.ObjectivePhases.Normalize(item.Phase),
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
