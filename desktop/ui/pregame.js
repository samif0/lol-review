// Revu desktop — Pre-Game / In-Game live page renderer.
//
// ONE renderer drives BOTH pregame.html (the full champ-select "Lock In" surface)
// and ingame.html (the in-game subset: cooldowns/intel + matchup + matchup-history
// + per-objective pre-game prompt boxes). They share the backing data exactly like
// the WinUI PreGameDialogViewModel drives both PreGamePage and InGamePage; the page
// decides which sections exist in its own HTML, and this script renders whatever is
// present (missing host elements are simply skipped).
//
// Conventions mirror app.js / session.js:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • fetchPregame() tries invoke FIRST, then ./sample-pregame.json for a plain-
//     browser preview.
//   • Every server string is written via textContent (never innerHTML) — XSS-safe;
//     colors arrive as *Hex strings applied to style properties only.
//   • ONE delegated [data-action] click handler.
//   • LIVE updates arrive over Tauri events ("lcu-event"): the sidecar streams the
//     LCU messages (champ-select start/update/cancel, game in-progress, game end)
//     and we react — refresh the live matchup, auto-navigate on gameInProgress,
//     surface a saved/closed banner on gameEnded.
//
// DEFERRED WRITES: mood / intent / practiced-toggles are NOT saved to the DB here.
// They're staged into the sidecar's live state (set_pregame_*); the sidecar's
// game-flow coordinator writes them to session_log at game END (same contract as
// the WinUI statics → ShellViewModel hop). The prompt answer boxes DO persist on
// every keystroke (save_pregame_draft → pre_game_draft_prompts), promoted at EOG.

// ── invoke resolver ──────────────────────────────────────────────────────────
let _invoke = null;
async function getInvoke() {
  if (_invoke) return _invoke;
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') { _invoke = mod.invoke; return _invoke; }
  } catch (_) { /* not resolvable outside the Tauri bundler — fall through */ }
  if (window.__TAURI__ && window.__TAURI__.core && typeof window.__TAURI__.core.invoke === 'function') {
    _invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
    return _invoke;
  }
  return null;
}

// Tauri event listener (for the LCU SSE stream re-emitted by the Rust host).
async function getListen() {
  try {
    const mod = await import('@tauri-apps/api/event');
    if (mod && typeof mod.listen === 'function') return mod.listen;
  } catch (_) { /* fall through */ }
  if (window.__TAURI__ && window.__TAURI__.event && typeof window.__TAURI__.event.listen === 'function') {
    return window.__TAURI__.event.listen.bind(window.__TAURI__.event);
  }
  return null;
}

// ── small DOM helpers ─────────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function setText(id, text) { const el = $(id); if (el) el.textContent = text ?? ''; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) { const t = $(id); return t ? t.content.firstElementChild.cloneNode(true) : null; }

// Which surface are we on? shell.js stamps data-page on its <script>.
const PAGE = (document.querySelector('script[data-page]')?.dataset.page) || 'pregame';
const IS_INGAME = PAGE === 'ingame';

// ── module state ──────────────────────────────────────────────────────────────
let _data = null;
// Live champ-select context, overlaid on top of the static snapshot by SSE ticks.
let _live = { myChampion: '', enemyChampion: '', myPosition: '', participantMapJson: '', sessionKey: null };
const _draftTimers = new Map(); // promptId -> debounce timer

// ── data fetch ────────────────────────────────────────────────────────────────
async function fetchPregame() {
  const invoke = await getInvoke();
  if (invoke) {
    const args = {};
    if (_live.myChampion) args.myChampion = _live.myChampion;
    if (_live.enemyChampion) args.enemy = _live.enemyChampion;
    if (_live.myPosition) args.role = _live.myPosition;
    if (_live.participantMapJson) args.participantMap = _live.participantMapJson;
    return invoke('get_pregame', args);
  }
  const res = await fetch('./sample-pregame.json');
  if (!res.ok) throw new Error(`sample-pregame.json ${res.status}`);
  return res.json();
}

// ════════════════════════════════════════════════════════════════════════════
// RENDER
// ════════════════════════════════════════════════════════════════════════════
function render(d) {
  clearError();
  _data = d;
  // Seed live context from the snapshot if SSE hasn't populated it yet.
  if (!_live.myChampion && d.myChampion) _live.myChampion = d.myChampion;
  if (!_live.enemyChampion && d.enemyChampion) _live.enemyChampion = d.enemyChampion;
  if (!_live.myPosition && d.myPosition) _live.myPosition = d.myPosition;

  renderIntelDeck(d.intelDeck || []);
  renderMatchup(d.matchup || {});
  renderMatchupHistory(d.matchupHistory || {});
  renderPromptBlocks(d.promptBlocks || []);

  // PreGamePage-only sections (absent in ingame.html → skipped).
  if (!IS_INGAME) {
    renderIntent(d.intent || {}, d.activePlan);
    renderMood(d.showMoodSelector);
    renderSessionIntention(d.sessionIntention || {});
    renderObjectives(d.objectives || {});
  } else {
    // InGamePage shows a "NO ACTIVE GAME" card when no champ is detected.
    show($('noactive'), !(d.matchup && d.matchup.hasMatchupDetected));
  }
}

// ── INTEL deck (rotating cards) ───────────────────────────────────────────────
let _intelIndex = 0;
let _intelTimer = null;
function renderIntelDeck(cards) {
  const host = $('intel-deck');
  const wrap = $('intel-wrap');
  if (!wrap) return;
  show(wrap, cards.length > 0);
  if (cards.length === 0) { if (_intelTimer) clearInterval(_intelTimer); return; }

  clear(host);
  cards.forEach((c, i) => {
    const el = tpl('tpl-intel');
    if (!el) return;
    el.querySelector('.intel-eyebrow').textContent = c.eyebrow || '';
    el.querySelector('.intel-headline').textContent = c.headline || '';
    const body = el.querySelector('.intel-body');
    body.textContent = c.body || '';
    show(body, !!c.body);
    el.hidden = i !== 0;
    host.appendChild(el);
  });
  _intelIndex = 0;

  // Pagination dots + auto-rotation (mirrors IntelRotatorControl: 7s crossfade,
  // dots only when >1 card; arrows step manually and stop the auto-rotation).
  const dots = $('intel-dots');
  if (dots) {
    clear(dots);
    show(dots, cards.length > 1);
    cards.forEach((_, i) => {
      const dot = document.createElement('span');
      dot.className = 'intel-dot' + (i === 0 ? ' on' : '');
      dots.appendChild(dot);
    });
  }
  show($('intel-arrows'), cards.length > 1);

  if (_intelTimer) clearInterval(_intelTimer);
  if (cards.length > 1) {
    _intelTimer = setInterval(() => stepIntel(1, false), 7000);
  }
}
function stepIntel(delta, manual) {
  const host = $('intel-deck');
  if (!host) return;
  const cards = Array.from(host.children);
  if (cards.length < 2) return;
  if (manual && _intelTimer) { clearInterval(_intelTimer); _intelTimer = null; }
  cards[_intelIndex].hidden = true;
  _intelIndex = (_intelIndex + delta + cards.length) % cards.length;
  cards[_intelIndex].hidden = false;
  const dots = $('intel-dots');
  if (dots) Array.from(dots.children).forEach((d, i) => d.classList.toggle('on', i === _intelIndex));
}

// ── MATCHUP card (live champ vs enemy + 2v2 pairing headline) ─────────────────
function renderMatchup(m) {
  const card = $('matchup-card');
  if (!card) return;
  show(card, !!m.hasMatchupDetected);
  if (m.accentHex) card.style.setProperty('--cardbr', m.accentHex);
  setText('matchup-my', m.myChampion || _live.myChampion || '');
  // Prefer the live enemy once it locks; else the snapshot placeholder.
  const enemy = _live.enemyChampion || (m.enemyOrPlaceholder && m.enemyOrPlaceholder !== '…' ? m.enemyOrPlaceholder : '');
  setText('matchup-enemy', enemy || '…');

  // 2v2 pairing headline, computed client-side from the live participant map +
  // role (mirror PreGameDialogViewModel.BuildPairingHeadline). Only ADC/supp/
  // mid/jungle with a valid map produce one.
  const headline = buildPairingHeadline(_live.myPosition || m.myPosition, _live.participantMapJson);
  setText('matchup-pairing', headline);
  show($('matchup-pairing'), !!headline);
}

function buildPairingHeadline(role, mapJson) {
  if (!mapJson) return '';
  let map = null;
  try { map = JSON.parse(mapJson); } catch (_) { return ''; }
  if (!map || typeof map !== 'object') return '';
  const r = (role || '').toLowerCase();
  const pair = (op, opn, ep, epn) => {
    const a = map[op], b = map[ep];
    if (!a || !b) return '';
    const ap = map[opn], bp = map[epn];
    const own = ap ? `${a}+${ap}` : a;
    const enemy = bp ? `${b}+${bp}` : b;
    return `${own} vs ${enemy}`;
  };
  if (r === 'adc' || r === 'bottom' || r === 'bot') return pair('ownBot', 'ownSupp', 'enemyBot', 'enemySupp');
  if (r === 'support' || r === 'supp' || r === 'utility') return pair('ownSupp', 'ownBot', 'enemySupp', 'enemyBot');
  if (r === 'mid' || r === 'middle') return pair('ownMid', 'ownJg', 'enemyMid', 'enemyJg');
  if (r === 'jungle' || r === 'jg') return pair('ownJg', 'ownMid', 'enemyJg', 'enemyMid');
  return '';
}

// ── MATCHUP HISTORY (saved notes vs this enemy) ───────────────────────────────
function renderMatchupHistory(mh) {
  const card = $('mh-card');
  if (!card) return;
  show(card, !!mh.has);
  if (!mh.has) return;
  if (mh.accentHex) card.style.setProperty('--cardbr', mh.accentHex);
  setText('mh-header', mh.headerText || '');
  const host = $('mh-list');
  clear(host);
  (mh.items || []).forEach((n) => {
    const el = tpl('tpl-mh');
    if (!el) return;
    el.querySelector('.mh-note').textContent = n.note || '';
    el.querySelector('.mh-date').textContent = n.dateText || '';
    const flag = el.querySelector('.mh-helpful');
    if (flag) {
      show(flag, !!n.hasHelpfulRating);
      flag.textContent = n.wasHelpful ? 'HELPED' : 'DIDN’T HELP';
      flag.classList.toggle('good', !!n.wasHelpful);
    }
    host.appendChild(el);
  });
}

// ── PROMPT BLOCKS (per-objective pre-game answer boxes; auto-saved drafts) ────
// True when the user is actively typing inside `host` — used to skip a re-render
// that would tear down the focused field and discard in-flight keystrokes (the
// champSelectUpdated input-eating bug). A champ lock-in fires a refresh every few
// hundred ms; without this guard it clobbers the textarea the user is mid-sentence in.
function isEditingWithin(host) {
  const a = document.activeElement;
  return !!(host && a && host.contains(a)
    && (a.tagName === 'TEXTAREA' || a.tagName === 'INPUT'));
}

function renderPromptBlocks(blocks) {
  const card = $('prompts-card');
  if (!card) return;
  show(card, blocks.length > 0);
  const host = $('prompts-list');
  // Don't blow away a prompt textarea the user is typing in. Their keystrokes are
  // autosaved on a debounce and re-seeded on the next refresh once focus leaves.
  if (isEditingWithin(host)) return;
  clear(host);
  blocks.forEach((b) => {
    const block = tpl('tpl-prompt-block');
    if (!block) return;
    const eyebrow = block.querySelector('.pb-eyebrow');
    eyebrow.textContent = b.eyebrow || (b.isPriority ? 'PRIORITY' : 'ACTIVE');
    if (b.accentHex) {
      eyebrow.style.color = b.accentHex;
      block.style.setProperty('--pb-accent', b.accentHex);
    }
    block.querySelector('.pb-title').textContent = b.objectiveTitle || '';
    const pHost = block.querySelector('.pb-prompts');
    (b.prompts || []).forEach((p) => {
      const row = tpl('tpl-prompt');
      if (!row) return;
      row.querySelector('.p-label').textContent = p.label || '';
      const ta = row.querySelector('.p-input');
      ta.value = p.answerText || '';
      ta.dataset.promptId = String(p.promptId);
      // Debounced autosave on every keystroke → save_pregame_draft.
      ta.addEventListener('input', () => scheduleDraftSave(p.promptId, ta.value));
      pHost.appendChild(row);
    });
    host.appendChild(block);
  });
}

function scheduleDraftSave(promptId, text) {
  if (_draftTimers.has(promptId)) clearTimeout(_draftTimers.get(promptId));
  _draftTimers.set(promptId, setTimeout(async () => {
    _draftTimers.delete(promptId);
    const invoke = await getInvoke();
    if (!invoke) { console.info('[pregame] (preview) draft save', promptId); return; }
    try { await invoke('save_pregame_draft', { payload: { promptId, text } }); }
    catch (err) { console.error('[pregame] draft save failed:', err); }
  }, 500));
}

// ── INTENT carry-over card (PreGamePage only) ─────────────────────────────────
let _intentSource = '';
let _intentCleared = false;
function renderIntent(intent, activePlan) {
  const card = $('intent-card');
  if (!card) return;
  if (intent.accentHex) card.style.setProperty('--cardbr', intent.accentHex);

  _intentSource = intent.selectedSource || '';
  _intentCleared = false;

  setText('intent-prov', intent.provenance || '');
  show($('intent-prov-row'), !!intent.provenance);

  setText('active-plan', activePlan || '');
  show($('active-plan-row'), !!activePlan);

  const box = $('intent-input');
  // Don't overwrite the box (or re-stage the seed over it) while the user is typing
  // their own intent — a champSelectUpdated tick would otherwise revert it to the seed.
  const editingIntent = isEditingWithin(card);
  if (box && !editingIntent) box.value = intent.seedText || '';

  // Source chips — visible only when their seed exists.
  setupChip('chip-carry', intent.hasCarrySource, _intentSource === 'carry');
  setupChip('chip-objective', intent.hasObjectiveSource, _intentSource === 'objective');
  setupChip('chip-adherence', intent.hasAdherenceSource, _intentSource === 'adherence');
  // Stash the seeds so the chips can re-seed the box on click.
  card.dataset.carrySeed = intent.carrySeed || '';
  card.dataset.carryProv = intent.carryProvenance || '';
  card.dataset.objSeed = intent.objectiveSeed || '';
  card.dataset.objProv = intent.objectiveProvenance || '';
  card.dataset.adhSeed = intent.adherenceSeed || '';
  card.dataset.adhProv = intent.adherenceProvenance || '';

  applyIntentClearedUi();
  // Stage the zero-tap default so a do-nothing game still carries it — but NOT while
  // the user is editing, or we'd POST the seed over their just-typed (and already
  // separately-staged) custom intent.
  if (!editingIntent) stageIntent();
}
function setupChip(id, has, selected) {
  const el = $(id);
  if (!el) return;
  show(el, !!has);
  el.classList.toggle('on', !!selected);
}
function applyIntentClearedUi() {
  show($('intent-edit'), !_intentCleared);
  show($('intent-cleared'), _intentCleared);
}

async function stageIntent() {
  const box = $('intent-input');
  const intention = _intentCleared ? '' : (box ? box.value.trim() : '');
  const source = intention ? _intentSource : '';
  const invoke = await getInvoke();
  if (!invoke) return;
  try { await invoke('set_pregame_intent', { payload: { intention, source, cleared: _intentCleared } }); }
  catch (err) { console.error('[pregame] stage intent failed:', err); }
}

function applySeed(seed, prov, source, chip) {
  const box = $('intent-input');
  if (box) box.value = seed || '';
  _intentSource = source;
  _intentCleared = false;
  setText('intent-prov', prov || '');
  show($('intent-prov-row'), !!prov);
  ['chip-carry', 'chip-objective', 'chip-adherence'].forEach((c) => $(c)?.classList.remove('on'));
  $(chip)?.classList.add('on');
  applyIntentClearedUi();
  stageIntent();
}

// ── MOOD selector (PreGamePage only) ──────────────────────────────────────────
let _mood = 0;
function renderMood(showIt) {
  show($('mood-card'), !!showIt);
  highlightMood();
}
function highlightMood() {
  document.querySelectorAll('.mood-btn').forEach((b) => {
    b.classList.toggle('on', Number(b.dataset.mood) === _mood);
  });
}
async function setMood(mood) {
  _mood = mood;
  highlightMood();
  const invoke = await getInvoke();
  if (!invoke) return;
  try { await invoke('set_pregame_mood', { payload: { mood } }); }
  catch (err) { console.error('[pregame] set mood failed:', err); }
}

// ── SESSION INTENTION (first-game-of-day; PreGamePage only) ───────────────────
function renderSessionIntention(si) {
  const card = $('session-card');
  if (!card) return;
  show(card, !!si.show);
  if (!si.show) return;
  if (si.accentHex) card.style.setProperty('--cardbr', si.accentHex);
  const box = $('session-input');
  if (box) box.value = si.intention || '';
  const chips = $('session-quick');
  if (chips) {
    clear(chips);
    (si.quickOptions || []).forEach((opt) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'sess-chip';
      b.textContent = opt;
      b.dataset.action = 'set_session_quick';
      b.dataset.text = opt;
      chips.appendChild(b);
    });
  }
}

// ── OBJECTIVES mega-card (priority + practiced toggles; PreGamePage only) ─────
const _practiced = new Set();
function renderObjectives(o) {
  const card = $('obj-card');
  if (!card) return;
  show(card, !!o.hasActiveObjective);
  if (!o.hasActiveObjective) return;
  if (o.accentHex) card.style.setProperty('--cardbr', o.accentHex);
  setText('obj-priority-title', o.priorityTitle || '');
  setText('obj-priority-crit', o.priorityCriteria || '');
  show($('obj-priority-crit'), !!o.priorityCriteria);

  _practiced.clear();
  const host = $('obj-list');
  clear(host);
  show($('obj-list-label'), !!o.hasObjectives);
  (o.items || []).forEach((it) => {
    const row = tpl('tpl-obj');
    if (!row) return;
    row.querySelector('.obj-title').textContent = it.title || '';
    const crit = row.querySelector('.obj-crit');
    crit.textContent = it.criteria || '';
    show(crit, !!it.criteria);
    const pill = row.querySelector('.obj-pri');
    show(pill, !!it.isPriority);
    const toggle = row.querySelector('.obj-toggle');
    toggle.dataset.objectiveId = String(it.objectiveId);
    toggle.dataset.action = 'toggle_practiced';
    host.appendChild(row);
  });
}
async function togglePracticed(objectiveId, btn) {
  if (_practiced.has(objectiveId)) _practiced.delete(objectiveId);
  else _practiced.add(objectiveId);
  if (btn) btn.classList.toggle('on', _practiced.has(objectiveId));
  const invoke = await getInvoke();
  if (!invoke) return;
  try { await invoke('set_pregame_practiced', { payload: { objectiveIds: Array.from(_practiced) } }); }
  catch (err) { console.error('[pregame] set practiced failed:', err); }
}

// ── LIVE banner (gameInProgress / gameEnded / lcu connection) ─────────────────
function setBanner(text, kind) {
  const el = $('live-banner');
  if (!el) return;
  show(el, !!text);
  if (!text) return;
  el.textContent = text;
  el.className = 'live-banner' + (kind ? ` ${kind}` : '');
}

// ════════════════════════════════════════════════════════════════════════════
// LCU LIVE CHANNEL — react to the SSE events the Rust host re-emits.
// ════════════════════════════════════════════════════════════════════════════
async function wireLiveChannel() {
  const listen = await getListen();
  const invoke = await getInvoke();
  if (!listen || !invoke) return; // plain-browser preview: no live channel

  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    handleLcuEvent(msg.type, msg.payload || {});
  });

  // Ask the Rust host to open (or join) the SSE stream.
  try { await invoke('start_lcu_events'); }
  catch (err) { console.error('[pregame] start_lcu_events failed:', err); }
}

let _refreshScheduled = false;
function scheduleRefresh() {
  if (_refreshScheduled) return;
  _refreshScheduled = true;
  setTimeout(() => { _refreshScheduled = false; loadPregame(); }, 150);
}

function handleLcuEvent(type, payload) {
  switch (type) {
    case 'liveState':
    case 'champSelectStarted':
    case 'champSelectUpdated': {
      // Overlay the live champ-select context, then re-render the matchup + refetch
      // (the intel deck + champion-gated prompts depend on the locked champion).
      if (payload.myChampion != null && payload.myChampion !== '') _live.myChampion = payload.myChampion;
      if (payload.enemyChampion != null) _live.enemyChampion = payload.enemyChampion;
      if (payload.enemyLaner != null) _live.enemyChampion = payload.enemyLaner;
      if (payload.myPosition != null && payload.myPosition !== '') _live.myPosition = payload.myPosition;
      if (payload.participantMapJson != null && payload.participantMapJson !== '') _live.participantMapJson = payload.participantMapJson;
      if (payload.sessionKey != null) _live.sessionKey = payload.sessionKey;
      if (_data) renderMatchup(_data.matchup || {});
      scheduleRefresh();
      break;
    }
    case 'champSelectCancelled':
      setBanner('Champ select cancelled.', 'warn');
      break;
    case 'gameInProgress':
      // Your game has begun. On the pre-game page, hop to the in-game subset.
      if (!IS_INGAME) { window.location.href = 'ingame.html'; }
      else { setBanner('In game; good luck.', 'ok'); }
      break;
    case 'gameStarted':
      // Loading screen — keep the page up so there's time to read/type.
      break;
    case 'gameEnded': {
      const saved = payload.saved;
      setBanner(saved ? 'Game saved. Review it when you’re ready.' : 'Game ended.', saved ? 'ok' : '');
      break;
    }
    case 'lcuConnection':
      // Toggle a small connected indicator if the page has one.
      { const dot = $('lcu-dot'); if (dot) dot.classList.toggle('on', !!payload.connected); }
      break;
    default:
      break;
  }
}

// ── error panel ───────────────────────────────────────────────────────────────
function renderError(err) {
  const panel = $('errpanel');
  if (!panel) return;
  setText('err-detail', (err && err.message) ? err.message : String(err));
  show(panel, true);
}
function clearError() { show($('errpanel'), false); }

// ── load orchestration (startup grace, mirrors app.js) ────────────────────────
let _loading = false;
async function loadPregame() {
  if (_loading) return;
  _loading = true;
  const maxAttempts = 25;
  try {
    for (let attempt = 1; ; attempt++) {
      try { render(await fetchPregame()); return; }
      catch (err) {
        const transient = /sidecar not ready|not ready|connection refused|failed to fetch/i.test(String(err));
        if (transient && attempt < maxAttempts) { await new Promise((r) => setTimeout(r, 400)); continue; }
        renderError(err);
        console.error('[pregame] load failed:', err);
        return;
      }
    }
  } finally { _loading = false; }
}

// ── ONE delegated [data-action] handler ───────────────────────────────────────
document.addEventListener('click', (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  switch (action) {
    case 'intel_prev': ev.preventDefault(); stepIntel(-1, true); break;
    case 'intel_next': ev.preventDefault(); stepIntel(1, true); break;
    case 'set_mood': ev.preventDefault(); setMood(Number(target.dataset.mood)); break;
    case 'use_carry': { ev.preventDefault(); const c = $('intent-card'); applySeed(c.dataset.carrySeed, c.dataset.carryProv, 'carry', 'chip-carry'); break; }
    case 'use_objective': { ev.preventDefault(); const c = $('intent-card'); applySeed(c.dataset.objSeed, c.dataset.objProv, 'objective', 'chip-objective'); break; }
    case 'use_adherence': { ev.preventDefault(); const c = $('intent-card'); applySeed(c.dataset.adhSeed, c.dataset.adhProv, 'objective', 'chip-adherence'); break; }
    case 'apply_ifthen': ev.preventDefault(); applyIfThen(); break;
    case 'toggle_carry': ev.preventDefault(); toggleIntentCarry(); break;
    case 'toggle_practiced': ev.preventDefault(); togglePracticed(Number(target.dataset.objectiveId), target); break;
    case 'set_session_quick': { ev.preventDefault(); const b = $('session-input'); if (b) b.value = target.dataset.text || ''; break; }
    default: break;
  }
});

function applyIfThen() {
  const box = $('intent-input');
  if (!box) return;
  const cur = box.value.trim();
  if (/^if /i.test(cur)) return;
  box.value = cur ? `If [moment], then I will ${cur}` : 'If [moment], then I will [action]';
  _intentSource = 'edited';
  setText('intent-prov', 'EDITED BY YOU: SAVES AS WRITTEN');
  show($('intent-prov-row'), true);
  ['chip-carry', 'chip-objective', 'chip-adherence'].forEach((c) => $(c)?.classList.remove('on'));
  stageIntent();
}
function toggleIntentCarry() {
  _intentCleared = !_intentCleared;
  if (_intentCleared) {
    setText('intent-prov', 'NOTHING CARRIED THIS GAME');
    show($('intent-prov-row'), true);
  }
  applyIntentClearedUi();
  stageIntent();
}

// The intent box itself flips the source to "edited" on manual typing.
let _intentDebounce = null;
document.addEventListener('input', (ev) => {
  if (ev.target && ev.target.id === 'intent-input') {
    _intentSource = 'edited';
    setText('intent-prov', 'EDITED BY YOU: SAVES AS WRITTEN');
    show($('intent-prov-row'), true);
    ['chip-carry', 'chip-objective', 'chip-adherence'].forEach((c) => $(c)?.classList.remove('on'));
    if (_intentDebounce) clearTimeout(_intentDebounce);
    _intentDebounce = setTimeout(stageIntent, 400);
  }
});

// ── boot ──────────────────────────────────────────────────────────────────────
function boot() { loadPregame(); wireLiveChannel(); }
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
