#nullable enable

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Revu.App.Dialogs;

/// <summary>
/// End Block — the post-session counterpart of StartBlockDialog. Closes the
/// daily loop: shows the intent locked at Start Block, asks "did you stick to
/// it?" (1-10), and captures one debrief line that tomorrow's Start Block
/// resurfaces. Writes sessions.debrief_rating / debrief_note / ended_at via
/// ISessionLogRepository.SaveSessionDebriefAsync.
/// </summary>
public sealed partial class EndBlockDialog : ContentDialog, INotifyPropertyChanged
{
    private readonly ISessionLogRepository _sessionLogRepo;

    public string IntentionText { get; private set; } = "";
    public bool HasIntention => !string.IsNullOrWhiteSpace(IntentionText);

    /// <summary>True once the user committed the debrief (vs. cancelled).</summary>
    public bool Saved { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EndBlockDialog()
    {
        _sessionLogRepo = App.GetService<ISessionLogRepository>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            var session = await _sessionLogRepo.GetSessionAsync(today);
            IntentionText = session?.Intention?.Trim() ?? "";

            // Re-opening an already-closed block pre-fills the prior answer so
            // the user edits rather than re-types.
            if (session is not null && session.DebriefRating > 0)
            {
                RatingSlider.Value = Math.Clamp(session.DebriefRating, 1, 10);
                NoteBox.Text = session.DebriefNote ?? "";
            }

            var stats = await _sessionLogRepo.GetStatsForDateAsync(today);
            TodayLineText.Text = stats.Games > 0
                ? $"{stats.Wins}W {stats.Losses}L"
                : "NO GAMES";

            OnPropertyChanged(nameof(IntentionText));
            OnPropertyChanged(nameof(HasIntention));
            Bindings.Update();
        }
        catch
        {
            // non-fatal — the dialog still works as a blank debrief form
        }
    }

    private void OnRatingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RatingValueText is not null)
        {
            RatingValueText.Text = ((int)e.NewValue).ToString();
        }
    }

    private async void OnCloseBlockClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.IsEnabled = false;
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var rating = (int)RatingSlider.Value;
            var note = NoteBox.Text.Trim();
            await _sessionLogRepo.SaveSessionDebriefAsync(today, rating, note);
            Saved = true;
        }
        catch
        {
            // non-fatal — closing the dialog without persisting beats trapping
            // the user in a broken ritual
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
