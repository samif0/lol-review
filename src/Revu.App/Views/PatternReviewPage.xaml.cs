#nullable enable

using System;
using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Revu.App.Views;

/// <summary>
/// Cross-game Pattern Review viewer. Walks a pattern's moments as a playlist;
/// the shared <see cref="Controls.VodSurface"/> plays each moment's VOD,
/// switching recordings transparently when the next moment is in another game.
/// </summary>
public sealed partial class PatternReviewPage : Page
{
    private long _loadedGameId;

    public PatternReviewViewModel ViewModel { get; }

    public PatternReviewPage()
    {
        ViewModel = App.GetService<PatternReviewViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.MomentActivated += OnMomentActivated;
        Loaded += (_, _) => AnimationHelper.AnimatePageEnter(RootGrid);
        Unloaded += (_, _) =>
        {
            ViewModel.MomentActivated -= OnMomentActivated;
            Surface.Pause();
            // Flush any pending note + auto-clip for the moment on screen.
            _ = ViewModel.CommitPendingAsync();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ObjectivePatternCard card)
        {
            ViewModel.LoadCommand.Execute(card);
        }
    }

    private async void OnMomentActivated(PatternMomentItem moment)
    {
        try
        {
            if (!moment.HasVod)
            {
                // No recording for this game — surface shows its own empty state.
                if (moment.GameId != _loadedGameId)
                {
                    _loadedGameId = moment.GameId;
                    await Surface.LoadAsync("");
                }
                return;
            }

            if (moment.GameId != _loadedGameId)
            {
                _loadedGameId = moment.GameId;
                await Surface.LoadAsync(moment.VodPath);
            }

            // Seek to the moment (queues internally until the media is ready).
            Surface.SeekTo(Math.Max(0, moment.StartTimeSeconds));
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteCrash($"[PatternReviewPage] OnMomentActivated failed: {ex}");
        }
    }

    private void OnMomentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PatternMomentItem item })
        {
            ViewModel.SelectMomentCommand.Execute(item);
        }
    }
}
