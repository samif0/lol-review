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
        app.MapPost("/api/update/download", async (UpdateService upd) =>
            Results.Json(await upd.DownloadAsync(), jsonOptions));

        // ── GET /api/settings/export (token-gated): build the Markdown review export ──
        // Pure READ: IReviewExportService.ExportAllAsync bundles games/notes/prompts/
        // objectives/tags/matchup notes/VOD links/bookmarks into a single Markdown
        // string. The sidecar does NOT write the file — it returns { markdown, fileName }
        // and the desktop main process saves it via the native save-file dialog. The
        // suggested filename mirrors the WinUI picker (revu-review-export-{yyyyMMdd-HHmm}).
        app.MapGet("/api/settings/export", async (IReviewExportService export, ILogger<Program> log, CancellationToken ct) =>
        {
            var markdown = await export.ExportAllAsync(ct);
            var fileName = $"revu-review-export-{DateTime.Now:yyyyMMdd-HHmm}.md";
            log.LogInformation("Review export built ({Chars} chars) -> {FileName}", markdown.Length, fileName);
            return Results.Json(new { ok = true, markdown, fileName }, jsonOptions);
        });

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
        // with a chosen backup. Core RestoreFromBackupAsync takes a PRE-RESTORE safety
        // backup FIRST (returns its path), then swaps in the chosen file. The desktop main process
        // relaunches on success. Returns { ok, preRestoreBackupPath } or { ok:false, error }.
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
        app.MapPost("/api/config/save", async (SaveConfigBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null) return Results.BadRequest(new { error = "body required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            var cfg = await w.Config.LoadAsync();

            // Folder fields: null/empty = unchanged; sentinel = explicit clear; else set.
            if (ConfigSaveGuards.TryResolveFolderWrite(body.AscentFolder, out var ascent)) cfg.AscentFolder = ascent;
            if (ConfigSaveGuards.TryResolveFolderWrite(body.ClipsFolder, out var clips)) cfg.ClipsFolder = clips;
            // Server-side clamp mirroring the Settings page (100–50000 MB). This value
            // feeds EnforceFolderSizeLimitAsync — an unclamped 0 (or negative) would
            // make the next clip extraction delete EVERY clip in the folder.
            if (body.ClipsMaxSizeMb is not null) cfg.ClipsMaxSizeMb = Math.Clamp(body.ClipsMaxSizeMb.Value, 100, 50_000);
            if (body.BackupEnabled is not null) cfg.BackupEnabled = body.BackupEnabled.Value;
            if (ConfigSaveGuards.TryResolveFolderWrite(body.BackupFolder, out var backup)) cfg.BackupFolder = backup;
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

            await w.Config.SaveAsync(cfg);
            log.LogInformation("Config saved via sidecar.");
            return Results.Json(new { ok = true }, jsonOptions);
        });

    }
}
