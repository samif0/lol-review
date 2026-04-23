#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>
/// Viewmodel for the first-launch onboarding flow.
///
/// States (<see cref="State"/>):
///   "welcome"  — email + optional invite code
///   "codeSent" — paste OTP
///   "account"  — Riot ID + region + validate via /account
///   "role"     — pick main role (TOP / JUNGLE / MIDDLE / BOTTOM / UTILITY)
///   "done"     — onboarding host swaps in the shell
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IRiotAuthClient _authClient;
    private readonly ILogger<OnboardingViewModel> _logger;

    public OnboardingViewModel(
        IConfigService configService,
        IRiotAuthClient authClient,
        ILogger<OnboardingViewModel> logger)
    {
        _configService = configService;
        _authClient = authClient;
        _logger = logger;
    }

    // ── State machine ───────────────────────────────────────────────

    [ObservableProperty]
    private string _state = "welcome";

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _info = "";

    // ── Welcome ────────────────────────────────────────────────────

    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _inviteCode = "";

    [ObservableProperty]
    private bool _showInviteField;

    // ── Code-sent ───────────────────────────────────────────────────

    [ObservableProperty]
    private string _otpCode = "";

    // ── Account (after login) ───────────────────────────────────────

    [ObservableProperty]
    private string _riotId = "";

    [ObservableProperty]
    private string _riotRegion = "na1";

    // ── Role (after account) ────────────────────────────────────────

    /// <summary>Riot internal role code: TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY. Empty until chosen.</summary>
    [ObservableProperty]
    private string _primaryRole = "";

    public bool IsTopSelected => PrimaryRole == "TOP";
    public bool IsJungleSelected => PrimaryRole == "JUNGLE";
    public bool IsMiddleSelected => PrimaryRole == "MIDDLE";
    public bool IsBottomSelected => PrimaryRole == "BOTTOM";
    public bool IsUtilitySelected => PrimaryRole == "UTILITY";

    partial void OnPrimaryRoleChanged(string value)
    {
        OnPropertyChanged(nameof(IsTopSelected));
        OnPropertyChanged(nameof(IsJungleSelected));
        OnPropertyChanged(nameof(IsMiddleSelected));
        OnPropertyChanged(nameof(IsBottomSelected));
        OnPropertyChanged(nameof(IsUtilitySelected));
    }

    /// <summary>Fires when the flow completes (successfully or via skip).</summary>
    public event Action? Completed;

    [RelayCommand]
    private void ToggleInviteField()
    {
        ShowInviteField = !ShowInviteField;
        if (!ShowInviteField) InviteCode = "";
    }

    [RelayCommand]
    private async Task ContinueFromWelcomeAsync()
    {
        if (Busy) return;
        Error = "";
        var email = (Email ?? "").Trim();
        var invite = (InviteCode ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(email))
        {
            Error = "Enter an email to continue.";
            return;
        }

        Busy = true;
        try
        {
            if (!string.IsNullOrEmpty(invite))
            {
                await _authClient.SignupAsync(email, invite);
            }
            else
            {
                await _authClient.LoginAsync(email);
            }
            Info = $"Check {email} for a code.";
            State = "codeSent";
            OtpCode = "";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding welcome continue failed");
            Error = "Couldn't reach the server. Check your connection.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (Busy) return;
        Error = "";
        var code = (OtpCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            Error = "Paste the code from your email.";
            return;
        }

        Busy = true;
        try
        {
            var result = await _authClient.VerifyAsync(code);
            var config = await _configService.LoadAsync();
            config.RiotSessionToken = result.SessionToken;
            config.RiotSessionEmail = (Email ?? "").Trim();
            config.RiotSessionExpiresAt = result.ExpiresAt;
            await _configService.SaveAsync(config);

            State = "account";
            Error = "";
            Info = "";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding verify failed");
            Error = "Couldn't verify the code.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void BackToWelcome()
    {
        State = "welcome";
        OtpCode = "";
        Error = "";
        Info = "";
    }

    [RelayCommand]
    private async Task FinishAccountAsync()
    {
        if (Busy) return;
        Error = "";
        var id = (RiotId ?? "").Trim();
        var region = (RiotRegion ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id) || !id.Contains('#') || id.StartsWith('#') || id.EndsWith('#'))
        {
            Error = "Enter your Riot ID as gameName#tagLine.";
            return;
        }
        if (string.IsNullOrEmpty(region))
        {
            Error = "Pick a region.";
            return;
        }

        Busy = true;
        try
        {
            var config = await _configService.LoadAsync();
            var session = config.RiotSessionToken;
            if (string.IsNullOrEmpty(session))
            {
                Error = "Session missing. Start over.";
                State = "welcome";
                return;
            }

            var account = await _authClient.ResolveAccountAsync(session, id, region);

            config.RiotId = id;
            config.RiotRegion = region;
            config.RiotPuuid = account.Puuid;
            config.OnboardingSkipped = false;
            await _configService.SaveAsync(config);

            State = "role";
            Error = "";
            Info = "";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding finish-account failed");
            Error = "Couldn't validate that account.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void SelectRole(string role)
    {
        PrimaryRole = role;
        Error = "";
    }

    [RelayCommand]
    private async Task FinishRoleAsync()
    {
        if (Busy) return;
        if (string.IsNullOrEmpty(PrimaryRole))
        {
            Error = "Pick the role you play most.";
            return;
        }

        Busy = true;
        try
        {
            var config = await _configService.LoadAsync();
            config.PrimaryRole = PrimaryRole;
            await _configService.SaveAsync(config);

            State = "done";
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding finish-role failed");
            Error = "Couldn't save your role.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var config = await _configService.LoadAsync();
            config.OnboardingSkipped = true;
            await _configService.SaveAsync(config);
            Completed?.Invoke();
        }
        finally { Busy = false; }
    }
}
