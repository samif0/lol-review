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

    private System.Threading.Tasks.TaskCompletionSource<bool>? _proposalsTcs;

    private System.Threading.Tasks.Task ShowProposalsDialog(CoachGenerateObjectiveResponse result)
    {
        var panel = new StackPanel { Spacing = 12 };

        var primaryBrush = (SolidColorBrush)Application.Current.Resources["PrimaryTextBrush"];
        var secondaryBrush = (SolidColorBrush)Application.Current.Resources["SecondaryTextBrush"];
        var subtleBorder = (SolidColorBrush)Application.Current.Resources["SubtleBorderBrush"];
        var accentBrush = (SolidColorBrush)Application.Current.Resources["AccentPurpleBrush"];
        var cardBg = (SolidColorBrush)Application.Current.Resources["SurfaceInsetBrush"];

        // Provider tag line, tiny and quiet.
        panel.Children.Add(new TextBlock
        {
            Text = $"[{result.Provider} / {result.Model} / {result.LatencyMs}ms]",
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = secondaryBrush,
            Opacity = 0.6,
        });

        if (result.Proposals.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "The coach didn't find enough patterns in your reviews to propose a specific objective. Try writing a few more detailed review notes and run this again.",
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            });
        }

        foreach (var proposal in result.Proposals)
        {
            var card = new Border
            {
                BorderBrush = subtleBorder,
                Background = cardBg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(14, 12, 14, 12),
            };
            var cardStack = new StackPanel { Spacing = 8 };

            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Rationale,
                FontSize = 12,
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = $"confidence: {proposal.Confidence:P0}",
                FontSize = 10,
                Foreground = accentBrush,
                FontFamily = new FontFamily("Consolas"),
                CharacterSpacing = 200,
                Opacity = 0.8,
            });

            var useBtn = new Button
            {
                Content = "Use this",
                Style = (Style)Application.Current.Resources["PrimaryActionButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(14, 4, 14, 4),
                Height = 32,
                FontSize = 12,
            };
            var localProposal = proposal;
            useBtn.Click += (_, _) =>
            {
                // Pre-fill the create form and close the modal.
                ViewModel.NewTitle = localProposal.Title;
                ViewModel.NewDescription = localProposal.Rationale;
                if (!ViewModel.IsCreating)
                {
                    ViewModel.ToggleCreateFormCommand.Execute(null);
                }
                CloseProposalsModal();
            };
            cardStack.Children.Add(useBtn);

            card.Child = cardStack;
            panel.Children.Add(card);
        }

        ProposalsModal.Title = "COACH PROPOSALS";
        ProposalsModal.Body = panel;
        ProposalsModal.Footer = null;
        ProposalsModal.IsOpen = true;

        // Return a task that completes when the modal closes, so the
        // caller can await showing it (matches the old ContentDialog
        // call-site expectations).
        _proposalsTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        return _proposalsTcs.Task;
    }

    private void CloseProposalsModal()
    {
        ProposalsModal.IsOpen = false;
        // Resolve whichever task opened the modal. ShowProposalsDialog
        // and ShowInfoDialog both use the same HudModal instance.
        _proposalsTcs?.TrySetResult(true);
        _proposalsTcs = null;
        _infoTcs?.TrySetResult(true);
        _infoTcs = null;
    }

    private void OnProposalsModalClose(object sender, System.EventArgs e)
    {
        CloseProposalsModal();
    }

    private System.Threading.Tasks.TaskCompletionSource<bool>? _infoTcs;

    private System.Threading.Tasks.Task ShowInfoDialog(string title, string body)
    {
        var primaryBrush = (SolidColorBrush)Application.Current.Resources["PrimaryTextBrush"];

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = primaryBrush,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });

        var okBtn = new Button
        {
            Content = "OK",
            Style = (Style)Application.Current.Resources["QuietActionButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 4, 18, 4),
            Height = 32,
            FontSize = 12,
        };
        okBtn.Click += (_, _) =>
        {
            ProposalsModal.IsOpen = false;
            _infoTcs?.TrySetResult(true);
            _infoTcs = null;
        };

        ProposalsModal.Title = title.ToUpperInvariant();
        ProposalsModal.Body = panel;
        ProposalsModal.Footer = okBtn;
        ProposalsModal.IsOpen = true;

        _infoTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        return _infoTcs.Task;
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
