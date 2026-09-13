// Shared Electron-to-sidecar command contract. Routes are explicit; helpers only
// supply the repeated envelope, argument-query mapping and deadline defaults.
// C# routes validate domain payload fields; validateCommand checks the IPC envelope.
const get = (route, args = {}, options = {}) => ({
  method: 'GET', route, args,
  ...(Object.keys(args).length ? { query: Object.fromEntries(Object.keys(args).map(key => [key, key])) } : {}),
  timeoutMs: 10_000, ...options,
});
const post = (route, { payload = true, timeoutMs = 30_000, ...options } = {}) => ({
  method: 'POST', route, args: payload ? { payload: 'object' } : {}, timeoutMs, ...options,
});
const native = (args = {}, options = {}) => ({ method: 'NATIVE', route: null, args, ...options });

const definitions = {
  set_window_resolution: native({"resolution":"string"}),
  get_dashboard: get("/api/dashboard"),
  get_pregame: get("/api/pregame", {"myChampion":"string?","enemy":"string?","role":"string?","participantMap":"string?"}),
  start_lcu_events: {"method":"SSE","route":"/api/events","args":{}},
  set_pregame_mood: post("/api/pregame/mood"),
  set_pregame_intent: post("/api/pregame/intent"),
  save_pregame_ifthen: post("/api/pregame/ifthen"),
  set_pregame_practiced: post("/api/pregame/practiced"),
  save_pregame_draft: post("/api/pregame/prompt/draft"),
  get_games: get("/api/games", {"view":"string?","page":"integer?"}, {"positiveQuery":["page"]}),
  get_objectives: get("/api/objectives"),
  get_objective_games: get("/api/objective/games", {"id":"integer"}),
  get_objective_notes: get("/api/objective/notes", {"id":"integer"}),
  get_objective: get("/api/objective", {"id":"integer"}),
  get_review: get("/api/review", {"gameId":"integer?"}, {"positiveQuery":["gameId"]}),
  get_rules: get("/api/rules"),
  get_tiltcheck: get("/api/tiltcheck"),
  get_patterns: get("/api/patterns"),
  get_vod: get("/api/vod", {"gameId":"integer"}),
  get_derived_events: get("/api/derived", {"gameId":"integer"}),
  get_config: get("/api/config"),
  start_block: post("/api/block/start"),
  end_block: post("/api/block/end"),
  start_stint: post("/api/stint/start"),
  end_stint: post("/api/stint/end"),
  get_stint: get("/api/stint"),
  save_review: post("/api/review/save"),
  skip_review: post("/api/review/skip"),
  delete_review: post("/api/review/delete"),
  create_objective: post("/api/objective/create"),
  update_objective: post("/api/objective/update"),
  set_objective_priority: post("/api/objective/priority"),
  complete_objective: post("/api/objective/complete"),
  delete_objective: post("/api/objective/delete"),
  run_reset: post("/api/reset"),
  save_config: post("/api/config/save"),
  delete_game: post("/api/game/delete"),
  reset_all_data: post("/api/settings/reset", {"payload":false,"sideEffect":"restart-after-write"}),
  restore_backup: post("/api/settings/restore", {"sideEffect":"restart-after-write"}),
  save_review_draft: post("/api/review/draft/save"),
  set_evidence_polarity: post("/api/evidence/polarity"),
  set_evidence_objective: post("/api/evidence/objective"),
  set_evidence_prompt: post("/api/evidence/prompt"),
  set_evidence_status: post("/api/evidence/status"),
  classify_death: post("/api/death/classify"),
  clear_death: post("/api/death/clear"),
  save_prompt_answer: post("/api/prompt/answer/save"),
  set_focus_adherence: post("/api/focus-adherence"),
  create_rule: post("/api/rule/create"),
  update_rule: post("/api/rule/update"),
  toggle_rule: post("/api/rule/toggle"),
  set_rule_enforce: post("/api/rule/enforce"),
  override_hard_stop: post("/api/hardstop/override"),
  delete_rule: post("/api/rule/delete"),
  get_matchups: get("/api/matchups"),
  get_matchups_export_markdown: get("/api/matchups/export", {"lane":"string?","last":"integer?"}, {"positiveQuery":["last"]}),
  create_matchup: post("/api/matchup/create"),
  create_matchup_from_last_game: post("/api/matchup/from-last-game"),
  update_matchup: post("/api/matchup/update"),
  save_matchup_notes: post("/api/matchup/notes"),
  delete_matchup: post("/api/matchup/delete"),
  save_encounter: post("/api/encounter/save"),
  save_event_correction: post("/api/event/correct"),
  revert_event_correction: post("/api/correction/revert"),
  get_event_corrections: get("/api/corrections", {"gameId":"integer"}),
  export_event_corrections: get("/api/corrections/export", {"gameId":"integer?"}, {"positiveQuery":["gameId"]}),
  add_bookmark: post("/api/bookmark/add"),
  update_bookmark_note: post("/api/bookmark/note"),
  delete_bookmark: post("/api/bookmark/delete"),
  set_bookmark_objective: post("/api/bookmark/objective"),
  set_bookmark_tag: post("/api/bookmark/tag"),
  set_bookmark_quality: post("/api/bookmark/quality"),
  extract_clip: post("/api/clip/extract"),
  auto_clip_objectives: post("/api/clip/auto-objectives"),
  mark_pattern_reviewed: post("/api/pattern/mark-reviewed"),
  save_pattern_moment_note: post("/api/pattern/moment/note"),
  get_active_objectives: get("/api/objectives/active"),
  save_manual_game: post("/api/game/manual"),
  auth_login: post("/api/auth/login"),
  auth_signup: post("/api/auth/signup"),
  auth_verify: post("/api/auth/verify"),
  auth_resolve: post("/api/auth/resolve"),
  auth_logout: post("/api/auth/logout", {"payload":false}),
  auth_clear_partial: post("/api/auth/clear-partial", {"payload":false}),
  get_auth_status: get("/api/auth/status"),
  share_clip: post("/api/clip/upload", {"timeoutMs":300000}),
  delete_clip: post("/api/clip/delete"),
  run_backfill: post("/api/backfill/start", {"payload":false,"timeoutMs":3600000}),
  get_settings_status: get("/api/settings/status"),
  scan_vods: post("/api/settings/scan-vods", { payload: false, timeoutMs: 120_000 }),
  check_update: get("/api/update/check"),
  download_update: post("/api/update/download", {"payload":false}),
  apply_update: native({}, {"sideEffect":"apply-update-stop-sidecar-exit"}),
  get_export_markdown: get("/api/settings/export"),
  get_review_export_markdown: get("/api/review/export", {"gameId":"integer"}),
  app_version: native(),
  get_recording_status: native(),
  save_recording_settings: native({ payload: 'object' }),
  open_recordings_folder: native(),
  get_background_settings: native(),
  save_background_settings: native({ payload: 'object' }),
  pick_folder: native(),
  save_export_file: native({"fileName":"string","markdown":"string"}),
  copy_text_to_clipboard: native({ text: 'string' }),
  open_log_folder: native(),
  review_vod: native({"gameId":"integer?"}),
  open_review: native({"gameId":"integer?"}),
  take_next_step: native(),
};

for (const definition of Object.values(definitions)) {
  Object.freeze(definition.args);
  if (definition.query) Object.freeze(definition.query);
  if (definition.positiveQuery) Object.freeze(definition.positiveQuery);
  Object.freeze(definition);
}
export const COMMANDS = Object.freeze(definitions);

export function validateCommand(command, args = {}) {
  if (typeof command !== 'string' || !Object.hasOwn(COMMANDS, command)) {
    throw new Error('Unsupported desktop command');
  }
  if (!args || typeof args !== 'object' || Array.isArray(args)) throw new TypeError('Command arguments must be an object');
  const definition = COMMANDS[command];
  for (const key of Object.keys(args)) {
    if (!Object.hasOwn(definition.args, key)) throw new TypeError(`Unexpected argument: ${key}`);
  }
  for (const [key, type] of Object.entries(definition.args)) {
    const value = args[key];
    if (value == null && type.endsWith('?')) continue;
    const base = type.replace('?', '');
    const valid = base === 'integer' ? Number.isSafeInteger(value)
      : base === 'object' ? !!value && typeof value === 'object' && !Array.isArray(value)
      : typeof value === 'string';
    if (!valid) throw new TypeError(`Invalid argument: ${key}`);
  }
  return definition;
}
