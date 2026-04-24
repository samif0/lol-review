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
///   "welcome"    — hero + "Start using Revu" (primary) / "Log in with email" (secondary)
///   "emailEntry" — email input for the optional login path
///   "codeSent"   — paste emailed OTP (login path only)
///   "account"    — Riot ID + region (login path only)
///   "role"       — primary-role pick; last screen before "done"
///   "done"       — onboarding host swaps in the shell
///
/// The old multi-screen tour (tourWhat/tourLoop/tourAscent/tourHabits/tourObjective)
/// has been cut. Contextual prompts now live on the Dashboard — see
/// DashboardViewModel's Stage / NextStep / Ascent-reminder properties.
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

    // True once the user has chosen the login path. Controls which
    // OnboardingSkipped value we stamp when role-pick finishes.
    private bool _chosenLoginPath;

    // ── Email / auth ────────────────────────────────────────────────

    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _otpCode = "";

    // ── Riot account (logged-in only) ───────────────────────────────

    [ObservableProperty]
    private string _riotId = "";

    [ObservableProperty]
    private string _riotRegion = "na1";

    // ── Role ────────────────────────────────────────────────────────

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

    /// <summary>Fires when the flow completes (with or without auth).</summary>
    public event Action? Completed;

    // ── Welcome ──────────────────────────────────────────────────────

    /// <summary>Primary action: skip auth and go straight to role pick.</summary>
    [RelayCommand]
    private void StartUsingRevu()
    {
        _chosenLoginPath = false;
        State = "role";
        Error = "";
    }

    /// <summary>Secondary action: reveal email input for the login path.</summary>
    [RelayCommand]
    private void BeginLogin()
    {
        _chosenLoginPath = true;
        State = "emailEntry";
        Error = "";
    }

    [RelayCommand]
    private void BackToWelcome()
    {
        State = "welcome";
        Email = "";
        OtpCode = "";
        Error = "";
        Info = "";
        _chosenLoginPath = false;
    }

    // ── Email entry + OTP ───────────────────────────────────────────

    [RelayCommand]
    private async Task SendLoginCodeAsync()
    {
        if (Busy) return;
        Error = "";
        var email = (Email ?? "").Trim();
        if (string.IsNullOrEmpty(email))
        {
            Error = "Enter an email to continue.";
            return;
        }

        Busy = true;
        try
        {
            await _authClient.LoginAsync(email);
            Info = $"Check {email} for a code.";
            State = "codeSent";
            OtpCode = "";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding send-login-code failed");
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
    private void BackToEmailEntry()
    {
        State = "emailEntry";
        OtpCode = "";
        Error = "";
        Info = "";
    }

    // ── Riot ID + region (logged-in only) ───────────────────────────

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

    // ── Role (final step — stamps OnboardingSkipped + fires Completed) ─

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

            // Skip-path users need OnboardingSkipped=true so the gate stops
            // firing. Login-path users already had it set false during
            // FinishAccountAsync — we have RiotProxyEnabled + PrimaryRole,
            // which is the other branch of OnboardingComplete.
            if (!_chosenLoginPath)
            {
                config.OnboardingSkipped = true;
            }
            await _configService.SaveAsync(config);

            State = "done";
            Error = "";
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding finish-role failed");
            Error = "Couldn't save your role.";
        }
        finally { Busy = false; }
    }
}
