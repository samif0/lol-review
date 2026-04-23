#nullable enable

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.App.ViewModels;
using Revu.Core.Lcu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>
    /// Build a tiny mono-font section label with a violet leading bar,
    /// used inside proposal cards for "WHEN TO DO IT" / "HOW YOU'LL KNOW"
    /// subsections. Kept visually lighter than the card title so it reads
    /// as a label, not a heading.
    /// </summary>
    private static StackPanel BuildFieldLabel(string text, FontFamily monoFont, SolidColorBrush accent)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, -2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 10,
            Height = 1,
            Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = monoFont,
            FontSize = 9,
            CharacterSpacing = 300,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private System.Threading.Tasks.TaskCompletionSource<bool>? _proposalsTcs;

    private System.Threading.Tasks.Task ShowProposalsDialog(CoachGenerateObjectiveResponse result)
    {
        var panel = new StackPanel { Spacing = 12 };

        var primaryBrush = (SolidColorBrush)Application.Current.Resources["PrimaryTextBrush"];
        var secondaryBrush = (SolidColorBrush)Application.Current.Resources["SecondaryTextBrush"];
        var mutedBrush = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        var subtleBorder = (SolidColorBrush)Application.Current.Resources["SubtleBorderBrush"];
        var accentBrush = (SolidColorBrush)Application.Current.Resources["AccentPurpleBrush"];
        var cardBg = (SolidColorBrush)Application.Current.Resources["SurfaceInsetBrush"];
        var shellCanvasBrush = (SolidColorBrush)Application.Current.Resources["ShellCanvasBrush"];
        var displayFont = (FontFamily)Application.Current.Resources["DisplayFont"];
        var monoFont = (FontFamily)Application.Current.Resources["MonoFont"];

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

        int cardIndex = 0;
        foreach (var proposal in result.Proposals)
        {
            cardIndex++;

            // Outer frame holds the inner card; outer corner accents
            // give each proposal the same HUD-card vibe as the modal
            // itself.
            var card = new Border
            {
                BorderBrush = subtleBorder,
                Background = cardBg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(16, 14, 16, 14),
            };
            var cardStack = new StackPanel { Spacing = 10 };

            // Breadcrumb eyebrow: "proposal 01 of 03"
            var eyebrowStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            eyebrowStack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 18,
                Height = 1,
                Fill = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            eyebrowStack.Children.Add(new TextBlock
            {
                Text = $"PROPOSAL {cardIndex:D2} / {result.Proposals.Count:D2}",
                FontFamily = monoFont,
                FontSize = 9,
                CharacterSpacing = 300,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            cardStack.Children.Add(eyebrowStack);

            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Title,
                FontFamily = displayFont,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = proposal.Rationale,
                FontSize = 12,
                Foreground = primaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                LineHeight = 18,
            });

            // Trigger — the in-game cue. Label + body.
            if (!string.IsNullOrWhiteSpace(proposal.Trigger))
            {
                cardStack.Children.Add(BuildFieldLabel("WHEN TO DO IT", monoFont, accentBrush));
                cardStack.Children.Add(new TextBlock
                {
                    Text = proposal.Trigger,
                    FontSize = 12,
                    Foreground = primaryBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.88,
                    LineHeight = 18,
                });
            }

            // Success criteria — how the player self-verifies post-game.
            if (!string.IsNullOrWhiteSpace(proposal.SuccessCriteria))
            {
                cardStack.Children.Add(BuildFieldLabel("HOW YOU'LL KNOW", monoFont, accentBrush));
                cardStack.Children.Add(new TextBlock
                {
                    Text = proposal.SuccessCriteria,
                    FontSize = 12,
                    Foreground = primaryBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.88,
                    LineHeight = 18,
                });
            }

            // Confidence as a mini progress bar + small label to the
            // side — feels more HUD than a plain "confidence: 40%".
            var confRow = new Grid();
            confRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            confRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var confTrack = new Border
            {
                Height = 3,
                Background = shellCanvasBrush,
                BorderBrush = subtleBorder,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            var confTrackGrid = new Grid();
            var confFill = new Border
            {
                Background = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            confTrackGrid.Loaded += (_, _) =>
            {
                // Set the width of the fill to a percentage of the track.
                // We use SizeChanged on the track parent to keep it
                // responsive to modal resizes.
                void ResizeFill()
                {
                    var w = confTrack.ActualWidth;
                    if (w > 2)
                    {
                        confFill.Width = System.Math.Max(0, (w - 2) * System.Math.Clamp(proposal.Confidence, 0, 1));
                    }
                }
                ResizeFill();
                confTrack.SizeChanged += (_, _) => ResizeFill();
            };
            confTrackGrid.Children.Add(confFill);
            confTrack.Child = confTrackGrid;
            Grid.SetColumn(confTrack, 0);
            confRow.Children.Add(confTrack);

            var confLabel = new TextBlock
            {
                Text = $"CONFIDENCE // {proposal.Confidence:P0}",
                FontFamily = monoFont,
                FontSize = 9,
                CharacterSpacing = 200,
                Foreground = mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(confLabel, 1);
            confRow.Children.Add(confLabel);

            cardStack.Children.Add(confRow);

            var useBtn = new Button
            {
                Content = "USE THIS",
                Style = (Style)Application.Current.Resources["PrimaryActionButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(18, 4, 18, 4),
                Height = 32,
                FontSize = 11,
                CharacterSpacing = 200,
                FontFamily = monoFont,
            };
            var localProposal = proposal;
            useBtn.Click += (_, _) =>
            {
                ViewModel.NewTitle = localProposal.Title;
                // Compose a rich description so the trigger and success
                // criteria are persisted alongside the rationale rather
                // than lost after the modal closes.
                var description = new System.Text.StringBuilder();
                description.Append(localProposal.Rationale);
                if (!string.IsNullOrWhiteSpace(localProposal.Trigger))
                {
                    description.Append("\n\nWhen to do it: ").Append(localProposal.Trigger);
                }
                if (!string.IsNullOrWhiteSpace(localProposal.SuccessCriteria))
                {
                    description.Append("\n\nHow you'll know: ").Append(localProposal.SuccessCriteria);
                }
                ViewModel.NewDescription = description.ToString();
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

        ProposalsModal.Eyebrow = "COACH // PROPOSALS";
        ProposalsModal.Title = "Pick an objective";
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
