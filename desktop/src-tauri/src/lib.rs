// Revu desktop — Tauri shell.
//
// The UI (desktop/index.html + app.js, the glass-aurora dashboard) calls these
// commands via invoke(). Each command proxies to the C# Revu.Sidecar over an
// authenticated localhost request. The sidecar is spawned on setup and we only
// show the window once it reports healthy.

mod sidecar;

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use tauri::{Emitter, Manager};

/// Guards against spawning more than one LCU SSE proxy. The pre-game / in-game
/// pages both call start_lcu_events on load; the first wins and the streamer runs
/// for the app's lifetime, re-emitting every event to whichever window is showing.
static LCU_EVENTS_STARTED: AtomicBool = AtomicBool::new(false);

/// Minimal percent-encoder for query-string VALUES (the champ-select participant
/// map is JSON, so it carries `{`, `"`, `:`, `,` etc. that must be escaped).
/// Keeps the RFC 3986 unreserved set verbatim; everything else → %XX. Avoids a
/// dependency for the handful of pre-game query params that need it.
fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for &b in s.as_bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(b as char);
            }
            _ => out.push_str(&format!("%{:02X}", b)),
        }
    }
    out
}

/// Returns the full dashboard snapshot JSON (see Revu.Sidecar /api/dashboard).
#[tauri::command]
async fn get_dashboard() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/dashboard").await
}

// ── LCU live channel (Batch 5) ───────────────────────────────────────────────

/// Returns the static champ-select / in-game intel snapshot (rotating intel deck,
/// active+priority objectives + their pre-game prompts, matchup notes, intent
/// carry-over seeds, latest if-then plan, mood/intention gates). Optional champ-
/// select context (my_champion / enemy / role / participant_map) seeds the matchup
/// card; the LIVE updates arrive over the SSE channel (start_lcu_events). See
/// Revu.Sidecar GET /api/pregame.
#[tauri::command]
async fn get_pregame(
    my_champion: Option<String>,
    enemy: Option<String>,
    role: Option<String>,
    participant_map: Option<String>,
) -> Result<serde_json::Value, String> {
    let mut query: Vec<String> = Vec::new();
    let push = |q: &mut Vec<String>, k: &str, v: &Option<String>| {
        if let Some(val) = v.as_deref() {
            if !val.is_empty() {
                q.push(format!("{}={}", k, urlencode(val)));
            }
        }
    };
    push(&mut query, "myChampion", &my_champion);
    push(&mut query, "enemy", &enemy);
    push(&mut query, "role", &role);
    push(&mut query, "participantMap", &participant_map);
    let path = if query.is_empty() {
        "/api/pregame".to_string()
    } else {
        format!("/api/pregame?{}", query.join("&"))
    };
    sidecar::get_json(&path).await
}

/// Opens the sidecar's authenticated SSE stream (/api/events) and re-emits every
/// LCU message to the webview as a Tauri event named "lcu-event" with payload
/// `{ type, payload }`. Idempotent — only the first call spins the streamer; later
/// calls (the pre-game and in-game pages both call this on load) are no-ops. The
/// streamer self-heals across sidecar restarts and runs for the app's lifetime.
/// The browser can't open an authenticated EventSource itself (no header support),
/// so the Tauri host owns the connection and the bearer token never reaches JS.
#[tauri::command]
async fn start_lcu_events(app: tauri::AppHandle) -> Result<(), String> {
    // Compare-and-swap: only the first caller proceeds.
    if LCU_EVENTS_STARTED
        .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
        .is_err()
    {
        return Ok(());
    }

    tauri::async_runtime::spawn(async move {
        sidecar::stream_sse(
            "/api/events",
            |evt| {
                // Forward as { type, payload } so the frontend has one handler.
                let _ = app.emit(
                    "lcu-event",
                    serde_json::json!({ "type": evt.event_type, "payload": evt.data }),
                );
            },
            // Run for the app's lifetime — there is no stop signal today.
            || false,
        )
        .await;
        // If stream_sse ever returns, allow a future restart.
        LCU_EVENTS_STARTED.store(false, Ordering::SeqCst);
    });

    Ok(())
}

/// Stages the pre-game MOOD selection (1..5) into the sidecar's live state; it is
/// written to session_log at game end. See Revu.Sidecar POST /api/pregame/mood.
#[tauri::command]
async fn set_pregame_mood(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pregame/mood", payload).await
}

/// Stages THIS GAME'S INTENT ({ intention?, source?, cleared? }) into the live
/// state; written to session_log at game end. See Revu.Sidecar POST /api/pregame/intent.
#[tauri::command]
async fn set_pregame_intent(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pregame/intent", payload).await
}

/// Stages the set of PRACTICED objective ids ({ objectiveIds:[...] }) into the
/// live state; recorded per-objective at game end. See Revu.Sidecar POST
/// /api/pregame/practiced.
#[tauri::command]
async fn set_pregame_practiced(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pregame/practiced", payload).await
}

/// Autosaves a champ-select prompt answer ({ promptId, text }) to the draft table
/// under the live session key; promoted to the game row at end-of-game. Returns
/// {ok:false} when no champ-select session is active. See Revu.Sidecar POST
/// /api/pregame/prompt/draft.
#[tauri::command]
async fn save_pregame_draft(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pregame/prompt/draft", payload).await
}

/// Returns the games-workspace snapshot JSON (see Revu.Sidecar /api/games).
/// `view` selects one of the four list views ("queue" | "today" | "history" |
/// "vod"; unknown/None → queue server-side); `page` is the zero-based History
/// page (offset page*30), ignored by the single-shot views.
#[tauri::command]
async fn get_games(
    view: Option<String>,
    page: Option<i64>,
) -> Result<serde_json::Value, String> {
    let mut query: Vec<String> = Vec::new();
    if let Some(v) = view.as_deref() {
        if !v.is_empty() {
            query.push(format!("view={}", v));
        }
    }
    if let Some(p) = page {
        if p > 0 {
            query.push(format!("page={}", p));
        }
    }
    let path = if query.is_empty() {
        "/api/games".to_string()
    } else {
        format!("/api/games?{}", query.join("&"))
    };
    sidecar::get_json(&path).await
}

/// Returns the objectives snapshot JSON (see Revu.Sidecar /api/objectives).
#[tauri::command]
async fn get_objectives() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/objectives").await
}

/// Returns one objective's linked games + evidence ledger (see Revu.Sidecar
/// /api/objective/games?id=N). Read-only drill-down from an objective card.
#[tauri::command]
async fn get_objective_games(id: i64) -> Result<serde_json::Value, String> {
    sidecar::get_json(&format!("/api/objective/games?id={id}")).await
}

/// Returns one objective's aggregated review notes + execution notes + clips
/// (see Revu.Sidecar /api/objective/notes?id=N). Read-only aggregator.
#[tauri::command]
async fn get_objective_notes(id: i64) -> Result<serde_json::Value, String> {
    sidecar::get_json(&format!("/api/objective/notes?id={id}")).await
}

/// Returns ONE objective's full edit hydration (core fields + multi-phase flags +
/// structured criterion + focus phase + custom prompts + champion gate + the
/// played-champion typeahead list + criteria-metric picker options). Read-only;
/// the Edit form posts back through create_objective / update_objective. See
/// Revu.Sidecar GET /api/objective?id=N.
#[tauri::command]
async fn get_objective(id: i64) -> Result<serde_json::Value, String> {
    sidecar::get_json(&format!("/api/objective?id={id}")).await
}

/// Returns the review snapshot JSON. With game_id, loads that specific game's
/// review (clicking a game row); without, the sample subject.
#[tauri::command]
async fn get_review(game_id: Option<i64>) -> Result<serde_json::Value, String> {
    let path = match game_id {
        Some(id) if id > 0 => format!("/api/review?gameId={id}"),
        _ => "/api/review".to_string(),
    };
    sidecar::get_json(&path).await
}

/// Returns the rules snapshot JSON (see Revu.Sidecar /api/rules).
#[tauri::command]
async fn get_rules() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/rules").await
}

/// Returns the tilt-check snapshot JSON (see Revu.Sidecar /api/tiltcheck).
#[tauri::command]
async fn get_tiltcheck() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/tiltcheck").await
}

/// Returns the patterns snapshot JSON (see Revu.Sidecar /api/patterns).
#[tauri::command]
async fn get_patterns() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/patterns").await
}

/// Returns the VOD snapshot JSON (file path + bookmarks) for a game.
#[tauri::command]
async fn get_vod(game_id: i64) -> Result<serde_json::Value, String> {
    sidecar::get_json(&format!("/api/vod?gameId={game_id}")).await
}

/// Returns the app-config snapshot JSON (see Revu.Sidecar /api/config).
/// Editable Settings fields + the cross-page reads (Ascent reminder, auto-clip
/// hint). Secrets are excluded server-side.
#[tauri::command]
async fn get_config() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/config").await
}

// ── WRITE commands — each POSTs a JSON payload to the sidecar write endpoint and
// returns the response. The frontend passes the body; the UI re-fetches after.

#[tauri::command]
async fn start_block(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/block/start", payload).await
}

#[tauri::command]
async fn end_block(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/block/end", payload).await
}

#[tauri::command]
async fn save_review(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/review/save", payload).await
}

#[tauri::command]
async fn skip_review(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/review/skip", payload).await
}

/// Delete a saved review ({gameId}), returning the game to the unreviewed queue.
/// Clears the review text + session_log review markers + concept tags + matchup note
/// + draft; PRESERVES objective progress + the session_log behavioral fields. Keeps
/// the game row (not a game delete). See Revu.Sidecar POST /api/review/delete.
#[tauri::command]
async fn delete_review(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/review/delete", payload).await
}

#[tauri::command]
async fn create_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/objective/create", payload).await
}

#[tauri::command]
async fn update_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/objective/update", payload).await
}

#[tauri::command]
async fn set_objective_priority(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/objective/priority", payload).await
}

#[tauri::command]
async fn complete_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/objective/complete", payload).await
}

/// DESTRUCTIVE: permanently deletes an objective and its cascade (prompts/answers/
/// game_objectives/champions). The sidecar takes a session safety backup first.
/// The frontend MUST confirm before calling. See Revu.Sidecar POST /api/objective/delete.
#[tauri::command]
async fn delete_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/objective/delete", payload).await
}

#[tauri::command]
async fn run_reset(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/reset", payload).await
}

/// Persists changed app-config fields (read-modify-write; only the keys present
/// in the payload are mutated). See Revu.Sidecar POST /api/config/save.
#[tauri::command]
async fn save_config(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/config/save", payload).await
}

/// DESTRUCTIVE: permanently deletes a game and its cascade. The sidecar snapshots
/// a DB backup first. The frontend MUST confirm before calling this.
/// See Revu.Sidecar POST /api/game/delete.
#[tauri::command]
async fn delete_game(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/game/delete", payload).await
}

/// DESTRUCTIVE: wipe ALL data and start fresh. The sidecar takes a full backup
/// FIRST (returns its path), then clears. On success the app RELAUNCHES so it
/// rehydrates from the empty DB cleanly. The frontend MUST type-to-confirm first.
/// See Revu.Sidecar POST /api/settings/reset.
#[tauri::command]
async fn reset_all_data(app: tauri::AppHandle) -> Result<serde_json::Value, String> {
    // Sidecar resets the DB (full backup taken first, server-side). Propagate any
    // error to the caller; only relaunch on success.
    let _ = sidecar::post_json("/api/settings/reset", serde_json::json!({})).await?;
    // Relaunch so the app rehydrates from the fresh DB. restart() exits the current
    // process (the new instance deletes the stale handshake + spawns its own sidecar);
    // it never returns, so no value is produced past this point.
    app.restart();
}

/// DESTRUCTIVE: replace the live DB with a chosen backup ({backupFilePath}). The
/// sidecar takes a PRE-RESTORE safety backup FIRST, then swaps the file. On success
/// the app RELAUNCHES so it loads the restored DB. The frontend MUST confirm first.
/// See Revu.Sidecar POST /api/settings/restore.
#[tauri::command]
async fn restore_backup(app: tauri::AppHandle, payload: serde_json::Value) -> Result<serde_json::Value, String> {
    let _ = sidecar::post_json("/api/settings/restore", payload).await?;
    app.restart();
}

/// Saves an in-progress review draft (so nav-away to VOD doesn't lose edits).
/// See Revu.Sidecar POST /api/review/draft/save.
#[tauri::command]
async fn save_review_draft(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/review/draft/save", payload).await
}

// ── Review-page granular writes (Batch 2) ────────────────────────────────────
// Each POSTs a single-field change and returns {ok:true}; the frontend re-fetches
// the review snapshot after to reflect committed state.

// SHARED evidence triage — the VOD player reuses these exact three commands.
/// Sets an evidence row's polarity (good|neutral|bad) and promotes it out of
/// needs_review. See Revu.Sidecar POST /api/evidence/polarity.
#[tauri::command]
async fn set_evidence_polarity(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/evidence/polarity", payload).await
}

/// Attaches/detaches an evidence row to an objective (objectiveId null detaches);
/// when attached + gameId present, also marks the objective practiced this game.
/// See Revu.Sidecar POST /api/evidence/objective.
#[tauri::command]
async fn set_evidence_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/evidence/objective", payload).await
}

/// Sets an evidence row's status (needs_review|evidence|dismissed|highlight) —
/// dismiss is the common case. See Revu.Sidecar POST /api/evidence/status.
#[tauri::command]
async fn set_evidence_status(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/evidence/status", payload).await
}

/// Classifies one death by cause (gameId, timeS, key). One-tap cause chip.
/// See Revu.Sidecar POST /api/death/classify.
#[tauri::command]
async fn classify_death(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/death/classify", payload).await
}

/// Clears one death's classification (gameId, timeS). Re-tap of the selected chip.
/// See Revu.Sidecar POST /api/death/clear.
#[tauri::command]
async fn clear_death(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/death/clear", payload).await
}

/// Saves a custom-prompt answer for (promptId, gameId); empty text deletes it.
/// See Revu.Sidecar POST /api/prompt/answer/save.
#[tauri::command]
async fn save_prompt_answer(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/prompt/answer/save", payload).await
}

/// Sets per-game focus adherence (value 2=Yes/1=Partly/0=No; null clears).
/// Persisted immediately. See Revu.Sidecar POST /api/focus-adherence.
#[tauri::command]
async fn set_focus_adherence(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/focus-adherence", payload).await
}

// ── Rules CRUD (Session Protocols page) ───────────────────────────────────────
// Each POSTs a JSON payload and returns {ok:true(,id)}. The frontend re-fetches
// get_rules after every write. delete_rule is a HARD delete — the frontend
// confirms before invoking it.

/// Creates a rule (name + ruleType + conditionValue + description? + replacementPlan?).
/// See Revu.Sidecar POST /api/rule/create.
#[tauri::command]
async fn create_rule(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/rule/create", payload).await
}

/// Updates an existing rule (id + the same fields create takes).
/// See Revu.Sidecar POST /api/rule/update.
#[tauri::command]
async fn update_rule(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/rule/update", payload).await
}

/// Flips a rule active/inactive (id). See Revu.Sidecar POST /api/rule/toggle.
#[tauri::command]
async fn toggle_rule(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/rule/toggle", payload).await
}

/// DESTRUCTIVE: hard-deletes a rule (id). The frontend MUST confirm first.
/// See Revu.Sidecar POST /api/rule/delete.
#[tauri::command]
async fn delete_rule(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/rule/delete", payload).await
}

// ── VOD bookmark CRUD (Batch 2) ───────────────────────────────────────────────
// Quick Bookmark tool + bookmark-list edit/delete/tag/quality. Each POSTs a JSON
// payload and returns {ok:true(,id)}; the VOD player re-fetches get_vod after.
// Clip extraction (ffmpeg) is DEFERRED — these are plain note-bookmark writes.

/// Adds a quick note-bookmark at a video time ({gameId, timeS, note?, objectiveId?,
/// promptId?}); returns the new bookmark id. See Revu.Sidecar POST /api/bookmark/add.
#[tauri::command]
async fn add_bookmark(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/add", payload).await
}

/// Edits a bookmark's note ({bookmarkId, note}). See Revu.Sidecar POST /api/bookmark/note.
#[tauri::command]
async fn update_bookmark_note(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/note", payload).await
}

/// Deletes a bookmark ({bookmarkId}). See Revu.Sidecar POST /api/bookmark/delete.
#[tauri::command]
async fn delete_bookmark(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/delete", payload).await
}

/// Attaches/detaches a bookmark's objective ({bookmarkId, objectiveId?}; null
/// detaches). See Revu.Sidecar POST /api/bookmark/objective.
#[tauri::command]
async fn set_bookmark_objective(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/objective", payload).await
}

/// Sets a bookmark's objective + optional prompt tag atomically ({bookmarkId,
/// objectiveId?, promptId?}). See Revu.Sidecar POST /api/bookmark/tag.
#[tauri::command]
async fn set_bookmark_tag(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/tag", payload).await
}

/// Sets a bookmark's quality ({bookmarkId, quality}; good|neutral|bad|"").
/// See Revu.Sidecar POST /api/bookmark/quality.
#[tauri::command]
async fn set_bookmark_quality(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/bookmark/quality", payload).await
}

// ── Clip extraction (Batch 3) ─────────────────────────────────────────────────

/// Extracts a clip from the VOD via ffmpeg and saves it as a clip-backed bookmark
/// + evidence row ({gameId, vodPath, championName?, startTimeS, endTimeS, note?,
/// quality?, objectiveId?, promptId?}). Returns {ok, clipPath, bookmarkId}. The VOD
/// player re-fetches get_vod after. See Revu.Sidecar POST /api/clip/extract.
#[tauri::command]
async fn extract_clip(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/clip/extract", payload).await
}

// ── Pattern review writes (Batch 3) ───────────────────────────────────────────

/// Marks a cross-game pattern reviewed ({patternKey, kind?, momentCount?}); ticks
/// the dashboard "Patterns Reviewed" stat. See Revu.Sidecar POST /api/pattern/mark-reviewed.
#[tauri::command]
async fn mark_pattern_reviewed(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pattern/mark-reviewed", payload).await
}

/// Autosaves a pattern moment's note and (first time, when it has a VOD + note)
/// silently clips its padded window + attaches it as evidence ({evidenceId, text,
/// gameId?, championName?, vodPath?, polarity?, startTimeS?, endTimeS?, alreadyClipped}).
/// Returns {ok, clipped, clipPath}. See Revu.Sidecar POST /api/pattern/moment/note.
#[tauri::command]
async fn save_pattern_moment_note(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/pattern/moment/note", payload).await
}

// ── Manual game entry (Batch 2) ───────────────────────────────────────────────

/// Returns the active post-game objectives for the Manual Entry objectives card.
/// See Revu.Sidecar GET /api/objectives/active.
#[tauri::command]
async fn get_active_objectives() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/objectives/active").await
}

/// Saves a manually-entered game + its review + objective assessments. Returns
/// {ok:true, gameId}. See Revu.Sidecar POST /api/game/manual.
#[tauri::command]
async fn save_manual_game(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/game/manual", payload).await
}

// ── Riot auth / account (Batch 4) ─────────────────────────────────────────────
// Email-OTP login + account resolve. SECURITY-SENSITIVE: /api/auth/verify and
// /api/auth/resolve persist the session token + PUUID via the sidecar's WRITE
// graph (DPAPI). The Onboarding + Settings pages and the VOD-share login panel all
// reuse these command names. Each POSTs a JSON payload and returns the sidecar's
// {ok, ...} (or an error string the sidecar mapped from RiotAuthException).

/// Sends an email OTP/magic-link ({email}). Returns {ok, info?}. No session yet.
/// See Revu.Sidecar POST /api/auth/login.
#[tauri::command]
async fn auth_login(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/login", payload).await
}

/// Signs up + sends an OTP ({email, inviteCode}). Returns {ok, info?}.
/// See Revu.Sidecar POST /api/auth/signup.
#[tauri::command]
async fn auth_signup(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/signup", payload).await
}

/// Verifies the OTP and persists the session ({code, email?}). Returns {ok, email}.
/// See Revu.Sidecar POST /api/auth/verify.
#[tauri::command]
async fn auth_verify(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/verify", payload).await
}

/// Resolves a Riot ID → PUUID + persists id/region/puuid + auto-detects rank
/// ({riotId, region}). Returns {ok, puuid, gameName, tagLine, rank}.
/// See Revu.Sidecar POST /api/auth/resolve.
#[tauri::command]
async fn auth_resolve(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/resolve", payload).await
}

/// Logs out: clears the stored session (best-effort server call). Returns {ok}.
/// See Revu.Sidecar POST /api/auth/logout.
#[tauri::command]
async fn auth_logout() -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/logout", serde_json::json!({})).await
}

/// Clears a half-saved session (token saved but no Riot ID) so the onboarding gate
/// doesn't jam. Called by the onboarding Back buttons. Returns {ok, cleared}.
/// See Revu.Sidecar POST /api/auth/clear-partial.
#[tauri::command]
async fn auth_clear_partial() -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/auth/clear-partial", serde_json::json!({})).await
}

/// Returns the signed-in snapshot ({signedIn, email, riotId, region, hasPuuid,
/// primaryRole, backfillReady}). Secrets are not returned. The Onboarding /
/// Settings / VOD-share surfaces read this. See Revu.Sidecar GET /api/auth/status.
#[tauri::command]
async fn get_auth_status() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/auth/status").await
}

// ── Clip share (Batch 4) ──────────────────────────────────────────────────────

/// Uploads a saved clip publicly (revu.lol/<id>) and persists the URL on the
/// bookmark ({gameId, bookmarkId, championName?, title?}). Returns {ok, shareUrl,
/// alreadyShared}. If logged out / session expired, the sidecar returns 401 with
/// needsLogin and clears the stale token — the frontend opens the inline login
/// panel. The VOD player copies the URL to the clipboard after. See Revu.Sidecar
/// POST /api/clip/upload.
#[tauri::command]
async fn share_clip(payload: serde_json::Value) -> Result<serde_json::Value, String> {
    // Clip bodies can be large; the sidecar caps the upload at 5 min, so give the
    // proxied request the same headroom (the default 30s would falsely time out).
    sidecar::post_json_timeout("/api/clip/upload", payload, Duration::from_secs(300)).await
}

// ── Riot-API backfill (Batch 4) ───────────────────────────────────────────────

/// Backfills enemy laners + laning@10 for games missing them via the Riot Match-V5
/// API (through the proxy). Long-running (throttled). Returns {ok, ranBackfill,
/// text, enemy, laning}. Requires sign-in + a resolved PUUID. See Revu.Sidecar
/// POST /api/backfill/start.
#[tauri::command]
async fn run_backfill() -> Result<serde_json::Value, String> {
    // Backfill walks every game missing matchup data, throttled ~1.5 RPS with two
    // round-trips per game on the laning leg — well past 30s for any real backlog.
    // 10-minute window; a larger backlog can be drained by re-running.
    sidecar::post_json_timeout("/api/backfill/start", serde_json::json!({}), Duration::from_secs(600)).await
}

// ── Settings page (Batch 6) ───────────────────────────────────────────────────

/// Returns the read-only Settings diagnostics (ffmpeg availability + Ascent
/// folder status + clip-folder usage + the backups list). Pairs with get_config
/// (the editable surface). See Revu.Sidecar GET /api/settings/status.
#[tauri::command]
async fn get_settings_status() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/settings/status").await
}

/// Scans the Ascent folder + auto-matches recordings to unlinked games (a write:
/// new VOD links are persisted). Returns {ok, matched, recordingCount, text}.
/// See Revu.Sidecar POST /api/settings/scan-vods.
#[tauri::command]
async fn scan_vods() -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/settings/scan-vods", serde_json::json!({})).await
}

// ── Auto-update (Velopack) ───────────────────────────────────────────────────
// The CHECK + DOWNLOAD run in the sidecar (Velopack UpdateManager). The APPLY runs
// HERE, because Velopack's Update.exe must swap files under the main exe and then
// relaunch it — which only works driven from the installed app, not the sidecar.

/// Ask the sidecar whether a newer release exists. Returns the UpdateCheckResult
/// shape { ok, installed, available, currentVersion, newVersion, message }.
#[tauri::command]
async fn check_update() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/update/check").await
}

/// Stage the discovered update's package (sidecar → Velopack DownloadUpdatesAsync).
/// Returns { ok, packagePath, version, message }. Apply is a separate step.
#[tauri::command]
async fn download_update() -> Result<serde_json::Value, String> {
    sidecar::post_json("/api/update/download", serde_json::json!({})).await
}

/// Apply a previously-downloaded update and restart. Invokes the install's bundled
/// Update.exe: `apply --silent --waitPid <our pid>` waits for THIS process to exit,
/// swaps in the staged package, and relaunches the app. We spawn it detached, then
/// the caller exits the app so the swap can proceed. No-op (returns an error the UI
/// shows) when not a Velopack install (dev run) — Update.exe won't be present.
#[tauri::command]
async fn apply_update(app: tauri::AppHandle) -> Result<(), String> {
    // Update.exe sits at the install ROOT, one level up from current/<app>.exe.
    let exe = std::env::current_exe().map_err(|e| format!("current_exe: {e}"))?;
    // current/ -> install root. Try parent.parent (current/ layout) then parent.
    let root = exe
        .parent()
        .and_then(|p| p.parent())
        .map(|p| p.to_path_buf())
        .or_else(|| exe.parent().map(|p| p.to_path_buf()))
        .ok_or_else(|| "could not resolve install root".to_string())?;
    let updater = root.join("Update.exe");
    if !updater.exists() {
        return Err("Update.exe not found — updates apply only in the installed app.".into());
    }

    let pid = std::process::id();
    let mut cmd = std::process::Command::new(&updater);
    cmd.arg("apply").arg("--silent").arg("--waitPid").arg(pid.to_string());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    cmd.spawn().map_err(|e| format!("failed to launch updater: {e}"))?;

    // Give the updater a beat to start waiting on our PID, then exit so it can swap.
    std::thread::sleep(std::time::Duration::from_millis(400));
    app.exit(0);
    Ok(())
}

/// Builds the Markdown review export and returns { ok, markdown, fileName }. The
/// sidecar does NOT write a file — save_export_file (below) handles the native
/// save dialog + disk write. See Revu.Sidecar GET /api/settings/export.
#[tauri::command]
async fn get_export_markdown() -> Result<serde_json::Value, String> {
    sidecar::get_json("/api/settings/export").await
}

/// Builds the Markdown review export for a SINGLE game and returns
/// { ok, found, markdown, fileName }. Used by the review page's Copy + Export.
/// See Revu.Sidecar GET /api/review/export.
#[tauri::command]
async fn get_review_export_markdown(game_id: i64) -> Result<serde_json::Value, String> {
    sidecar::get_json(&format!("/api/review/export?gameId={game_id}")).await
}

/// The app version (from tauri.conf.json), shown in the branded title-bar strip.
/// Single-sources the version that used to come from ShellViewModel.AppVersion.
#[tauri::command]
fn app_version(app: tauri::AppHandle) -> String {
    app.package_info().version.to_string()
}

// ── Native ops (Tauri dialog / fs / shell plugins) ────────────────────────────
// The app routes everything through invoke(), so these native interactions live
// in Rust commands (using the plugins' Rust APIs) rather than the plugins' JS
// surface. Mirrors the WinUI FolderPicker / MarkdownExportPicker / Open-log-folder.

/// Opens a native folder picker and returns the chosen path (or null if the user
/// cancelled). Used by the Ascent / Clips / Backup "Browse" buttons; the frontend
/// drops the path into the matching field (persisted on Save). Mirrors
/// SettingsViewModel.PickFolderAsync (Windows.Storage.Pickers.FolderPicker).
#[tauri::command]
async fn pick_folder(app: tauri::AppHandle) -> Result<Option<String>, String> {
    use tauri_plugin_dialog::DialogExt;
    // Async commands run off the main thread, so the blocking dialog variant is
    // the canonical pattern here (no callback/channel threading to reason about).
    let picked = app.dialog().file().blocking_pick_folder();
    // FilePath is an enum: Path(PathBuf) on most desktop picks, but Url(file://…)
    // on some platforms/configs. `to_string()` on the Url variant yields a
    // "file:///C:/Users/…" URI with forward slashes — which the sidecar then
    // stores verbatim and Directory.Exists rejects, so the Ascent scan silently
    // finds nothing. into_path() normalises BOTH variants to a real filesystem
    // path (same fix save_export_file already uses). On the rare Url-resolve
    // failure, fall back to to_string() rather than dropping the pick entirely.
    Ok(picked.map(|p| match p.clone().into_path() {
        Ok(path) => path.to_string_lossy().to_string(),
        Err(_) => p.to_string(),
    }))
}

/// Opens a native "Save As" dialog (default name from the sidecar) and writes the
/// given Markdown to the chosen path. Returns { ok, saved, path? }; saved=false
/// (no error) when the user cancels. Mirrors MarkdownExportPicker.PickSavePathAsync
/// + File.WriteAllTextAsync. Uses the dialog plugin for the picker and the fs
/// plugin's path so the write is permission-scoped.
#[tauri::command]
async fn save_export_file(
    app: tauri::AppHandle,
    file_name: String,
    markdown: String,
) -> Result<serde_json::Value, String> {
    use tauri_plugin_dialog::DialogExt;
    let default_name = if file_name.is_empty() {
        "revu-review-export.md".to_string()
    } else {
        file_name
    };
    let chosen = app
        .dialog()
        .file()
        .set_file_name(&default_name)
        .add_filter("Markdown", &["md"])
        .blocking_save_file();
    match chosen {
        None => Ok(serde_json::json!({ "ok": true, "saved": false })),
        Some(p) => {
            // FilePath -> a real filesystem path for the write.
            let path = p
                .into_path()
                .map_err(|e| format!("could not resolve save path: {e}"))?;
            std::fs::write(&path, markdown.as_bytes())
                .map_err(|e| format!("failed to write export: {e}"))?;
            Ok(serde_json::json!({
                "ok": true,
                "saved": true,
                "path": path.to_string_lossy().to_string()
            }))
        }
    }
}

/// Opens the Revu log folder (%LOCALAPPDATA%\Revu) in the OS file explorer via the
/// shell plugin's opener. Creates the directory first so the open never fails on a
/// fresh install. Mirrors SettingsViewModel.OpenLogFolderCommand (Process.Start).
#[tauri::command]
async fn open_log_folder(app: tauri::AppHandle) -> Result<(), String> {
    use tauri_plugin_shell::ShellExt;
    let dir = dirs::data_local_dir()
        .map(|d| d.join("Revu"))
        .ok_or_else(|| "could not resolve %LOCALAPPDATA%".to_string())?;
    let _ = std::fs::create_dir_all(&dir);
    app.shell()
        .open(dir.to_string_lossy().to_string(), None)
        .map_err(|e| format!("failed to open log folder: {e}"))
}

// Navigation-only commands (no write). The frontend handles routing; these exist
// so the existing data-action handlers resolve. review_vod / open_review will
// drive navigation to the VOD/Review pages once those flows are wired.
#[tauri::command]
async fn review_vod(game_id: Option<i64>) -> Result<(), String> {
    let _ = game_id;
    Ok(())
}

#[tauri::command]
async fn open_review(game_id: Option<i64>) -> Result<(), String> {
    let _ = game_id;
    Ok(())
}

#[tauri::command]
async fn take_next_step() -> Result<(), String> {
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        // Settings page native ops (Batch 6): folder/save-file dialogs + writing
        // the Markdown export to the chosen path.
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .setup(|app| {
            // Spawn the C# sidecar and wait for it to report ready BEFORE the
            // window starts hitting get_dashboard. We hide the window until then.
            if let Some(win) = app.get_webview_window("main") {
                let _ = win.hide();
            }

            // Spawn synchronously (cheap), then await readiness on the async runtime.
            if let Err(e) = sidecar::spawn() {
                eprintln!("sidecar spawn error: {e}");
            }

            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                match sidecar::wait_ready(Duration::from_secs(20)).await {
                    Ok(hs) => {
                        println!("sidecar ready on port {}", hs.port);
                        if let Some(win) = handle.get_webview_window("main") {
                            let _ = win.show();
                            let _ = win.set_focus();
                        }
                    }
                    Err(e) => {
                        eprintln!("sidecar not ready: {e}");
                        // Show the window anyway so the UI's error panel surfaces.
                        if let Some(win) = handle.get_webview_window("main") {
                            let _ = win.show();
                        }
                    }
                }
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_dashboard,
            get_games,
            get_objectives,
            get_objective_games,
            get_objective_notes,
            get_objective,
            get_review,
            get_rules,
            get_tiltcheck,
            get_patterns,
            get_vod,
            get_config,
            start_block,
            end_block,
            save_review,
            skip_review,
            delete_review,
            create_objective,
            update_objective,
            set_objective_priority,
            complete_objective,
            delete_objective,
            run_reset,
            save_config,
            delete_game,
            reset_all_data,
            restore_backup,
            save_review_draft,
            set_evidence_polarity,
            set_evidence_objective,
            set_evidence_status,
            classify_death,
            clear_death,
            save_prompt_answer,
            set_focus_adherence,
            create_rule,
            update_rule,
            toggle_rule,
            delete_rule,
            add_bookmark,
            update_bookmark_note,
            delete_bookmark,
            set_bookmark_objective,
            set_bookmark_tag,
            set_bookmark_quality,
            extract_clip,
            mark_pattern_reviewed,
            save_pattern_moment_note,
            get_active_objectives,
            save_manual_game,
            auth_login,
            auth_signup,
            auth_verify,
            auth_resolve,
            auth_logout,
            auth_clear_partial,
            get_auth_status,
            share_clip,
            run_backfill,
            get_settings_status,
            scan_vods,
            check_update,
            download_update,
            apply_update,
            get_export_markdown,
            get_review_export_markdown,
            app_version,
            pick_folder,
            save_export_file,
            open_log_folder,
            get_pregame,
            start_lcu_events,
            set_pregame_mood,
            set_pregame_intent,
            set_pregame_practiced,
            save_pregame_draft,
            review_vod,
            take_next_step,
            open_review
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
