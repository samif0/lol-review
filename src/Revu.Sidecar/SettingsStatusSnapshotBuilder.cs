#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

// ─────────────────────────────────────────────────────────────────────────────
// GET /api/settings/status — the read-only DIAGNOSTIC slice the Settings page
// shows next to the editable config fields (GET /api/config already serves the
// editable surface). Three independent reads, NONE of which touch the DB:
//
//   • ffmpeg availability — IClipService.FindFfmpegAsync() verbatim (filesystem
//     probe). Drives the "clip saving disabled" banner.
//   • Ascent folder status — a recursive video-file count, mirroring
//     SettingsViewModel.UpdateAscentStatus / EnumerateFilesSafe EXACTLY (same
//     extension set, same status strings + hex colors, same try/catch fallbacks).
//   • Clips folder usage — recursive byte-sum vs ClipsMaxSizeMb, mirroring
//     SettingsViewModel.UpdateClipUsage EXACTLY (same pct thresholds + hex).
//   • Backups list — IBackupService.ListBackupsAsync() verbatim (enumerates the
//     backups directory; newest first). Feeds the Restore picker (restore itself
//     is DEFERRED — destructive + relaunch).
//
// Folder-status computation is filesystem enumeration, not DB SQL, so it is
// replicated here from the WinUI VM (the VM owns it too — there is no Core method
// to reuse). ffmpeg + backups DO have Core methods and are reused verbatim.
//
// PascalCase here, camelCase on the wire (Program.cs serializer). Color hexes
// ride along as strings (mirror every other snapshot builder — the frontend
// applies them to style props only, never innerHTML).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SettingsStatusSnapshotBuilder
{
    // Mirror SettingsViewModel.VideoExtensions EXACTLY (note: includes .mov,
    // which the Core ClipService/VodService sets omit — the Settings page counts
    // .mov as an Ascent recording, so we match the page, not Core).
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov"
    };

    private readonly IConfigService _config;
    private readonly IClipService _clips;
    private readonly IBackupService _backups;
    private readonly ILogger<SettingsStatusSnapshotBuilder> _logger;

    public SettingsStatusSnapshotBuilder(
        IConfigService config,
        IClipService clips,
        IBackupService backups,
        ILogger<SettingsStatusSnapshotBuilder> logger)
    {
        _config = config;
        _clips = clips;
        _backups = backups;
        _logger = logger;
    }

    public async Task<SettingsStatusDto> BuildAsync(CancellationToken ct = default)
    {
        AppConfig cfg;
        try
        {
            cfg = await _config.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings status: LoadAsync failed; serving defaults");
            cfg = new AppConfig();
        }

        var ffmpeg = await ProbeFfmpegAsync();
        var ascent = ComputeAscentStatus(cfg.AscentFolder ?? "");
        var clipUsage = ComputeClipUsage(cfg.ClipsFolder ?? "", cfg.ClipsMaxSizeMb);
        var backups = await LoadBackupsAsync();

        return new SettingsStatusDto(
            Ffmpeg: ffmpeg,
            Ascent: ascent,
            ClipUsage: clipUsage,
            Backups: backups);
    }

    // ── ffmpeg — IClipService.FindFfmpegAsync() verbatim ─────────────────────
    private async Task<FfmpegStatusDto> ProbeFfmpegAsync()
    {
        try
        {
            var path = await _clips.FindFfmpegAsync();
            var available = path is not null;
            return new FfmpegStatusDto(
                Available: available,
                Text: available ? "Available" : "Not found; clip saving disabled",
                ColorHex: available ? "#7EC9A0" : "#D38C90");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg probe failed (degraded to not-found)");
            return new FfmpegStatusDto(false, "Not found; clip saving disabled", "#D38C90");
        }
    }

    // ── Ascent status — mirror SettingsViewModel.UpdateAscentStatus EXACTLY ───
    private FolderStatusDto ComputeAscentStatus(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new FolderStatusDto("", "#8A80A8"); // blank → empty status

        try
        {
            if (Directory.Exists(folder))
            {
                var count = EnumerateFilesSafe(folder)
                    .Count(path => VideoExtensions.Contains(Path.GetExtension(path)));

                return count > 0
                    ? new FolderStatusDto($"Found {count} recording{(count != 1 ? "s" : "")}", "#7EC9A0")
                    : new FolderStatusDto("No video files found in this folder", "#D38C90");
            }
            return new FolderStatusDto("Folder does not exist", "#D38C90");
        }
        catch
        {
            return new FolderStatusDto("Error checking folder", "#D38C90");
        }
    }

    // ── Clip usage — mirror SettingsViewModel.UpdateClipUsage EXACTLY ─────────
    private ClipUsageDto ComputeClipUsage(string folder, int maxSizeMb)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                long totalBytes = 0;
                foreach (var path in EnumerateFilesSafe(folder))
                {
                    try { totalBytes += new FileInfo(path).Length; }
                    catch { /* ignore transient/locked files while totaling */ }
                }

                var totalMb = totalBytes / (1024.0 * 1024.0);
                var pct = maxSizeMb > 0 ? totalMb / maxSizeMb * 100 : 0;

                var color = pct < 80 ? "#7EC9A0" : pct < 95 ? "#C9956A" : "#D38C90";
                return new ClipUsageDto(
                    Text: $"Using {totalMb:F0} MB / {maxSizeMb} MB ({pct:F0}%)",
                    ColorHex: color);
            }
            return new ClipUsageDto("No clips folder configured", "#8A80A8");
        }
        catch
        {
            return new ClipUsageDto("Error reading clips folder", "#D38C90");
        }
    }

    // ── Backups — IBackupService.ListBackupsAsync() verbatim ──────────────────
    private async Task<IReadOnlyList<BackupDto>> LoadBackupsAsync()
    {
        try
        {
            var backups = await _backups.ListBackupsAsync();
            return backups
                .Select(b => new BackupDto(
                    FilePath: b.FilePath,
                    FileName: b.FileName,
                    Label: b.Label,
                    Timestamp: b.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                    SizeMb: Math.Round(b.FileSizeBytes / (1024.0 * 1024.0), 1)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Backups list failed (degraded to empty)");
            return Array.Empty<BackupDto>();
        }
    }

    // ── EnumerateFilesSafe — copied verbatim from SettingsViewModel ───────────
    // Iterative directory walk that swallows per-folder access errors so a single
    // permission-denied subfolder doesn't blank the whole status.
    private static IEnumerable<string> EnumerateFilesSafe(string rootFolder)
    {
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
                yield return file;

            string[] directories;
            try { directories = Directory.GetDirectories(current); }
            catch { directories = Array.Empty<string>(); }

            foreach (var dir in directories)
                pending.Push(dir);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DTOs (PascalCase → camelCase on the wire).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Read-only diagnostics for the Settings page (GET /api/settings/status).</summary>
public sealed record SettingsStatusDto(
    FfmpegStatusDto Ffmpeg,
    FolderStatusDto Ascent,
    ClipUsageDto ClipUsage,
    IReadOnlyList<BackupDto> Backups);

public sealed record FfmpegStatusDto(bool Available, string Text, string ColorHex);

/// <summary>Ascent folder status — text + the WinUI hex color for it.</summary>
public sealed record FolderStatusDto(string Text, string ColorHex);

/// <summary>Clip folder usage — "Using X MB / Y MB (Z%)" + the threshold hex.</summary>
public sealed record ClipUsageDto(string Text, string ColorHex);

/// <summary>One backup file in the restore picker. Restore is DEFERRED.</summary>
public sealed record BackupDto(
    string FilePath,
    string FileName,
    string Label,
    string Timestamp,
    double SizeMb);
