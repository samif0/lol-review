#nullable enable

using LoLReview.App.Contracts;
using LoLReview.App.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LoLReview.App.Services;

/// <summary>
/// Service for showing modal ContentDialogs.
/// </summary>
public sealed class DialogService : IDialogService
{
    private XamlRoot? _xamlRoot;

    public PreGameDialog? LastPreGameDialog { get; private set; }

    public void Initialize(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot;
    }

    public async Task<ContentDialogResult> ShowPreGameDialogAsync()
    {
        var dialog = new PreGameDialog();
        if (_xamlRoot is not null)
            dialog.XamlRoot = _xamlRoot;
        dialog.RequestedTheme = ElementTheme.Dark;
        LastPreGameDialog = dialog;
        return await dialog.ShowAsync();
    }

    public async Task<ContentDialogResult> ShowGameReviewDialogAsync(long gameId)
    {
        var dialog = new GameReviewDialog();
        if (_xamlRoot is not null)
            dialog.XamlRoot = _xamlRoot;
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
        // TODO: Replace with actual ManualEntryDialog content
        var dialog = CreateDialog("Manual Game Entry", "Enter game details manually.");
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Cancel";
        return await dialog.ShowAsync();
    }

    public async Task<ContentDialogResult> ShowSessionDebriefDialogAsync(string date)
    {
        // TODO: Replace with actual SessionDebriefDialog content
        var dialog = CreateDialog("Session Debrief", $"How was your session on {date}?");
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Skip";
        return await dialog.ShowAsync();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message);
        dialog.PrimaryButtonText = "Yes";
        dialog.CloseButtonText = "No";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private ContentDialog CreateDialog(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
        };

        if (_xamlRoot is not null)
        {
            dialog.XamlRoot = _xamlRoot;
        }

        // Apply dark theme
        dialog.RequestedTheme = ElementTheme.Dark;

        return dialog;
    }
}
