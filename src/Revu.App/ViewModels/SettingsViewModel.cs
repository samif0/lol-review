#nullable enable

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

    // ── Constructor ─────────────────────────────────────────────────

    private readonly IRiotAuthClient _riotAuthClient;

    public SettingsViewModel(
        IConfigService configService,
        IClipService clipService,
        IUpdateService updateService,
        IRiotAuthClient riotAuthClient,
        ILogger<SettingsViewModel> logger)
    {
        _configService = configService;
        _clipService = clipService;
        _updateService = updateService;
        _riotAuthClient = riotAuthClient;
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
            config.RiotId = (RiotId ?? "").Trim();
            config.RiotRegion = (RiotRegion ?? "").Trim().ToLowerInvariant();

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
}
