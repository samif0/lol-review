#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.Core.Lcu;

namespace LoLReview.App.ViewModels;

/// <summary>
/// ViewModel for the main shell / navigation frame.
/// </summary>
public partial class ShellViewModel : ObservableRecipient,
    IRecipient<LcuConnectionChangedMessage>,
    IRecipient<ChampSelectStartedMessage>,
    IRecipient<GameEndedMessage>
{
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatusText = "Waiting for League...";

    public ShellViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;

        // Activate the messenger so we receive messages
        IsActive = true;
    }

    [RelayCommand]
    private void Navigate(string pageKey)
    {
        _navigationService.NavigateTo(pageKey);
    }

    public void Receive(LcuConnectionChangedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            IsConnected = message.IsConnected;
            ConnectionStatusText = message.IsConnected ? "Connected" : "Waiting for League...";
        });
    }

    public void Receive(ChampSelectStartedMessage message)
    {
        // Navigate to session logger when champ select starts
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            _navigationService.NavigateTo("session");
        });
    }

    public void Receive(GameEndedMessage message)
    {
        // Could show post-game review dialog or navigate
        // For now, just navigate to session page
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            _navigationService.NavigateTo("session");
        });
    }
}
