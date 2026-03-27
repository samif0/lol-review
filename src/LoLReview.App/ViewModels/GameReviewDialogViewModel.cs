#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

/// <summary>
/// Thin wrapper ViewModel for the GameReviewDialog (post-game popup).
/// Delegates to ReviewViewModel for the actual review logic.
/// </summary>
public partial class GameReviewDialogViewModel : ObservableObject
{
    private readonly ILogger<GameReviewDialogViewModel> _logger;

    [ObservableProperty]
    private long _gameId;

    public GameReviewDialogViewModel(ILogger<GameReviewDialogViewModel> logger)
    {
        _logger = logger;
    }
}
