#nullable enable

using System.Linq;
using Microsoft.UI.Text;
using Revu.App.Contracts;
using Revu.App.Dialogs;
using Revu.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.Services;

/// <summary>
/// Service for showing modal ContentDialogs.
/// </summary>
public sealed class DialogService : IDialogService
{
    private XamlRoot? _xamlRoot;
    private readonly TaskCompletionSource _initializedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PreGameDialog? LastPreGameDialog { get; private set; }

    public void Initialize(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot;
        _initializedTcs.TrySetResult();
    }

    public async Task<ContentDialogResult> ShowPreGameDialogAsync()
    {
        var dialog = new PreGameDialog();
        await PrepareDialogAsync(dialog);
        dialog.RequestedTheme = ElementTheme.Dark;
        LastPreGameDialog = dialog;
        return await dialog.ShowAsync();
    }

    public async Task<ContentDialogResult> ShowGameReviewDialogAsync(long gameId)
    {
        var dialog = new GameReviewDialog();
        await PrepareDialogAsync(dialog);
        dialog.RequestedTheme = ElementTheme.Dark;
        dialog.LoadGame(gameId);

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            dialog.Save();
        }

        return result;
    }

    public async Task<ContentDialogResult> ShowManualEntryDialogAsync()
    {
        // v1 ships a placeholder text dialog here; ManualEntryPage is the
        // full-form path users actually navigate to. This dialog only
        // exists for code paths that historically called the dialog
        // version. v2.17 backlog will either remove the dialog entry
        // point or wire up a real ManualEntryDialog control.
        var dialog = CreateDialog("Manual Game Entry", "Enter game details manually.");
        await PrepareDialogAsync(dialog);
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";
        return await dialog.ShowAsync();
    }

    public async Task<ContentDialogResult> ShowSessionDebriefDialogAsync(string date)
    {
        // v1 ships a placeholder confirm dialog; the full debrief flow is
        // SessionLoggerPage (sidebar nav). This dialog is the prompt that
        // points users there. v2.17 backlog: either inline the rating
        // capture here or remove the dialog.
        var dialog = CreateDialog("Session Debrief", $"How was your session on {date}?");
        await PrepareDialogAsync(dialog);
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Skip";
        return await dialog.ShowAsync();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        await PrepareDialogAsync(dialog);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        await PrepareDialogAsync(dialog);
        dialog.PrimaryButtonText = "Yes";
        dialog.CloseButtonText = "No";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<IReadOnlyList<MissedGameCandidate>> ShowMissedGamesSelectionAsync(IReadOnlyList<MissedGameCandidate> games)
    {
        if (games.Count == 0)
        {
            return [];
        }

        var dialog = new ContentDialog
        {
            Title = games.Count == 1 ? "Missed Game Found" : "Missed Games Found",
            PrimaryButtonText = "Ingest Selected",
            CloseButtonText = games.Count == 1 ? "Dismiss" : "Dismiss All",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        await PrepareDialogAsync(dialog);

        var introText = new TextBlock
        {
            Text = games.Count == 1
                ? "The app found 1 recent finished game that is not in your history yet. Ingest it for review, or dismiss it so it is not offered again."
                : $"The app found {games.Count} recent finished games that are not in your history yet. Choose which ones to ingest. Unchecked or dismissed games will not be offered again automatically.",
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var gamePanel = new StackPanel { Spacing = 8 };
        var checkboxes = new List<CheckBox>();

        void UpdatePrimaryState()
        {
            dialog.IsPrimaryButtonEnabled = checkboxes.Any(cb => cb.IsChecked == true);
        }

        foreach (var game in games.OrderByDescending(g => g.Timestamp))
        {
            var checkbox = new CheckBox
            {
                IsChecked = true,
                Tag = game,
                Content = BuildMissedGameCard(game),
            };

            checkbox.Checked += (_, _) => UpdatePrimaryState();
            checkbox.Unchecked += (_, _) => UpdatePrimaryState();
            checkboxes.Add(checkbox);
            gamePanel.Children.Add(checkbox);
        }

        dialog.IsPrimaryButtonEnabled = checkboxes.Count > 0;
        dialog.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                introText,
                new ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = gamePanel,
                },
            },
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return [];
        }

        return [.. checkboxes
            .Where(cb => cb.IsChecked == true)
            .Select(cb => cb.Tag)
            .OfType<MissedGameCandidate>()];
    }

    private ContentDialog CreateDialog(string title, string content)
    {
        return new ContentDialog
        {
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
    }

    private async Task PrepareDialogAsync(ContentDialog dialog)
    {
        if (_xamlRoot is null)
        {
            await _initializedTcs.Task.ConfigureAwait(true);
        }

        if (_xamlRoot is not null)
        {
            dialog.XamlRoot = _xamlRoot;
        }
    }

    private static UIElement BuildMissedGameCard(MissedGameCandidate candidate)
    {
        var game = candidate.Stats;
        var title = $"{game.ChampionName} {(game.Win ? "Win" : "Loss")}";
        var subtitle = string.IsNullOrWhiteSpace(game.DatePlayed)
            ? game.DisplayGameMode
            : $"{game.DatePlayed}  ·  {game.DisplayGameMode}";
        var detail = $"{game.Kills}/{game.Deaths}/{game.Assists} KDA  ·  {game.DurationFormatted}";

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(24, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    },
                    new TextBlock
                    {
                        Text = detail,
                        Opacity = 0.75,
                    },
                },
            },
        };
    }
}
