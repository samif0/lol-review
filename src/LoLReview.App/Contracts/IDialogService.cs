#nullable enable

using LoLReview.App.Dialogs;
using LoLReview.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Contracts;

/// <summary>
/// Service for showing modal dialogs (pre-game, post-game review, manual entry, etc.).
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Set the XamlRoot used for dialog positioning. Must be called after the shell loads.
    /// </summary>
    void Initialize(Microsoft.UI.Xaml.XamlRoot xamlRoot);

    /// <summary>Show the pre-game objectives/rules dialog during champion select.</summary>
    Task<ContentDialogResult> ShowPreGameDialogAsync();

    /// <summary>The last shown PreGameDialog instance (for reading mood/focus after closing).</summary>
    PreGameDialog? LastPreGameDialog { get; }

    /// <summary>Show the post-game review dialog after a match ends.</summary>
    Task<ContentDialogResult> ShowGameReviewDialogAsync(long gameId);

    /// <summary>Show the manual game entry dialog.</summary>
    Task<ContentDialogResult> ShowManualEntryDialogAsync();

    /// <summary>Show the end-of-session debrief dialog.</summary>
    Task<ContentDialogResult> ShowSessionDebriefDialogAsync(string date);

    /// <summary>Show a simple informational message.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Show a yes/no confirmation dialog. Returns true if user confirmed.</summary>
    Task<bool> ShowConfirmAsync(string title, string message);

    /// <summary>Show a selectable list of recent unsaved games and return the chosen games to ingest.</summary>
    Task<IReadOnlyList<MissedGameCandidate>> ShowMissedGamesSelectionAsync(IReadOnlyList<MissedGameCandidate> games);
}
