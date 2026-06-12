#nullable enable

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Revu.App.Dialogs;

/// <summary>
/// v2.16: Start Block — short pre-queue ritual that fires earlier than
/// PreGameDialog (which is locked in once champ-select starts). Asks for a
/// session goal, surfaces the user's priority objective, and runs a 30-second
/// countdown so the user enters queue with focused intent.
///
/// Trigger is currently manual (button on Dashboard). Auto-trigger from LCU
/// home-state detection is in scope but punted to a follow-up.
/// </summary>
public sealed partial class StartBlockDialog : ContentDialog, INotifyPropertyChanged
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private CancellationTokenSource? _countdownCts;

    public string PriorityObjectiveTitle { get; private set; } = "";
    public string PriorityObjectiveCriteria { get; private set; } = "";
    public bool HasPriorityObjective => !string.IsNullOrWhiteSpace(PriorityObjectiveTitle);

    public string LastDebriefMeta { get; private set; } = "";
    public string LastDebriefNote { get; private set; } = "";
    public bool HasLastDebrief => !string.IsNullOrWhiteSpace(LastDebriefNote)
        || !string.IsNullOrWhiteSpace(LastDebriefMeta);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The session goal text the user entered. Read after the dialog returns.</summary>
    public string SessionGoal => GoalBox.Text;

    public StartBlockDialog()
    {
        _objectivesRepo = App.GetService<IObjectivesRepository>();
        _sessionLogRepo = App.GetService<ISessionLogRepository>();
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var priority = await _objectivesRepo.GetPriorityAsync();
            if (priority is not null)
            {
                PriorityObjectiveTitle = priority.Title;
                PriorityObjectiveCriteria = string.IsNullOrWhiteSpace(priority.CompletionCriteria)
                    ? ""
                    : $"Success: {priority.CompletionCriteria}";
                OnPropertyChanged(nameof(PriorityObjectiveTitle));
                OnPropertyChanged(nameof(PriorityObjectiveCriteria));
                OnPropertyChanged(nameof(HasPriorityObjective));
                Bindings.Update();
            }
        }
        catch
        {
            // non-fatal — dialog still works without a priority objective
        }

        try
        {
            // Feed-forward edge of the loop: surface the most recent End Block
            // debrief (up to a week back, so weekend gaps don't lose it).
            for (var daysBack = 1; daysBack <= 7; daysBack++)
            {
                var date = DateTime.Now.AddDays(-daysBack);
                var prior = await _sessionLogRepo.GetSessionAsync(date.ToString("yyyy-MM-dd"));
                if (prior is null || (prior.DebriefRating <= 0 && string.IsNullOrWhiteSpace(prior.DebriefNote)))
                {
                    continue;
                }

                var dayLabel = daysBack == 1
                    ? "YESTERDAY"
                    : date.ToString("MMM dd").ToUpperInvariant();
                LastDebriefMeta = prior.DebriefRating > 0
                    ? $"{dayLabel} — STUCK TO IT {prior.DebriefRating}/10"
                    : dayLabel;
                LastDebriefNote = prior.DebriefNote?.Trim() ?? "";
                OnPropertyChanged(nameof(LastDebriefMeta));
                OnPropertyChanged(nameof(LastDebriefNote));
                OnPropertyChanged(nameof(HasLastDebrief));
                Bindings.Update();
                break;
            }
        }
        catch
        {
            // non-fatal — dialog still works without the last debrief card
        }

        StartCountdown();
    }

    private void StartCountdown()
    {
        _countdownCts?.Cancel();
        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;
        _ = RunCountdownAsync(30, token);
    }

    private async Task RunCountdownAsync(int seconds, CancellationToken token)
    {
        for (int remaining = seconds; remaining >= 0 && !token.IsCancellationRequested; remaining--)
        {
            CountdownText.Text = $"0:{remaining:D2}";
            try
            {
                await Task.Delay(1000, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Always cancel the timer when the dialog closes, regardless of route.
        _countdownCts?.Cancel();
    }

    /// <summary>
    /// In-content primary button click. Saves the session goal then dismisses
    /// the dialog with a Primary result so callers can detect commit vs cancel.
    /// Lives outside the ContentDialog button slot because that slot's theme
    /// resources can't be cleanly overridden to match the HUD aesthetic.
    /// </summary>
    private async void OnLockInClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        var goal = GoalBox.Text.Trim();
        if (!string.IsNullOrEmpty(goal))
        {
            try
            {
                var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                await _sessionLogRepo.SetSessionIntentionAsync(dateStr, goal);
            }
            catch
            {
                // non-fatal — SessionGoal still readable by the caller
            }
        }
        Hide();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
