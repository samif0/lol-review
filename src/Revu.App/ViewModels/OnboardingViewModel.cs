#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>
/// Viewmodel for the first-launch onboarding flow.
///
/// States (<see cref="State"/>):
///   "welcome"       — hero + "Start using Revu" (primary) / "Log in with email" (secondary)
///   "emailEntry"    — email input for the optional login path
///   "codeSent"      — paste emailed OTP (login path only)
///   "account"       — Riot ID + region (login path only)
///   "role"          — primary-role pick (login path only)
///   "tourWhat"      — shared: what Revu is (1/4)
///   "tourLoop"      — shared: auto-capture + review (2/4)
///   "tourHabits"    — shared: objectives + rules (3/4)
///   "tourObjective" — shared: create first objective (4/4, writes to DB)
///   "done"          — onboarding host swaps in the shell
///
/// Both paths (login and skip) converge at "tourWhat" after any auth steps.
/// Skip sets <c>OnboardingSkipped=true</c>; login sets it false. Either way,
/// <c>OnboardingComplete</c> becomes true by the time we fire <c>Completed</c>.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly IRiotAuthClient _authClient;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ILogger<OnboardingViewModel> _logger;

    public OnboardingViewModel(
        IConfigService configService,
        IRiotAuthClient authClient,
        IObjectivesRepository objectivesRepo,
        ILogger<OnboardingViewModel> logger)
    {
        _configService = configService;
        _authClient = authClient;
        _objectivesRepo = objectivesRepo;
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

    // True once the user has chosen the login path. Used to route the tour's
    // final step back to "done" directly, and to short-circuit the "Skip auth"
    // branch on welcome.
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

    // ── Role (logged-in only) ───────────────────────────────────────

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

    // ── Tour: first objective ───────────────────────────────────────

    [ObservableProperty]
    private string _firstObjectiveTitle = "";

    [ObservableProperty]
    private string _firstObjectiveConfirmation = "";

    /// <summary>Fires when the flow completes (with or without auth).</summary>
    public event Action? Completed;

    // ── Welcome ──────────────────────────────────────────────────────

    /// <summary>Primary action: skip auth, go into the role step then the tour.</summary>
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

    // ── Role (logged-in only) ───────────────────────────────────────

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

            State = "tourWhat";
            Error = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding finish-role failed");
            Error = "Couldn't save your role.";
        }
        finally { Busy = false; }
    }

    // ── Tour (shared by both paths) ─────────────────────────────────

    [RelayCommand]
    private void NextTourStep()
    {
        Error = "";
        State = State switch
        {
            "tourWhat" => "tourLoop",
            "tourLoop" => "tourHabits",
            "tourHabits" => "tourObjective",
            _ => State,
        };
    }

    [RelayCommand]
    private async Task CreateFirstObjectiveAsync()
    {
        if (Busy) return;
        Error = "";
        var title = (FirstObjectiveTitle ?? "").Trim();
        if (string.IsNullOrEmpty(title))
        {
            Error = "Give your objective a name — something short you want to practice this session.";
            return;
        }

        Busy = true;
        try
        {
            await _objectivesRepo.CreateAsync(title);
            FirstObjectiveConfirmation = $"✔ \"{title}\" is now your priority objective.";

            // Stamp skip/finish state and fire Completed. For the login path
            // OnboardingSkipped is already false (set during FinishAccountAsync);
            // for the skip path we set it here.
            var config = await _configService.LoadAsync();
            if (!_chosenLoginPath)
            {
                config.OnboardingSkipped = true;
                await _configService.SaveAsync(config);
            }

            State = "done";
            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding first-objective create failed");
            Error = "Couldn't save your objective. Try again.";
        }
        finally { Busy = false; }
    }

    /// <summary>
    /// Skip the "create an objective" step. Still records OnboardingSkipped
    /// as appropriate so the user doesn't land back here.
    /// </summary>
    [RelayCommand]
    private async Task SkipTourObjectiveAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var config = await _configService.LoadAsync();
            if (!_chosenLoginPath)
            {
                config.OnboardingSkipped = true;
                await _configService.SaveAsync(config);
            }
            State = "done";
            Completed?.Invoke();
        }
        finally { Busy = false; }
    }
}
