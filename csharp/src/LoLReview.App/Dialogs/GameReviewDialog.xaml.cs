#nullable enable

using LoLReview.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Dialogs;

/// <summary>
/// Post-game review dialog -- thin wrapper that hosts review content in a ContentDialog.
/// Reuses ReviewViewModel for the actual review logic.
/// </summary>
public sealed partial class GameReviewDialog : ContentDialog
{
    public ReviewViewModel ReviewVM { get; }

    public GameReviewDialog()
    {
        ReviewVM = App.GetService<ReviewViewModel>();
        InitializeComponent();
    }

    /// <summary>Load review data for the specified game.</summary>
    public void LoadGame(long gameId)
    {
        ReviewVM.LoadCommand.Execute(gameId);
    }

    /// <summary>Save the review when PrimaryButton is clicked.</summary>
    public void Save()
    {
        ReviewVM.SaveReviewCommand.Execute(null);
    }
}
