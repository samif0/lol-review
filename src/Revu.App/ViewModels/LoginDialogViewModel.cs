#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>
/// Drives the sidebar "Log in" dialog. Chains the same three steps the
/// onboarding flow uses, in one modal:
///   "email"   — enter email, send magic-link OTP
///   "code"    — paste the emailed code, mint + store the session
///   "account" — enter Riot ID (gameName#tagLine) + region, resolve + store
/// On a clean run through "account" the dialog reports success so the shell can
/// refresh the sidebar indicator and close. The Riot-ID step can be skipped —
/// the session alone is enough to share clips; linking the account just enables
/// match lookup, exactly as in onboarding.
/// </summary>
public partial class LoginDialogViewModel : ObservableObject
{
    private readonly IRiotAuthClient _authClient;
    private readonly IConfigService _configService;
    private readonly ILogger<LoginDialogViewModel> _logger;

    public LoginDialogViewModel(
        IRiotAuthClient authClient,
        IConfigService configService,
        ILogger<LoginDialogViewModel> logger)
    {
        _authClient = authClient;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>"email" → "code" → "account" → "done".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmailStep))]
    [NotifyPropertyChangedFor(nameof(IsCodeStep))]
    [NotifyPropertyChangedFor(nameof(IsAccountStep))]
    [NotifyPropertyChangedFor(nameof(StepLabel))]
    private string _state = "email";

    public bool IsEmailStep => State == "email";
    public bool IsCodeStep => State == "code";
    public bool IsAccountStep => State == "account";

    /// <summary>"1 / 3", "2 / 3", "3 / 3" — terminal-style step counter.</summary>
    public string StepLabel => State switch
    {
        "email" => "1 / 3",
        "code" => "2 / 3",
        _ => "3 / 3",
    };

    /// <summary>True once the full flow (incl. Riot ID) completed successfully.</summary>
    public bool Completed { get; private set; }

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string _info = "";

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasInfo => !string.IsNullOrWhiteSpace(Info);

    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _otpCode = "";

    [ObservableProperty]
    private string _riotId = "";

    [ObservableProperty]
    private string _riotRegion = "na1";

    /// <summary>Pre-fill the email if we remember one from a prior session.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            if (!string.IsNullOrWhiteSpace(config.RiotSessionEmail)) Email = config.RiotSessionEmail;
            if (!string.IsNullOrWhiteSpace(config.RiotId)) RiotId = config.RiotId;
            if (!string.IsNullOrWhiteSpace(config.RiotRegion)) RiotRegion = config.RiotRegion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login dialog: failed to pre-fill from config");
        }
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (Busy) return;
        Error = "";
        var email = (Email ?? "").Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            Error = "Enter a valid email address.";
            return;
        }

        Busy = true;
        try
        {
            await _authClient.LoginAsync(email);
            State = "code";
            Info = $"Check {email} for a code.";
            OtpCode = "";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login dialog: send code failed");
            Error = "Couldn't reach the server. Check your connection.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task VerifyCodeAsync()
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
            config.OnboardingSkipped = false;
            await _configService.SaveAsync(config);

            State = "account";
            Info = "Signed in. Link your Riot account to load your games.";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login dialog: verify failed");
            Error = "Couldn't verify the code.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
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
                State = "email";
                return;
            }

            var account = await _authClient.ResolveAccountAsync(session, id, region);

            config.RiotId = id;
            config.RiotRegion = region;
            config.RiotPuuid = account.Puuid;
            config.OnboardingSkipped = false;
            await _configService.SaveAsync(config);

            Completed = true;
            State = "done";
        }
        catch (RiotAuthException ex) { Error = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login dialog: resolve account failed");
            Error = "Couldn't validate that account.";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void BackToEmail()
    {
        State = "email";
        OtpCode = "";
        Error = "";
        Info = "";
    }
}
