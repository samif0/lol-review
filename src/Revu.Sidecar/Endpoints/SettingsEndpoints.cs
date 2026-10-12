#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapSettings(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/config (token-gated): read-only app-config snapshot ─────────────
        // Editable fields the Settings page round-trips + the derived/cross-page reads
        // (including AutoTimelineClippingHintDismissed).
        // Forces a disk re-read so it reflects POST /api/config/save. Secrets excluded.
        app.MapGet("/api/config", async (ConfigSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // ── GET /api/settings/status (token-gated): read-only Settings diagnostics ───
        // ffmpeg availability + clip-folder usage + the backups
        // list. All filesystem/DB-free reads (backups dir enumeration is not a DB read).
        // Pairs with GET /api/config (the editable surface) to fully hydrate the page.
        app.MapGet("/api/settings/status", async (SettingsStatusSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // Explicitly configured external folder only. Accepted work is tracked so
        // shutdown cancels enumeration/delays and drains any in-flight SQL insert.
        app.MapPost("/api/settings/scan-vods", async (WriteServices w, SidecarBackgroundWork work,
            RecordingRegistrationService registrations, SidecarEventHub events, ILogger<Program> log) =>
        {
            var completion = new TaskCompletionSource<VodScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!work.TryRun("manual Ascent scan", async () =>
            {
                try
                {
                    var cfg = await w.Config.LoadAsync();
                    if (string.IsNullOrWhiteSpace(cfg.AscentFolder))
                    {
                        completion.TrySetResult(new(false, 0, 0, "Choose an Ascent recording folder first."));
                        return;
                    }
                    await w.BackupGuard.EnsureBackedUpAsync();
                    var result = await registrations.RunExternalScanAsync(
                        reserved => w.VodScan.ScanAsync(work.Stopping, reserved), work.Stopping);
                    foreach (var gameId in result.LinkedGameIds) events.Publish("vodLinked", new { gameId });
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested)
                { completion.TrySetResult(new(false, 0, 0, "Revu is closing; scan stopped. Existing VOD links are preserved.")); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Ascent recording scan failed");
                    completion.TrySetResult(new(false, 0, 0, "Couldn't scan or link recordings. Check the folder is accessible and try again."));
                }
            })) return Results.Json(new VodScanResult(false, 0, 0, "Revu is closing; scan was not started."), jsonOptions, statusCode: 503);
            return Results.Json(await completion.Task, jsonOptions);
        });

        // GET /api/update/check — ask the GitHub release feed whether a newer version
        // exists (Velopack UpdateManager). Read-only; never throws. The host polls this on
        // launch (banner) and Settings (manual check). Returns the UpdateCheckResult shape.
        app.MapGet("/api/update/check", async (UpdateService upd) =>
            Results.Json(await upd.CheckAsync(), jsonOptions));

        // POST /api/update/download — stage the discovered update's package locally so the
        // desktop main process can apply it via Update.exe. Returns { ok, packagePath, version }.
        // A full package can outlast any client deadline, so the download is accepted
        // background work (SidecarJobs): giving up waiting never cancels it, asking again
        // joins it, and progress goes out as updateDownloadProgress SSE events.
        app.MapPost("/api/update/download", (UpdateService upd, SidecarJobs jobs, SidecarEventHub events,
            ILogger<Program> log, CancellationToken ct) =>
            DownloadUpdateAsync(upd.DownloadAsync, jobs, events, jsonOptions, log, ct));

        // ── POST /api/settings/export (token-gated): save the Markdown review export ──
        // IReviewExportService bundles games/notes/prompts/objectives/tags/matchup notes/
        // VOD links/bookmarks (one bulk read per table) and streams them to a new
        // revu-review-export-{yyyyMMdd-HHmm}.md in Documents\Revu. The sidecar writes the
        // file itself: a long history's Markdown used to round-trip through the renderer and
        // failed past the 4 MiB IPC envelope. Returns { ok, path, fileName, games }; the
        // desktop host opens that fixed folder on request. Denied in isolated host testing
        // (the folder is outside the scratch root).
        app.MapPost("/api/settings/export", (IReviewExportService export, ILogger<Program> log, CancellationToken ct) =>
            SaveReviewExportAsync(export, ReviewExportFolder(), jsonOptions, log, ct));

        // POST /api/settings/reset — DESTRUCTIVE: wipe all data and start fresh. The Core
        // IBackupService.ResetAllDataAsync ALWAYS takes a full backup FIRST (returns its
        // path), then clears, so this can never blind-overwrite. The desktop main process relaunches
        // the app on success. Returns { ok, backupPath } or { ok:false, error }.
        app.MapPost("/api/settings/reset", async (WriteServices w, ILogger<Program> log) =>
        {
            var result = await w.Backup.ResetAllDataAsync();
            if (!result.Success)
                return Results.Json(new { ok = false, error = result.ErrorMessage ?? "Reset failed." }, jsonOptions, statusCode: 422);
            log.LogWarning("Reset all data (backup at {BackupPath})", result.BackupFilePath);
            return Results.Json(new { ok = true, backupPath = result.BackupFilePath }, jsonOptions);
        });

        // POST /api/settings/restore { backupFilePath } — DESTRUCTIVE: replace the live DB
        // with a chosen backup. backupFilePath only NAMES an entry of Revu's own backup list:
        // Core RestoreFromBackupAsync looks the file up server-side (anything else is refused
        // unopened), validates it, takes a PRE-RESTORE safety backup (returns its path), then
        // swaps it in. The desktop main process relaunches on success. Returns
        // { ok, preRestoreBackupPath } or { ok:false, error }.
        app.MapPost("/api/settings/restore", async (RestoreBackupBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.BackupFilePath))
                return Results.BadRequest(new { error = "backupFilePath required" });
            var result = await w.Backup.RestoreFromBackupAsync(body.BackupFilePath);
            if (!result.Success)
                return Results.Json(new { ok = false, error = result.ErrorMessage ?? "Restore failed." }, jsonOptions, statusCode: 422);
            log.LogWarning("Restored backup {Path} (pre-restore backup at {PreBackup})", body.BackupFilePath, result.PreRestoreBackupFilePath);
            return Results.Json(new { ok = true, preRestoreBackupPath = result.PreRestoreBackupFilePath }, jsonOptions);
        });

        // POST /api/config/save — read-modify-write the app config (Settings page +
        // dismiss-flag writers). Mirrors SettingsViewModel.SaveCommand EXACTLY: load the
        // whole config, mutate ONLY the fields present in the body (so unrelated keys —
        // secrets, keybinds, puuid — are never clobbered), then SaveAsync. Every field
        // is nullable in the body; a null means "leave unchanged".
        //
        // P-023 defense-in-depth for the FOLDER paths: an empty/whitespace folder string
        // is treated as "leave unchanged" too — NOT as "blank the saved path". A save made
        // before the Settings page finished rendering (or any caller that sends "") would
        // otherwise zero clips_folder/backup_folder over the real values,
        // which is exactly how all three got blanked while riot_* survived. A DELIBERATE
        // clear (the Clear button) sends the FolderClearSentinel, which maps to "".
        //
        // H6: the load, the field mapping and the save run under ONE ConfigService lock
        // (UpdateAsync), so a concurrent writer (sign-in, resolve, sign-out) cannot lose
        // its update, and a config.json that exists but cannot be read throws instead of
        // being replaced with defaults plus this body's fields.
        app.MapPost("/api/config/save", async (SaveConfigBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null) return Results.BadRequest(new { error = "body required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            string? rejected = null;
            try
            {
                await w.Config.UpdateAsync(cfg =>
                {
                    rejected = ApplyConfigSave(body, cfg);
                    return rejected is null;
                });
            }
            catch (ConfigUnreadableException ex)
            {
                log.LogWarning(ex, "Config save refused: config.json could not be read");
                return Results.Json(new { ok = false, error = "Your settings could not be read right now, so nothing was changed. Try again." },
                    jsonOptions, statusCode: 503);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed write: a full disk, a locked file or the secret store.
                log.LogError(ex, "Config save failed while writing");
                return Results.Json(new { ok = false, error = "Your settings could not be saved. Check that your drive has free space, then try again." },
                    jsonOptions, statusCode: 500);
            }
            if (rejected is not null) return Results.BadRequest(new { error = rejected });
            log.LogInformation("Config saved via sidecar.");
            return Results.Json(new { ok = true }, jsonOptions);
        });

    }

    /// <summary>
    /// Maps a /api/config/save body onto the freshly loaded config (null = unchanged).
    /// Returns a user-facing error to reject the whole save, or null to save it.
    /// </summary>
    internal static string? ApplyConfigSave(SaveConfigBody body, Revu.Core.Models.AppConfig cfg)
    {
            // Folder fields: null/empty = unchanged; sentinel = explicit clear; else set.
            var ascentSet = ConfigSaveGuards.TryResolveFolderWrite(body.AscentFolder, out var ascent);
            if (ascentSet && IsNewNonLocalFolder(ascent, cfg.AscentFolder)) return NonLocalFolderError("Ascent recordings");
            if (ascentSet) cfg.AscentFolder = ascent;
            var clipsSet = ConfigSaveGuards.TryResolveFolderWrite(body.ClipsFolder, out var clips);
            if (clipsSet && IsNewNonLocalFolder(clips, cfg.ClipsFolder)) return NonLocalFolderError("clips");
            if (clipsSet) cfg.ClipsFolder = clips;
            if ((ascentSet || clipsSet) && ClipsFolderConflict(cfg) is { } conflict) return conflict;
            // Server-side clamp mirroring the Settings page (100–50000 MB). This value
            // feeds EnforceFolderSizeLimitAsync — an unclamped 0 (or negative) would
            // make the next clip extraction delete EVERY clip in the folder.
            if (body.ClipsMaxSizeMb is not null) cfg.ClipsMaxSizeMb = Math.Clamp(body.ClipsMaxSizeMb.Value, 100, 50_000);
            if (body.BackupEnabled is not null) cfg.BackupEnabled = body.BackupEnabled.Value;
            if (ConfigSaveGuards.TryResolveFolderWrite(body.BackupFolder, out var backup))
            {
                if (IsNewNonLocalFolder(backup, cfg.BackupFolder)) return NonLocalFolderError("backups");
                cfg.BackupFolder = backup;
            }
            if (body.TiltFixMode is not null) cfg.TiltFixMode = body.TiltFixMode.Value;
            if (body.RequireReviewNotes is not null) cfg.RequireReviewNotes = body.RequireReviewNotes.Value;
            if (body.SidebarAnimationEnabled is not null) cfg.SidebarAnimationEnabled = body.SidebarAnimationEnabled.Value;
            // Window size: null/blank = unchanged; "default" stores "" (built-in size);
            // "maximized" / "WxH" store as-is; garbage is rejected (unchanged).
            if (ConfigSaveGuards.TryResolveWindowResolution(body.WindowResolution, out var windowRes)) cfg.WindowResolution = windowRes;
            if (body.MinimizeDuringGame is not null) cfg.MinimizeDuringGame = body.MinimizeDuringGame.Value;
            if (body.AutoTimelineClippingEnabled is not null) cfg.AutoTimelineClippingEnabled = body.AutoTimelineClippingEnabled.Value;
            if (body.AutoTimelineClippingHintDismissed is not null) cfg.AutoTimelineClippingHintDismissed = body.AutoTimelineClippingHintDismissed.Value;
            if (body.AutoClipObjectivesEnabled is not null) cfg.AutoClipObjectivesEnabled = body.AutoClipObjectivesEnabled.Value;
            if (body.AutoMatchupNotesEnabled is not null) cfg.AutoMatchupNotesEnabled = body.AutoMatchupNotesEnabled.Value;
            if (body.FirstReviewTutorialStep is not null) cfg.FirstReviewTutorialStep = body.FirstReviewTutorialStep.Trim();
            if (body.FirstReviewTutorialCompleted is not null) cfg.FirstReviewTutorialCompleted = body.FirstReviewTutorialCompleted.Value;
            if (body.FirstReviewTutorialDismissed is not null) cfg.FirstReviewTutorialDismissed = body.FirstReviewTutorialDismissed.Value;
            if (body.FirstReviewTutorialObjectiveId is not null) cfg.FirstReviewTutorialObjectiveId = Math.Max(0, body.FirstReviewTutorialObjectiveId.Value);
            if (body.FirstReviewTutorialGameId is not null) cfg.FirstReviewTutorialGameId = Math.Max(0, body.FirstReviewTutorialGameId.Value);
            // RiotId / Region: null OR empty = leave unchanged (P-020 clobber guard) — a save
            // before the page hydrates sends "" / the select default and must NOT blank a
            // configured account. Sign-out, not an empty Save, clears these.
            if (ConfigSaveGuards.TryResolveTextWrite(body.RiotId, out var riotId)) cfg.RiotId = riotId;
            // Region is lower-cased on Save (mirror SettingsViewModel).
            if (ConfigSaveGuards.TryResolveTextWrite(body.Region, out var region)) cfg.RiotRegion = region.ToLowerInvariant();
            if (body.PrimaryRole is not null) cfg.PrimaryRole = body.PrimaryRole;
            // Onboarding ROLE-FINISH writes land here: the wizard's final step saves
            // PrimaryRole on both paths, and on the SKIP path also stamps
            // OnboardingSkipped=true (mirror OnboardingViewModel.FinishRoleAsync). The
            // login path leaves this null — /api/auth/resolve already set it false.
            if (body.OnboardingSkipped is not null) cfg.OnboardingSkipped = body.OnboardingSkipped.Value;
            return null;
    }

    // Revu writes to and scans these folders on its own, every launch. A folder on a network
    // share (\\server\share) would make it connect to that host, and the automatic backup
    // would copy the whole database there, so a NEWLY chosen folder must be on this PC
    // (LocalPathPolicy). A value saved before this check is left alone, so the Settings page
    // sending it back unchanged never blocks an unrelated save.
    internal static bool IsNewNonLocalFolder(string resolved, string? current) =>
        resolved.Length > 0
        && !string.Equals(resolved, (current ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && !LocalPathPolicy.IsLocal(resolved);

    private static string NonLocalFolderError(string purpose) =>
        $"Choose a folder on this PC for your {purpose}. Network share paths aren't supported.";

    // H5: clip cleanup deletes old clips from the clips folder, so that folder must never
    // be, contain, or sit inside a folder of game recordings (the Ascent folder or Revu's
    // own recordings folder). A blank clips folder means the default one.
    internal static string? ClipsFolderConflict(Revu.Core.Models.AppConfig cfg)
    {
        var clips = string.IsNullOrWhiteSpace(cfg.ClipsFolder) ? AppDataPaths.ClipsDirectory : cfg.ClipsFolder;
        if (!string.IsNullOrWhiteSpace(cfg.AscentFolder) && LocalPathPolicy.FoldersOverlap(clips, cfg.AscentFolder))
            return "Your clips folder can't be the same as, inside, or contain your Ascent recordings folder. Choose a separate folder for clips.";
        if (LocalPathPolicy.FoldersOverlap(clips, AppDataPaths.RecordingsDirectory))
            return "Your clips folder can't be the same as, inside, or contain Revu's recordings folder. Choose a separate folder for clips.";
        return null;
    }

    /// <summary>
    /// POST /api/update/download as accepted work: starts (or joins) the one update download
    /// and waits for it while the client is listening. A client that gives up gets 499 while the
    /// download continues; asking again returns the same download's result.
    /// </summary>
    internal static async Task<IResult> DownloadUpdateAsync(
        Func<Action<int>?, CancellationToken, Task<UpdateDownloadResult>> download, SidecarJobs jobs,
        SidecarEventHub events, JsonSerializerOptions jsonOptions, ILogger log, CancellationToken ct)
    {
        var job = jobs.Start("update-download", "update download", async stopping =>
        {
            var lastPercent = -1;
            var result = await download(percent =>
            {
                // Velopack reports often; one event per whole percent is plenty.
                if (percent == lastPercent) return;
                lastPercent = percent;
                events.Publish("updateDownloadProgress", new { percent, finished = false });
            }, stopping);
            events.Publish("updateDownloadProgress", new
            {
                percent = result.Ok ? 100 : Math.Max(lastPercent, 0), finished = true, ok = result.Ok,
                version = result.Version, message = result.Message,
            });
            return result;
        });
        if (job is null)
            return Results.Json(new UpdateDownloadResult(false, null, null, "Revu is closing; the download was not started."),
                jsonOptions, statusCode: 503);

        try
        {
            return Results.Json(await job.WaitAsync(ct), jsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogInformation("Update download: the client stopped waiting; the download keeps running");
            return Results.Json(new UpdateDownloadResult(false, null, null, "The request was cancelled."), jsonOptions, statusCode: 499);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new UpdateDownloadResult(false, null, null, "Download stopped."), jsonOptions, statusCode: 503);
        }
    }

    /// <summary>Documents\Revu: where "Export all reviews" saves. The desktop host opens the
    /// same fixed folder (app.getPath('documents')), never a path the page supplies.</summary>
    internal static string ReviewExportFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "Revu");
    }

    internal static async Task<IResult> SaveReviewExportAsync(IReviewExportService export, string folder,
        JsonSerializerOptions jsonOptions, ILogger log, CancellationToken ct)
    {
        try
        {
            var saved = await export.ExportAllToFolderAsync(folder, ct);
            log.LogInformation("Review export saved ({Games} games) -> {Path}", saved.GameCount, saved.Path);
            return Results.Json(new { ok = true, path = saved.Path, fileName = saved.FileName, games = saved.GameCount }, jsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogInformation("Review export cancelled by the caller; nothing saved");
            return Results.Json(new { ok = false, error = "The request was cancelled." }, jsonOptions, statusCode: 499);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Review export could not be written to {Folder}", folder);
            return Results.Json(new { ok = false, error = $"Couldn't save the export to {folder}. Check that the folder can be written to." },
                jsonOptions, statusCode: 422);
        }
    }
}
