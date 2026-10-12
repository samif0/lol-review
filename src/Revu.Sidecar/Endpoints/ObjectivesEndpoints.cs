#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapObjectives(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/objectives (token-gated): read-only objectives snapshot ─────────
        app.MapGet("/api/objectives", async (ObjectivesSnapshotBuilder builder, CancellationToken ct) =>
        {
            var snapshot = await builder.BuildAsync(ct);
            return Results.Json(snapshot, jsonOptions);
        });

        // ── GET /api/objective/games?id=N (token-gated): one objective's linked games +
        // its evidence ledger. Read-only drill-down reached from an objective card; the
        // per-row Watch VOD / Review jumps are plain frontend navigation.
        app.MapGet("/api/objective/games", async (long id, ObjectiveGamesSnapshotBuilder builder, CancellationToken ct) =>
            Results.Json(await builder.BuildAsync(id, ct), jsonOptions));

        // ── GET /api/objective/notes?id=N (token-gated): one objective's aggregated
        // review notes + execution notes + clips/bookmarks. Read-only aggregator; each
        // row jumps back to review / vodplayer via plain frontend navigation.
        app.MapGet("/api/objective/notes", async (long id, ObjectiveNotesSnapshotBuilder builder, CancellationToken ct) =>
            Results.Json(await builder.BuildAsync(id, ct), jsonOptions));

        // ── GET /api/objective?id=N (token-gated): FULL edit hydration for one objective.
        // Mirrors ObjectivesViewModel.BeginEditObjectiveAsync — core fields + multi-phase
        // flags + structured criterion + focus phase + custom prompts + champion gate +
        // the played-champion typeahead list. Read-only; the actual save goes through the
        // POST /api/objective/create|update write endpoints. 404 when the id doesn't
        // resolve. The criteria-metric picker options ride along so the form can build its
        // dropdown without hardcoding the metric list.
        app.MapGet("/api/objective", async (long id, ObjectiveEditSnapshotBuilder builder, CancellationToken ct) =>
        {
            var dto = await builder.BuildAsync(id, ct);
            if (dto is null) return Results.NotFound(new { error = "objective not found" });
            return Results.Json(
                new { objective = dto, criteriaMetrics = ObjectiveEditSnapshotBuilder.BuildCriteriaMetricOptions() },
                jsonOptions);
        });

        // POST /api/objective/create — create an objective AND persist its full editing
        // surface (prompts / champions / focus-phase / structured criterion). Mirrors
        // ObjectivesViewModel.CreateObjectiveAsync: at least one practice phase must be
        // checked; minis clamp target to max(1,N); the side-tables (prompts diff-save,
        // champion gate replace, focus phase, criterion) are written after the core row.
        app.MapPost("/api/objective/create", async (CreateObjectiveBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Title))
                return Results.BadRequest(new { error = "title required" });
            // Mirror CanCreate: at least one of pre/in/post must be checked.
            if (!body.PracticePre && !body.PracticeIn && !body.PracticePost)
                return Results.BadRequest(new { error = "at least one practice phase required" });

            await w.BackupGuard.EnsureBackedUpAsync();

            var id = await CreateObjectiveAsync(body, w.Objectives, w.Prompts, w.BeginWriteSession, log);

            log.LogInformation("Objective created: {Id} '{Title}'", id, body.Title);
            return Results.Json(new { ok = true, id }, jsonOptions);
        });

        // POST /api/objective/update — update an objective AND its full editing surface.
        // Mirrors the EditingObjectiveId branch of CreateObjectiveAsync: UpdateWithPhases
        // + UpdateTargetGameCount, then the same side-table persist as create.
        app.MapPost("/api/objective/update", async (UpdateObjectiveBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0 || string.IsNullOrWhiteSpace(body.Title))
                return Results.BadRequest(new { error = "id and title required" });
            if (!body.PracticePre && !body.PracticeIn && !body.PracticePost)
                return Results.BadRequest(new { error = "at least one practice phase required" });

            await w.BackupGuard.EnsureBackedUpAsync();

            await UpdateObjectiveAsync(body, w.Objectives, w.Prompts, w.BeginWriteSession, log);

            log.LogInformation("Objective updated: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/objective/delete  { id } — hard delete. The Core repo cascades the
        // prompts/answers/game_objectives/champion rows in one transaction and detaches
        // clips and evidence. The frontend confirms first; a fresh safety backup is taken
        // right before the delete (the session's first-write backup may be hours old).
        app.MapPost("/api/objective/delete", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await DeleteObjectiveAsync(body.Id, w.Backup, w.Objectives);
            log.LogInformation("Objective deleted: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/objective/priority  { id }
        app.MapPost("/api/objective/priority", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Objectives.SetPriorityAsync(body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/objective/complete  { id }
        app.MapPost("/api/objective/complete", async (ObjectiveIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Objectives.MarkCompleteAsync(body.Id);
            log.LogInformation("Objective completed: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // MANUAL GAME ENTRY (Batch 2). Hand-log a game NOT auto-captured + a minimal
        // review. The Manual Entry page needs the active post-game objectives to render
        // its practiced/note rows, then POSTs the whole form.
        // ─────────────────────────────────────────────────────────────────────────────

        // GET /api/objectives/active — active objectives that show in post-game (pre/in/
        // post), shaped for the Manual Entry objectives card. READ-only; no backup guard.
        // Mirrors ManualEntryDialogViewModel.LoadObjectivesAsync (GetActiveAsync filtered
        // by ObjectivePhases.ShowsInPostGame). Uses the WRITE-graph Objectives repo (read
        // methods on it are still plain SELECTs).
        app.MapGet("/api/objectives/active", async (WriteServices w, ILogger<Program> log) =>
        {
            try
            {
                var active = await w.Objectives.GetActiveAsync();
                // Tracked event tokens per objective (raw types, SPELL_*, TEAMFIGHT). One query
                // for all active objectives; grouped by id. Lets the VOD viewer (a) draw the
                // token chips and (b) know whether an objective tracks TEAMFIGHT — so teamfight
                // zones only stay loud when the focused objective actually tracks them.
                var tokensByObjective = new Dictionary<long, List<string>>();
                try
                {
                    foreach (var (token, objId, _) in await w.Objectives.GetActiveObjectiveEventTokensAsync())
                    {
                        if (!tokensByObjective.TryGetValue(objId, out var list)) { list = new List<string>(); tokensByObjective[objId] = list; }
                        var t = (token ?? "").Trim().ToUpperInvariant();
                        if (t.Length > 0 && !list.Contains(t)) list.Add(t);
                    }
                }
                catch (Exception ex) { log.LogDebug(ex, "Active objectives: token map load failed (degraded)"); }

                var shown = active
                    .Where(o => Revu.Core.Data.Repositories.ObjectivePhases.ShowsInPostGame(o.Phase))
                    .ToList();

                // P-027: ship each objective's custom prompts for the VOD prompt-pickers.
                // Batched — same one-query shape as the event tokens above; the per-
                // objective loop used to cost one round-trip per active objective on the
                // VOD/manual-entry hot path. Failure degrades to empty, never the route.
                IReadOnlyDictionary<long, IReadOnlyList<Revu.Core.Data.Repositories.ObjectivePrompt>> promptsByObjective =
                    new Dictionary<long, IReadOnlyList<Revu.Core.Data.Repositories.ObjectivePrompt>>();
                try
                {
                    promptsByObjective = await w.Prompts.GetPromptsForObjectivesAsync(shown.Select(o => o.Id).ToList());
                }
                catch (Exception px)
                {
                    log.LogDebug(px, "Prompts batch load failed (degraded to empty)");
                }

                var rows = new List<object>();
                foreach (var o in shown)
                {
                    var prompts = promptsByObjective.TryGetValue(o.Id, out var promptList)
                        ? promptList.Select(p => (object)new
                            {
                                promptId = p.Id,
                                label = p.Label,
                                phase = p.Phase,
                            })
                            .ToArray()
                        : Array.Empty<object>();

                    var toks = tokensByObjective.TryGetValue(o.Id, out var l) ? l : new List<string>();
                    rows.Add(new
                    {
                        objectiveId = o.Id,
                        title = o.Title,
                        phaseLabel = Revu.Core.Data.Repositories.ObjectivePhases.ToDisplayLabel(o.Phase),
                        // Type drives the VOD objective-framed viewer's color-by-type chrome
                        // (primary/mental/mini). Additive, read-only — no schema change.
                        type = o.Type,
                        isPriority = o.IsPriority,
                        isMini = o.IsMini,
                        // Tracked tokens → viewer token chips; tracksTeamfight gates whether
                        // teamfight zones stay loud when this objective is focused. Any fight
                        // token the player was IN counts (TEAMFIGHT or a numbers verdict); a
                        // fight without the player is a pin, never a band.
                        trackedTokens = toks,
                        tracksTeamfight = toks.Any(t => t.EndsWith("TEAMFIGHT", StringComparison.Ordinal)
                            && t != Revu.Core.Models.GameEvent.TrackableTokens.AbsentTeamfightToken),
                        prompts,
                    });
                }
                return Results.Json(new { ok = true, objectives = rows }, jsonOptions);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Active objectives load failed (degraded to empty)");
                return Results.Json(new { ok = true, objectives = Array.Empty<object>() }, jsonOptions);
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Objective write helpers (shared by /api/objective/create + /update).
    // ─────────────────────────────────────────────────────────────────────────────

    // Mirror ObjectivesViewModel: type-select index/string → the persisted column.
    static string NormalizeObjectiveType(string? type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "mini" or "2" => "mini",
            "mental" or "1" => "mental",
            _ => "primary",
        };
    }

    // POST /api/objective/create minus the HTTP plumbing: the core row and every side
    // table in ONE transaction, so a failure never leaves a half-configured objective
    // (and a retry never duplicates one).
    internal static async Task<long> CreateObjectiveAsync(CreateObjectiveBody body,
        IObjectivesRepository objectives, IPromptsRepository prompts, Func<DbWriteSession> beginSession, ILogger log)
    {
        var type = NormalizeObjectiveType(body.Type);
        var targetGameCount = type == "mini" ? Math.Max(1, body.TargetGameCount) : 0;

        using var session = beginSession();
        var id = await objectives.CreateWithPhasesAndTargetAsync(
            body.Title.Trim(), (body.SkillArea ?? "").Trim(), type,
            (body.CompletionCriteria ?? "").Trim(), (body.Description ?? "").Trim(),
            body.PracticePre, body.PracticeIn, body.PracticePost,
            targetGameCount, session);

        await PersistObjectiveSideTablesAsync(objectives, prompts, session, log, id,
            existing: Array.Empty<Revu.Core.Data.Repositories.ObjectivePrompt>(), body.Prompts, body.Champions,
            body.FocusPhaseIndex, body.CriteriaMetricIndex, body.CriteriaOpIndex, body.CriteriaValueText,
            body.EventTypes);
        session.Commit();
        return id;
    }

    // POST /api/objective/update minus the HTTP plumbing; one transaction like create.
    internal static async Task UpdateObjectiveAsync(UpdateObjectiveBody body,
        IObjectivesRepository objectives, IPromptsRepository prompts, Func<DbWriteSession> beginSession, ILogger log)
    {
        var type = NormalizeObjectiveType(body.Type);
        var targetGameCount = type == "mini" ? Math.Max(1, body.TargetGameCount) : 0;
        // Read the stored prompts before the write session opens (the diff-save base).
        var existing = await prompts.GetPromptsForObjectiveAsync(body.Id);

        using var session = beginSession();
        await objectives.UpdateWithPhasesAsync(
            body.Id, body.Title.Trim(), (body.SkillArea ?? "").Trim(), type,
            (body.CompletionCriteria ?? "").Trim(), (body.Description ?? "").Trim(),
            body.PracticePre, body.PracticeIn, body.PracticePost, session);
        await objectives.UpdateTargetGameCountAsync(body.Id, targetGameCount, session);

        await PersistObjectiveSideTablesAsync(objectives, prompts, session, log, body.Id, existing,
            body.Prompts, body.Champions,
            body.FocusPhaseIndex, body.CriteriaMetricIndex, body.CriteriaOpIndex, body.CriteriaValueText,
            body.EventTypes);
        session.Commit();
    }

    // Snapshot right before the hard delete, like game delete: it drops the objective's
    // prompts and their written answers, and the session's first-write backup may be old.
    internal static async Task DeleteObjectiveAsync(long objectiveId, IBackupService backup, IObjectivesRepository objectives)
    {
        await backup.CreateSafetyBackupAsync($"delete-objective-{objectiveId}");
        await objectives.DeleteAsync(objectiveId);
    }

    // Persist the side-tables of an objective AFTER its core row exists, in the same
    // order the WinUI VM does: prompts diff-save → champion gate replace → focus phase
    // → structured criterion. Reuses Revu.Core write methods verbatim, all inside the
    // caller's write session.
    static async Task PersistObjectiveSideTablesAsync(
        IObjectivesRepository objectives,
        IPromptsRepository promptsRepo,
        DbWriteSession session,
        ILogger log,
        long objectiveId,
        IReadOnlyList<Revu.Core.Data.Repositories.ObjectivePrompt> existing,
        List<ObjectivePromptBody>? prompts,
        List<string>? champions,
        int focusPhaseIndex,
        int criteriaMetricIndex,
        int criteriaOpIndex,
        string? criteriaValueText,
        List<string>? eventTypes = null)
    {
        // ── Custom prompts: diff-save against the stored rows. Mirrors
        //    ObjectivesViewModel.SavePromptsForObjectiveAsync: blank labels never
        //    persist; unchanged rows skip the update; removed rows are deleted;
        //    sortOrder = the prompt's index in the submitted list.
        var draft = prompts ?? new List<ObjectivePromptBody>();
        var existingById = existing.ToDictionary(p => p.Id);
        var keptIds = new HashSet<long>();

        for (var i = 0; i < draft.Count; i++)
        {
            var label = (draft[i].Label ?? "").Trim();
            if (string.IsNullOrEmpty(label)) continue; // blank rows don't persist

            var phase = Revu.Core.Data.Repositories.ObjectivePhases.Normalize(draft[i].Phase);
            var originalId = draft[i].Id;

            if (originalId > 0 && existingById.TryGetValue(originalId, out var prior))
            {
                keptIds.Add(prior.Id);
                // Only write when something actually changed (avoid updated_at churn).
                if (prior.Phase != phase || prior.Label != label || prior.SortOrder != i)
                {
                    await promptsRepo.UpdatePromptAsync(prior.Id, phase, label, i, session);
                }
            }
            else
            {
                var newId = await promptsRepo.CreatePromptAsync(objectiveId, phase, label, i, session);
                keptIds.Add(newId);
            }
        }

        // Anything on disk but not in the submitted list anymore was deleted.
        foreach (var prior in existing)
        {
            if (!keptIds.Contains(prior.Id))
            {
                await promptsRepo.DeletePromptAsync(prior.Id, session);
            }
        }

        // ── Champion gate: replace wholesale (empty list = applies to all champions).
        //    De-dupe case-insensitively, preserve caller casing (mirror AddChampion).
        var champs = new List<string>();
        foreach (var c in champions ?? new List<string>())
        {
            var name = (c ?? "").Trim();
            if (name.Length == 0) continue;
            if (champs.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
            champs.Add(name);
        }
        await objectives.SetChampionsForObjectiveAsync(objectiveId, champs, session);

        // ── Event-token gate: replace wholesale (empty list = tracks no events). The
        //    repo validates each token against the trackable vocabulary, so junk is
        //    dropped silently; we just forward the submitted list. Run as an optional
        //    step so a token-table problem (e.g. a DB that somehow missed the v8
        //    migration) never fails the whole objective save — the objective + its other
        //    side tables still persist; only the event-token tie is rolled back.
        var tokenFailure = await session.TryStepAsync(() => objectives.SetEventTokensForObjectiveAsync(
            objectiveId, eventTypes ?? new List<string>(), session));
        if (tokenFailure is not null)
        {
            log.LogWarning(tokenFailure, "Objective {ObjectiveId}: event-token persist failed (skipped)", objectiveId);
        }

        // ── Auto-clip focus phase (0 Auto / 1 Laning / 2 Mid-late / 3 Teamfight / 4 Any).
        await objectives.UpdateFocusPhaseAsync(
            objectiveId,
            Revu.Core.Data.Repositories.ObjectiveFocusPhases.FromIndex(focusPhaseIndex),
            session);

        // ── Structured criterion. Metric index 0 ("Free text only") clears it; 1..N
        //    map to ObjectiveCriteria.Metrics[index-1]. Op index 1 = "<=" else ">=".
        //    Value parses invariant first then current culture, defaults 0 (mirror VM).
        var metricKey =
            criteriaMetricIndex > 0 && criteriaMetricIndex <= Revu.Core.Services.ObjectiveCriteria.Metrics.Count
                ? Revu.Core.Services.ObjectiveCriteria.Metrics[criteriaMetricIndex - 1].Key
                : "";
        var valueText = (criteriaValueText ?? "").Trim();
        if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var criteriaValue))
        {
            double.TryParse(valueText, out criteriaValue);
        }
        await objectives.UpdateCriteriaAsync(
            objectiveId,
            metricKey,
            criteriaOpIndex == 1 ? "<=" : ">=",
            criteriaValue,
            session);
    }
}
