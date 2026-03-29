#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Helpers;
using LoLReview.App.Services;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;
using Velopack;
using Windows.Storage.Pickers;

namespace LoLReview.App.ViewModels;

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
    private string _ascentStatusColorHex = "#7070a0";

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
    private string _clipUsageColorHex = "#22c55e";

    [ObservableProperty]
    private bool _ffmpegAvailable;

    [ObservableProperty]
    private string _ffmpegStatusText = "Checking...";

    [ObservableProperty]
    private string _ffmpegStatusColorHex = "#7070a0";

    [ObservableProperty]
    private bool _backupEnabled;

    [ObservableProperty]
    private string _backupFolder = "";

    [ObservableProperty]
    private bool _tiltFixEnabled;

    [ObservableProperty]
    private bool _requireReviewNotes;

    [ObservableProperty]
    private string _appVersion = "";

    [ObservableProperty]
    private string _saveStatusText = "";

    [ObservableProperty]
    private string _saveStatusColorHex = "#22c55e";

    [ObservableProperty]
    private string _updateStatusText = "";

    [ObservableProperty]
    private string _updateStatusColorHex = "#7070a0";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private int _updateProgress;

    // ── Constructor ─────────────────────────────────────────────────

    public SettingsViewModel(
        IConfigService configService,
        IClipService clipService,
        IUpdateService updateService,
        ILogger<SettingsViewModel> logger)
    {
        _configService = configService;
        _clipService = clipService;
        _updateService = updateService;
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

            // App version (from Velopack if installed, else assembly)
            AppVersion = _updateService.IsInstalled
                ? $"v{_updateService.CurrentVersion}"
                : (typeof(App).Assembly.GetName().Version is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "dev");

            // Check ffmpeg
            var ffmpegPath = await _clipService.FindFfmpegAsync();
            FfmpegAvailable = ffmpegPath != null;
            FfmpegStatusText = FfmpegAvailable ? "Available" : "Not found -- clip saving disabled";
            FfmpegStatusColorHex = FfmpegAvailable ? "#22c55e" : "#ef4444";

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
            SaveStatusColorHex = verified ? "#22c55e" : "#c89b3c";

            _logger.LogInformation("Settings saved successfully");

            // Clear status after a delay
            await Task.Delay(2000);
            SaveStatusText = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            SaveStatusText = "Error saving settings";
            SaveStatusColorHex = "#ef4444";
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
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatusText = "Checking for updates...";
        UpdateStatusColorHex = "#7070a0";
        IsUpdateAvailable = false;

        try
        {
            _pendingUpdate = await _updateService.CheckForUpdateAsync();

            if (_pendingUpdate != null)
            {
                UpdateStatusText = $"Update available: v{_pendingUpdate.TargetFullRelease.Version}";
                UpdateStatusColorHex = "#c89b3c";
                IsUpdateAvailable = true;
            }
            else
            {
                UpdateStatusText = "You're on the latest version";
                UpdateStatusColorHex = "#22c55e";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            UpdateStatusText = "Update check failed";
            UpdateStatusColorHex = "#ef4444";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndApplyUpdateAsync()
    {
        if (_pendingUpdate == null) return;

        try
        {
            UpdateStatusText = "Downloading update...";
            UpdateStatusColorHex = "#c89b3c";

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
            UpdateStatusColorHex = "#ef4444";
        }
    }

    [RelayCommand]
    private void ClearAscentFolder()
    {
        AscentFolder = "";
        AscentStatus = "Ascent VOD disabled";
        AscentStatusColorHex = "#7070a0";
    }

    // ── Helpers ─────────────────────────────────────────────────────

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
                    AscentStatusColorHex = "#22c55e";
                }
                else
                {
                    AscentStatus = "No video files found in this folder";
                    AscentStatusColorHex = "#ef4444";
                }
            }
            else
            {
                AscentStatus = "Folder does not exist";
                AscentStatusColorHex = "#ef4444";
            }
        }
        catch
        {
            AscentStatus = "Error checking folder";
            AscentStatusColorHex = "#ef4444";
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
                    ClipUsageColorHex = "#22c55e";
                else if (pct < 95)
                    ClipUsageColorHex = "#c89b3c";
                else
                    ClipUsageColorHex = "#ef4444";
            }
            else
            {
                CurrentClipUsage = "No clips folder configured";
                ClipUsageColorHex = "#7070a0";
            }
        }
        catch
        {
            CurrentClipUsage = "Error reading clips folder";
            ClipUsageColorHex = "#ef4444";
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
