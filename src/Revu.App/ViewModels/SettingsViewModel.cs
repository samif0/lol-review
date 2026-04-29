#nullable enable

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Velopack;
using Windows.Storage.Pickers;

namespace Revu.App.ViewModels;

/// <summary>ViewModel for the Settings page.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".webm",
        ".avi",
        ".mov"
    };

    private readonly IConfigService _configService;
    private readonly IClipService _clipService;
    private readonly IUpdateService _updateService;
    private readonly IBackupService _backupService;
    private readonly ILogger<SettingsViewModel> _logger;

    private UpdateInfo? _pendingUpdate;

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private string _ascentFolder = "";

    [ObservableProperty]
    private string _ascentStatus = "";

    [ObservableProperty]
    private string _ascentStatusColorHex = "#8A80A8";

    [ObservableProperty]
    private string _clipsFolder = "";

    [ObservableProperty]
    private int _clipsMaxSizeMb = 2048;

    public string ClipsMaxSizeMbText
    {
        get => ClipsMaxSizeMb.ToString();
        set
        {
            if (int.TryParse(value, out var v) && v >= 100 && v <= 50000)
            {
                ClipsMaxSizeMb = v;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    private string _currentClipUsage = "";

    [ObservableProperty]
    private string _clipUsageColorHex = "#7EC9A0";

    [ObservableProperty]
    private bool _ffmpegAvailable;

    [ObservableProperty]
    private string _ffmpegStatusText = "Checking...";

    [ObservableProperty]
    private string _ffmpegStatusColorHex = "#8A80A8";

    [ObservableProperty]
    private bool _backupEnabled;

    [ObservableProperty]
    private string _backupFolder = "";

    [ObservableProperty]
    private bool _tiltFixEnabled;

    [ObservableProperty]
    private bool _requireReviewNotes;

    // v2.15.0: sidebar/page-enter animation preference. Default true.
    [ObservableProperty]
    private bool _sidebarAnimationEnabled = true;

    // v2.16.1: minimize window + suspend animations during a game. Default true.
    [ObservableProperty]
    private bool _minimizeDuringGame = true;

    // v2.15.0: reset/revert state.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResetConfirmTextValid))]
    private string _resetConfirmText = "";

    [ObservableProperty]
    private string _resetStatus = "";

    [ObservableProperty]
    private bool _isResetting;

    [ObservableProperty]
    private string _backfillStatus = "";

    [ObservableProperty]
    private bool _isBackfilling;

    /// <summary>True when the user typed "RESET" into the confirm box.</summary>
    public bool IsResetConfirmTextValid => string.Equals(ResetConfirmText?.Trim(), "RESET", StringComparison.Ordinal);

    public ObservableCollection<BackupChoiceItem> AvailableBackups { get; } = new();

    [ObservableProperty]
    private BackupChoiceItem? _selectedBackup;

    [ObservableProperty]
    private string _restoreStatus = "";

    [ObservableProperty]
    private bool _isRestoring;

    // ── Riot proxy login (Path B) ─────────────────────────────────

    /// <summary>
    /// View states: "loggedOut" (email form) → "codeSent" (OTP entry) → "loggedIn" (profile).
    /// </summary>
    [ObservableProperty]
    private string _riotAuthState = "loggedOut";

    [ObservableProperty]
    private bool _riotAuthBusy;

    [ObservableProperty]
    private string _riotAuthError = "";

    // Logged-out form
    [ObservableProperty]
    private string _riotAuthEmail = "";

    [ObservableProperty]
    private string _riotAuthInviteCode = "";

    // Code-sent form
    [ObservableProperty]
    private string _riotAuthOtpCode = "";

    [ObservableProperty]
    private string _riotAuthInfo = "";  // "We sent a code to <email>..."

    // Logged-in profile
    [ObservableProperty]
    private string _riotAuthLoggedInEmail = "";

    [ObservableProperty]
    private string _riotId = "";

    [ObservableProperty]
    private string _riotRegion = "";

    [ObservableProperty]
    private string _appVersion = "";

    [ObservableProperty]
    private string _saveStatusText = "";

    [ObservableProperty]
    private string _saveStatusColorHex = "#7EC9A0";

    [ObservableProperty]
    private string _updateStatusText = "";

    [ObservableProperty]
    private string _updateStatusColorHex = "#8A80A8";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private int _updateProgress;

    public SolidColorBrush UpdateStatusBrush => HexBrush(UpdateStatusColorHex);

    // v2.17 cross-cutting: surface the last 50 lines of velopack.log when the
    // user clicks "Diagnose update". Shown inline in the About card so first-
    // cohort users can paste it into a bug report when their update silently
    // fails. Set to "" when the panel is collapsed.
    [ObservableProperty]
    private string _velopackLogTail = "";

    [ObservableProperty]
    private bool _isVelopackTailVisible;

    // ── Constructor ─────────────────────────────────────────────────

    private readonly IRiotAuthClient _riotAuthClient;
    private readonly EnemyLanerBackfillService _backfillService;

    public SettingsViewModel(
        IConfigService configService,
        IClipService clipService,
        IUpdateService updateService,
        IRiotAuthClient riotAuthClient,
        IBackupService backupService,
        EnemyLanerBackfillService backfillService,
        ILogger<SettingsViewModel> logger)
    {
        _configService = configService;
        _clipService = clipService;
        _updateService = updateService;
        _riotAuthClient = riotAuthClient;
        _backupService = backupService;
        _backfillService = backfillService;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();

            AscentFolder = config.AscentFolder;
            ClipsFolder = config.ClipsFolder;
            ClipsMaxSizeMb = config.ClipsMaxSizeMb;
            BackupEnabled = config.BackupEnabled;
            BackupFolder = config.BackupFolder;
            TiltFixEnabled = config.TiltFixMode;
            RequireReviewNotes = config.RequireReviewNotes;
            SidebarAnimationEnabled = config.SidebarAnimationEnabled;
            MinimizeDuringGame = config.MinimizeDuringGame;
            RiotId = config.RiotId;
            RiotRegion = config.RiotRegion;
            RestoreRiotAuthState(config);

            // App version (from Velopack if installed, else assembly)
            AppVersion = _updateService.IsInstalled
                ? $"v{_updateService.CurrentVersion}"
                : (typeof(App).Assembly.GetName().Version is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "dev");

            // Check ffmpeg
            var ffmpegPath = await _clipService.FindFfmpegAsync();
            FfmpegAvailable = ffmpegPath != null;
            FfmpegStatusText = FfmpegAvailable ? "Available" : "Not found -- clip saving disabled";
            FfmpegStatusColorHex = FfmpegAvailable ? "#7EC9A0" : "#D38C90";

            // Check ascent folder status
            if (!string.IsNullOrWhiteSpace(AscentFolder))
            {
                UpdateAscentStatus(AscentFolder);
            }
            else
            {
                AscentStatus = "";
            }

            // Clip usage (placeholder -- actual implementation would scan folder)
            UpdateClipUsage();

            RefreshUpdateState();
            if (_updateService.IsInstalled && !_updateService.HasChecked && !_updateService.IsChecking)
            {
                await RefreshUpdateStateAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();

            config.AscentFolder = AscentFolder;
            config.ClipsFolder = ClipsFolder;
            config.ClipsMaxSizeMb = ClipsMaxSizeMb;
            config.BackupEnabled = BackupEnabled;
            config.BackupFolder = BackupFolder;
            config.TiltFixMode = TiltFixEnabled;
            config.RequireReviewNotes = RequireReviewNotes;
            config.SidebarAnimationEnabled = SidebarAnimationEnabled;
            config.MinimizeDuringGame = MinimizeDuringGame;
            // Propagate immediately so the user sees the effect without a restart.
            // SidebarEnergyDrainAnimator.UpdateTarget checks this flag every time
            // the nav selection changes; toggling off clears current trails on
            // next update but leaves any mid-animation trails until then. Force a
            // re-run: the ShellPage subscribes to nav changes and calls
            // UpdateTarget on selection — we trigger that by briefly toggling
            // the flag… actually simpler: let next nav event pick it up. The
            // trails are subtle; users won't mind one extra second of drift.
            Helpers.SidebarEnergyDrainAnimator.Enabled = SidebarAnimationEnabled;
            var newRiotId = (RiotId ?? "").Trim();
            var newRiotRegion = (RiotRegion ?? "").Trim().ToLowerInvariant();
            var riotIdChanged = !string.Equals(config.RiotId, newRiotId, StringComparison.OrdinalIgnoreCase)
                              || !string.Equals(config.RiotRegion, newRiotRegion, StringComparison.OrdinalIgnoreCase);
            config.RiotId = newRiotId;
            config.RiotRegion = newRiotRegion;

            // v2.16: resolve the Riot ID + region to a PUUID and stash it.
            // Settings used to skip this step (only Onboarding wrote PUUID),
            // which left users who logged in via Settings unable to run the
            // matchup backfill — RunAsync bails when PUUID is empty.
            if (!string.IsNullOrEmpty(newRiotId)
                && !string.IsNullOrEmpty(newRiotRegion)
                && !string.IsNullOrEmpty(config.RiotSessionToken)
                && (string.IsNullOrEmpty(config.RiotPuuid) || riotIdChanged))
            {
                try
                {
                    var account = await _riotAuthClient.ResolveAccountAsync(
                        config.RiotSessionToken, newRiotId, newRiotRegion);
                    config.RiotPuuid = account.Puuid ?? "";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resolve PUUID for {RiotId}/{Region}", newRiotId, newRiotRegion);
                }
            }

            await _configService.SaveAsync(config);

            var reloaded = await _configService.LoadAsync();
            var verified =
                string.Equals(reloaded.AscentFolder ?? "", AscentFolder ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reloaded.ClipsFolder ?? "", ClipsFolder ?? "", StringComparison.OrdinalIgnoreCase) &&
                reloaded.ClipsMaxSizeMb == ClipsMaxSizeMb &&
                reloaded.BackupEnabled == BackupEnabled &&
                string.Equals(reloaded.BackupFolder ?? "", BackupFolder ?? "", StringComparison.OrdinalIgnoreCase) &&
                reloaded.TiltFixMode == TiltFixEnabled &&
                reloaded.RequireReviewNotes == RequireReviewNotes;

            UpdateAscentStatus(AscentFolder ?? "");
            UpdateClipUsage();

            SaveStatusText = verified ? "Settings saved and verified." : "Settings saved, but verification did not fully match.";
            SaveStatusColorHex = verified ? "#7EC9A0" : "#C9956A";

            _logger.LogInformation("Settings saved successfully");

            // Clear status after a delay
            await Task.Delay(2000);
            SaveStatusText = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            SaveStatusText = "Error saving settings";
            SaveStatusColorHex = "#D38C90";
        }
    }

    [RelayCommand]
    private async Task BrowseAscentFolderAsync()
    {
        var folder = await PickFolderAsync("Select Ascent Recordings Folder");
        if (folder != null)
        {
            AscentFolder = folder;
            UpdateAscentStatus(folder);
        }
    }

    [RelayCommand]
    private async Task BrowseClipsFolderAsync()
    {
        var folder = await PickFolderAsync("Select Clips Folder");
        if (folder != null)
        {
            ClipsFolder = folder;
        }
    }

    [RelayCommand]
    private async Task BrowseBackupFolderAsync()
    {
        var folder = await PickFolderAsync("Select Backup Folder");
        if (folder != null)
        {
            BackupFolder = folder;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        await RefreshUpdateStateAsync(force: true);
    }

    [RelayCommand]
    private async Task DownloadAndApplyUpdateAsync()
    {
        if (_pendingUpdate == null) return;

        try
        {
            UpdateStatusText = "Downloading update...";
            UpdateStatusColorHex = "#C9956A";

            await _updateService.DownloadUpdateAsync(_pendingUpdate, progress =>
            {
                DispatcherHelper.RunOnUIThread(() =>
                {
                    UpdateProgress = progress;
                    UpdateStatusText = $"Downloading... {progress}%";
                });
            });

            UpdateStatusText = "Restarting to apply update...";
            _updateService.ApplyUpdateAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download/apply failed");
            UpdateStatusText = "Update failed — try again later";
            UpdateStatusColorHex = "#D38C90";
        }
    }

    [RelayCommand]
    private void ClearAscentFolder()
    {
        AscentFolder = "";
        AscentStatus = "Ascent VOD disabled";
        AscentStatusColorHex = "#8A80A8";
    }

    /// <summary>
    /// v2.17 cross-cutting: opens %LOCALAPPDATA%\Revu in Explorer so users
    /// don't need to navigate AppData manually when filing a bug. The folder
    /// holds crash.log, startup.log, and the coach-host log.
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Revu");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open log folder");
        }
    }

    /// <summary>
    /// v2.17 cross-cutting: reads the last 50 lines of
    /// %LOCALAPPDATA%\LoLReview\velopack.log and surfaces them inline.
    /// V2_16_BACKLOG Investigation #1 — gives users a way to see what
    /// happened during a silent update failure without having to dig
    /// through AppData themselves.
    /// </summary>
    [RelayCommand]
    private void DiagnoseUpdate()
    {
        if (IsVelopackTailVisible)
        {
            IsVelopackTailVisible = false;
            VelopackLogTail = "";
            return;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LoLReview",
                "velopack.log");
            if (!File.Exists(path))
            {
                VelopackLogTail = "(no velopack.log found — the auto-updater may not have run yet on this install)";
                IsVelopackTailVisible = true;
                return;
            }

            // Use FileShare.ReadWrite — Velopack may still hold the file.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var allLines = reader.ReadToEnd().Split('\n');
            var tail = allLines.Length > 50 ? allLines[^50..] : allLines;
            VelopackLogTail = string.Join("\n", tail).TrimEnd();
            IsVelopackTailVisible = true;
        }
        catch (Exception ex)
        {
            VelopackLogTail = $"(could not read velopack.log: {ex.Message})";
            IsVelopackTailVisible = true;
            _logger.LogWarning(ex, "Could not read velopack.log");
        }
    }

    partial void OnUpdateStatusColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(UpdateStatusBrush));
    }

    private async Task RefreshUpdateStateAsync(bool force)
    {
        IsCheckingUpdate = true;
        RefreshUpdateState();

        try
        {
            _pendingUpdate = await _updateService.CheckForUpdateAsync(force);
        }
        finally
        {
            RefreshUpdateState();
        }
    }

    private void RefreshUpdateState()
    {
        _pendingUpdate = _updateService.AvailableUpdate;
        IsCheckingUpdate = _updateService.IsChecking;
        IsUpdateAvailable = _updateService.IsUpdateAvailable;

        if (_updateService.IsChecking)
        {
            UpdateStatusText = "Checking for updates...";
            UpdateStatusColorHex = "#8A80A8";
            return;
        }

        UpdateStatusText = _updateService.StatusText;

        if (_updateService.IsUpdateAvailable)
        {
            UpdateStatusColorHex = "#C9956A";
        }
        else if (_updateService.LastCheckFailed)
        {
            UpdateStatusColorHex = "#D38C90";
        }
        else if (_updateService.IsInstalled && _updateService.HasChecked)
        {
            UpdateStatusColorHex = "#7EC9A0";
        }
        else
        {
            UpdateStatusColorHex = "#8A80A8";
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void RestoreRiotAuthState(Revu.Core.Models.AppConfig config)
    {
        var unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrWhiteSpace(config.RiotSessionToken) && config.RiotSessionExpiresAt > unixNow)
        {
            RiotAuthLoggedInEmail = config.RiotSessionEmail;
            RiotAuthState = "loggedIn";
        }
        else
        {
            RiotAuthLoggedInEmail = "";
            RiotAuthState = "loggedOut";
        }
        RiotAuthError = "";
        RiotAuthInfo = "";
        RiotAuthOtpCode = "";
    }

    [RelayCommand]
    private async Task RiotAuthSignupAsync()
    {
        if (RiotAuthBusy) return;
        RiotAuthError = "";
        var email = (RiotAuthEmail ?? "").Trim();
        var code = (RiotAuthInviteCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code))
        {
            RiotAuthError = "Enter an email and an invite code.";
            return;
        }

        RiotAuthBusy = true;
        try
        {
            await _riotAuthClient.SignupAsync(email, code);
            RiotAuthState = "codeSent";
            RiotAuthInfo = $"Check {email} for a code.";
            RiotAuthOtpCode = "";
        }
        catch (RiotAuthException ex) { RiotAuthError = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signup failed");
            RiotAuthError = "Couldn't reach the server.";
        }
        finally { RiotAuthBusy = false; }
    }

    [RelayCommand]
    private async Task RiotAuthLoginAsync()
    {
        if (RiotAuthBusy) return;
        RiotAuthError = "";
        var email = (RiotAuthEmail ?? "").Trim();
        if (string.IsNullOrEmpty(email))
        {
            RiotAuthError = "Enter an email.";
            return;
        }

        RiotAuthBusy = true;
        try
        {
            await _riotAuthClient.LoginAsync(email);
            RiotAuthState = "codeSent";
            RiotAuthInfo = $"Check {email} for a code.";
            RiotAuthOtpCode = "";
        }
        catch (RiotAuthException ex) { RiotAuthError = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            RiotAuthError = "Couldn't reach the server.";
        }
        finally { RiotAuthBusy = false; }
    }

    [RelayCommand]
    private async Task RiotAuthVerifyAsync()
    {
        if (RiotAuthBusy) return;
        RiotAuthError = "";
        var code = (RiotAuthOtpCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            RiotAuthError = "Paste the code from your email.";
            return;
        }

        RiotAuthBusy = true;
        try
        {
            var result = await _riotAuthClient.VerifyAsync(code);
            var config = await _configService.LoadAsync();
            config.RiotSessionToken = result.SessionToken;
            config.RiotSessionEmail = (RiotAuthEmail ?? "").Trim();
            config.RiotSessionExpiresAt = result.ExpiresAt;
            await _configService.SaveAsync(config);

            RiotAuthLoggedInEmail = config.RiotSessionEmail;
            RiotAuthState = "loggedIn";
            RiotAuthInviteCode = "";
            RiotAuthOtpCode = "";
            RiotAuthInfo = "";
        }
        catch (RiotAuthException ex) { RiotAuthError = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verify failed");
            RiotAuthError = "Couldn't verify the code.";
        }
        finally { RiotAuthBusy = false; }
    }

    [RelayCommand]
    private void RiotAuthBackToEmail()
    {
        RiotAuthState = "loggedOut";
        RiotAuthOtpCode = "";
        RiotAuthError = "";
        RiotAuthInfo = "";
    }

    [RelayCommand]
    private async Task RiotAuthLogoutAsync()
    {
        if (RiotAuthBusy) return;
        RiotAuthBusy = true;
        try
        {
            var config = await _configService.LoadAsync();
            var token = config.RiotSessionToken;
            config.RiotSessionToken = "";
            config.RiotSessionEmail = "";
            config.RiotSessionExpiresAt = 0;
            // Reset Skip so a stale opt-out doesn't keep the user out of
            // the onboarding flow on next launch.
            config.OnboardingSkipped = false;
            await _configService.SaveAsync(config);
            if (!string.IsNullOrEmpty(token))
            {
                await _riotAuthClient.LogoutAsync(token);
            }
            RiotAuthLoggedInEmail = "";
            RiotAuthState = "loggedOut";
            RiotAuthEmail = "";
            RiotAuthInviteCode = "";
            RiotAuthOtpCode = "";
            RiotAuthInfo = "";
            RiotAuthError = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout failed");
        }
        finally { RiotAuthBusy = false; }
    }

    private void UpdateAscentStatus(string folder)
    {
        try
        {
            if (System.IO.Directory.Exists(folder))
            {
                var count = EnumerateFilesSafe(folder)
                    .Count(path => VideoExtensions.Contains(System.IO.Path.GetExtension(path)));

                if (count > 0)
                {
                    AscentStatus = $"Found {count} recording{(count != 1 ? "s" : "")}";
                    AscentStatusColorHex = "#7EC9A0";
                }
                else
                {
                    AscentStatus = "No video files found in this folder";
                    AscentStatusColorHex = "#D38C90";
                }
            }
            else
            {
                AscentStatus = "Folder does not exist";
                AscentStatusColorHex = "#D38C90";
            }
        }
        catch
        {
            AscentStatus = "Error checking folder";
            AscentStatusColorHex = "#D38C90";
        }
    }

    private void UpdateClipUsage()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ClipsFolder) && System.IO.Directory.Exists(ClipsFolder))
            {
                long totalBytes = 0;
                foreach (var path in EnumerateFilesSafe(ClipsFolder))
                {
                    try
                    {
                        totalBytes += new System.IO.FileInfo(path).Length;
                    }
                    catch
                    {
                        // Ignore transient or locked files while totaling clip usage.
                    }
                }

                var totalMb = totalBytes / (1024.0 * 1024.0);
                var pct = ClipsMaxSizeMb > 0 ? totalMb / ClipsMaxSizeMb * 100 : 0;

                CurrentClipUsage = $"Using {totalMb:F0} MB / {ClipsMaxSizeMb} MB ({pct:F0}%)";

                if (pct < 80)
                    ClipUsageColorHex = "#7EC9A0";
                else if (pct < 95)
                    ClipUsageColorHex = "#C9956A";
                else
                    ClipUsageColorHex = "#D38C90";
            }
            else
            {
                CurrentClipUsage = "No clips folder configured";
                ClipUsageColorHex = "#8A80A8";
            }
        }
        catch
        {
            CurrentClipUsage = "Error reading clips folder";
            ClipUsageColorHex = "#D38C90";
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string rootFolder)
    {
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            try
            {
                files = System.IO.Directory.GetFiles(current);
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] directories;
            try
            {
                directories = System.IO.Directory.GetDirectories(current);
            }
            catch
            {
                directories = [];
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private static SolidColorBrush HexBrush(string hex)
    {
        var normalized = (hex ?? "#8A80A8").Trim().TrimStart('#');
        if (normalized.Length != 6)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(255, 138, 128, 168)); // #8A80A8 neutral
        }

        try
        {
            var r = byte.Parse(normalized[..2], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(normalized[2..4], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(normalized[4..6], System.Globalization.NumberStyles.HexNumber);
            return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        }
        catch
        {
            return new SolidColorBrush(ColorHelper.FromArgb(255, 138, 128, 168)); // #8A80A8 neutral
        }
    }

    private static async Task<string?> PickFolderAsync(string description)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            // Get the main window handle from the active window
            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == nint.Zero)
            {
                // Fallback: get from WinUI app window (walk visual tree)
                return null;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            return null;
        }
    }

    // ── v2.15.0 reset + revert ──────────────────────────────────────

    /// <summary>Reload the list of available backups from disk for the restore picker.</summary>
    [RelayCommand]
    private async Task RefreshBackupsAsync()
    {
        try
        {
            var list = await _backupService.ListBackupsAsync();
            AvailableBackups.Clear();
            foreach (var b in list)
            {
                AvailableBackups.Add(new BackupChoiceItem(b));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list backups");
        }
    }

    /// <summary>
    /// Full reset: creates a pre-reset safety backup, deletes live DB + config,
    /// then asks the app to exit so the user relaunches into a clean state.
    /// Only runs when <see cref="IsResetConfirmTextValid"/>.
    /// </summary>
    [RelayCommand]
    private async Task ResetAllDataAsync()
    {
        if (IsResetting) return;
        if (!IsResetConfirmTextValid)
        {
            ResetStatus = "Type RESET in the box to confirm.";
            return;
        }

        IsResetting = true;
        ResetStatus = "Backing up and wiping data...";
        try
        {
            var result = await _backupService.ResetAllDataAsync();
            if (result.Success)
            {
                ResetStatus = string.IsNullOrEmpty(result.BackupFilePath)
                    ? "Data wiped. The app will exit — relaunch to start fresh."
                    : $"Done. Pre-reset backup saved to {result.BackupFilePath}. Exiting now...";
                // Exit the process so no in-memory state survives the reset.
                await Task.Delay(1500);
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
            else
            {
                ResetStatus = result.ErrorMessage ?? "Reset failed. Your data is untouched.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetAllDataAsync failed");
            ResetStatus = "Reset failed unexpectedly. Your data is untouched.";
        }
        finally
        {
            IsResetting = false;
        }
    }

    /// <summary>
    /// Restore the live DB from the selected backup. Backs up the current DB
    /// as <c>pre_restore_*</c> first so the restore itself is reversible.
    /// </summary>
    [RelayCommand]
    private async Task RestoreSelectedBackupAsync()
    {
        if (IsRestoring) return;
        if (SelectedBackup is null)
        {
            RestoreStatus = "Pick a backup to restore.";
            return;
        }

        IsRestoring = true;
        RestoreStatus = $"Restoring from {SelectedBackup.Info.FileName}...";
        try
        {
            var result = await _backupService.RestoreFromBackupAsync(SelectedBackup.Info.FilePath);
            if (result.Success)
            {
                RestoreStatus = "Restore complete. The app will exit — relaunch to load the restored DB.";
                await Task.Delay(1500);
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
            else
            {
                RestoreStatus = result.ErrorMessage ?? "Restore failed. Your current data is untouched.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestoreSelectedBackupAsync failed");
            RestoreStatus = "Restore failed unexpectedly. Your current data is untouched.";
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>
    /// v2.15.8: walk every game with a blank enemy_laner and try to fill it
    /// from Match-V5 via the proxy. Throttled to ~1.5 RPS so a 200-game
    /// backlog runs in roughly 2 minutes. Synchronous — the user waits.
    /// </summary>
    [RelayCommand]
    private async Task BackfillEnemyLanersAsync()
    {
        if (IsBackfilling) return;
        IsBackfilling = true;
        BackfillStatus = "Scanning games...";
        try
        {
            // v2.16: live progress so a 200-game run doesn't look frozen.
            var progress = new Progress<EnemyLanerBackfillProgress>(p =>
            {
                if (p.Total <= 0)
                {
                    BackfillStatus = "Scanning games...";
                    return;
                }
                BackfillStatus = $"Scanned {p.Scanned}/{p.Total}  \u2022  matched {p.Updated}  \u2022  skipped {p.Skipped}  \u2022  failed {p.Failed}";
            });

            var result = await _backfillService.RunAsync(progress: progress);
            BackfillStatus = result.Scanned == 0
                ? "Nothing to backfill — every game already has its enemy laner."
                : $"Done. Updated {result.Updated} of {result.Scanned} games "
                  + $"(skipped {result.Skipped}, failed {result.Failed}). "
                  + "Re-open Dashboard / History to see the matchup labels.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enemy-laner backfill failed");
            BackfillStatus = "Backfill hit an error — check logs.";
        }
        finally
        {
            IsBackfilling = false;
        }
    }
}

/// <summary>v2.15.0: backup-picker row. Wraps <see cref="BackupFileInfo"/> with a displayable label.</summary>
public sealed class BackupChoiceItem
{
    public BackupChoiceItem(BackupFileInfo info) { Info = info; }
    public BackupFileInfo Info { get; }
    public string DisplayLabel => Info.Label;
}
