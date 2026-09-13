#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapPlanning(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/pregame[?myChampion=&enemy=&role=&participantMap=] (token-gated) ──
        // The STATIC champ-select / in-game intel deck: rotating intel cards, active +
        // priority objectives + their pre-game custom prompts (draft answers prefilled),
        // saved matchup notes, the intent carry-over seeds (carry / objective / adherence)
        // with provenance, the latest if-then plan, and the mood/intention gates. Mirrors
        // the READ half of PreGameDialogViewModel.LoadAsync. The LIVE champ-select data
        // (my champ / enemy / role / map → live matchup + 2v2 pairing) arrives over the
        // SSE channel; the query params (or the live state) seed the matchup card at load.
        app.MapGet("/api/pregame", async (
            string? myChampion, string? enemy, string? role, string? participantMap,
            PreGameSnapshotBuilder builder, CancellationToken ct) =>
        {
            var snapshot = await builder.BuildAsync(myChampion, enemy, role, participantMap, ct);
            return Results.Json(snapshot, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // DEFERRED PRE-GAME SNAPSHOT WRITES (Batch 5). These are NOT DB writes — they
        // stage the user's champ-select choices (mood / intent / practiced objective ids)
        // into the in-memory LcuLiveState, which the SidecarGameFlowCoordinator reads and
        // persists to session_log at game END (exactly like the WinUI PreGameDialogViewModel
        // statics → ShellViewModel hop). The one champ-select write that DOES hit the DB —
        // the per-prompt draft answer — reuses the existing POST /api/prompt/answer/save's
        // sibling draft path below. No backup guard needed (no DB mutation here).
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/pregame/mood  { mood }  — 1..5 (Tilted/Off/Neutral/Good/LockedIn).
        // Out-of-range values are rejected (0 = "unset" is allowed): the staged value
        // flows unchecked into session_log.pre_game_mood at EOG, so a buggy caller
        // would silently pollute mood analytics.
        app.MapPost("/api/pregame/mood", (PreGameMoodBody body, LcuLiveState live) =>
        {
            var mood = body?.Mood ?? 0;
            if (mood is < 0 or > 5)
                return Results.BadRequest(new { error = "mood must be 0 (unset) or 1..5" });
            live.SetMood(mood);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/pregame/intent  { intention?, source?, cleared? } — the THIS GAME'S
        // INTENT card state. source ∈ carry|objective|edited (adherence maps to objective).
        app.MapPost("/api/pregame/intent", (PreGameIntentBody body, LcuLiveState live) =>
        {
            live.SetIntent(body?.Intention ?? "", body?.Source ?? "", body?.Cleared ?? false);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/pregame/practiced  { objectiveIds:[...] } — the practiced-toggle set.
        app.MapPost("/api/pregame/practiced", (PreGamePracticedBody body, LcuLiveState live) =>
        {
            live.SetPracticed(body?.ObjectiveIds ?? new List<long>());
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/pregame/prompt/draft  { promptId, text } — autosave a champ-select
        // prompt answer to pre_game_draft_prompts under the current live session key, so
        // it survives a webview reload and is promoted to the game row at EOG. This is the
        // ONE champ-select interaction that writes to the DB (a single upsert). Mirrors
        // PreGamePromptAnswer.AnswerText → SaveDraftAnswerAsync. Backup-guarded like the
        // other writes; no-op (ok:false) when no live champ-select session is active.
        app.MapPost("/api/pregame/prompt/draft", async (PreGameDraftBody body, WriteServices w, LcuLiveState live, ILogger<Program> log) =>
        {
            if (body is null || body.PromptId <= 0)
                return Results.BadRequest(new { error = "promptId required" });
            var sessionKey = live.SessionKey;
            if (string.IsNullOrEmpty(sessionKey))
                return Results.Json(new { ok = false, error = "no active champ-select session" }, jsonOptions);
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Prompts.SaveDraftAnswerAsync(sessionKey, body.PromptId, body.Text ?? "");
            log.LogInformation("Pre-game prompt draft saved: prompt {PromptId} (session {Session})", body.PromptId, sessionKey);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ── GET /api/rules (token-gated): list of the user's rules ───────────────────
        // Lists active + inactive rules with their live RULE CHECK state + behavioral
        // evidence. The full CRUD (create / update / toggle / delete) lives in the four
        // POST endpoints below; the snapshot is the read half.
        app.MapGet("/api/rules", async (RulesSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // ─────────────────────────────────────────────────────────────────────────────
        // Rules CRUD writes (token-gated). Reuse RulesRepository (the schema-tolerant
        // repo the WinUI app writes through) verbatim — no hand-rolled SQL. Each takes
        // the one-time session safety backup before writing, then the frontend refetches
        // GET /api/rules. loss_streak encodes its optional cooldown as "threshold:minutes"
        // in conditionValue, exactly like RulesViewModel.CreateRuleAsync builds it (the
        // frontend assembles that string before posting).
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/rule/create  { name, ruleType?, conditionValue?, description?, replacementPlan? }
        app.MapPost("/api/rule/create", async (CreateRuleBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var id = await w.Rules.CreateAsync(
                body.Name.Trim(),
                body.Description?.Trim() ?? "",
                string.IsNullOrWhiteSpace(body.RuleType) ? "custom" : body.RuleType,
                body.ConditionValue?.Trim() ?? "",
                body.ReplacementPlan?.Trim() ?? "");
            log.LogInformation("Rule created: {Id} '{Name}' ({Type})", id, body.Name, body.RuleType);
            return Results.Json(new { ok = true, id }, jsonOptions);
        });

        // POST /api/rule/update  { id, name, ruleType?, conditionValue?, description?, replacementPlan? }
        app.MapPost("/api/rule/update", async (UpdateRuleBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0 || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "id and name required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Rules.UpdateAsync(
                body.Id,
                body.Name.Trim(),
                body.Description?.Trim() ?? "",
                string.IsNullOrWhiteSpace(body.RuleType) ? "custom" : body.RuleType,
                body.ConditionValue?.Trim() ?? "",
                body.ReplacementPlan?.Trim() ?? "");
            log.LogInformation("Rule updated: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/rule/toggle  { id } — flip active/inactive (RulesRepository.ToggleAsync).
        app.MapPost("/api/rule/toggle", async (RuleIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Rules.ToggleAsync(body.Id);
            log.LogInformation("Rule toggled: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/rule/enforce  { id, enforce } — v3.7: flip a rule between display-only
        // and ENFORCED (the sidecar cancels the queue while it is tripped). Types the
        // enforcer can never act on (custom, min_mental) are rejected so a rule can't be
        // flagged into a state it can never hold.
        app.MapPost("/api/rule/enforce", async (RuleEnforceBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            var rule = await w.Rules.GetAsync(body.Id);
            if (rule is null) return Results.NotFound(new { error = "rule not found" });
            if (body.Enforce && !HardStopPolicy.CanEnforce(rule.RuleType))
                return Results.BadRequest(new { error = $"a {rule.RuleType} rule cannot be enforced" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Rules.SetEnforceAsync(body.Id, body.Enforce);
            log.LogInformation("Rule {Id} enforce = {Enforce}", body.Id, body.Enforce);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/hardstop/override  { ruleId } — v3.7: the player is queuing anyway.
        // Logs the override (silences that rule's enforcement for the rest of the local
        // day; the Rules page counts it) and clears the replayed lock snapshot.
        app.MapPost("/api/hardstop/override", async (HardStopOverrideBody body, WriteServices w, HardStopEnforcer enforcer, ILogger<Program> log) =>
        {
            if (body is null || body.RuleId <= 0) return Results.BadRequest(new { error = "ruleId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await enforcer.OverrideAsync(body.RuleId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/rule/delete  { id } — HARD delete (DELETE FROM rules). The frontend
        // confirms before calling; the first-write safety backup is the net here.
        app.MapPost("/api/rule/delete", async (RuleIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Id <= 0) return Results.BadRequest(new { error = "id required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Rules.DeleteAsync(body.Id);
            log.LogInformation("Rule deleted: {Id}", body.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ── GET /api/tiltcheck (token-gated): read-only tilt-reset history + stats ────
        // Recent rituals (≤20), aggregate before/after stats, and the latest if-then
        // plan (≤14d). The reset RITUAL is a WRITE the frontend runs via
        // invoke('run_reset', …) → POST /api/reset; this snapshot is READ-ONLY.
        app.MapGet("/api/tiltcheck", async (TiltCheckSnapshotBuilder b, CancellationToken ct) =>
            Results.Json(await b.BuildAsync(ct), jsonOptions));

        // POST /api/pregame/ifthen  { plan } — R-002: author/confirm an if-then plan in the
        // PRE-CUE window (champ-select), so the plan PRE-DATES the trigger (Gollwitzer &
        // Sheeran / Schweiger Gallo 2009 — delegate the initiation decision to the planning
        // moment). Writes the plan to its existing home, tilt_checks.if_then_plan, as a
        // minimal row (emotion="pregame_plan") so GetLatestPlanAsync (≤14d) surfaces it on
        // the pregame intent card's ACTIVE PLAN row — closing the write-only/reactive-timing
        // gap (the plan was previously writable only from the post-loss Tilt reset ritual).
        // Descriptive, never scored. Backup-guarded like the other writes.
        app.MapPost("/api/pregame/ifthen", async (PreGameIfThenBody body, WriteServices w, ILogger<Program> log) =>
        {
            var plan = body?.Plan?.Trim() ?? "";
            if (plan.Length == 0)
                return Results.BadRequest(new { error = "plan required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.TiltChecks.SaveAsync(
                emotion: "pregame_plan",
                intensityBefore: 0,
                ifThenPlan: plan);
            log.LogInformation("Pre-queue if-then plan saved");
            return Results.Json(new { ok = true }, jsonOptions);
        });
    }
}
