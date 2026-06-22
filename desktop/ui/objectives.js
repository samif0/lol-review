// Revu desktop — Objectives page renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_objectives`.
// All server-supplied strings are written via textContent (never innerHTML)
// to keep the surface XSS-free. Colors arrive as *Hex strings and are applied
// to style/stroke properties only.

// ── invoke resolver ────────────────────────────────────────────────────────
// Prefer the official @tauri-apps/api/core import; fall back to the global the
// Tauri webview injects. In a plain browser (no Tauri) both are absent and we
// fall through to a local sample JSON so the page previews standalone.
let _invoke = null;
async function getInvoke() {
  if (_invoke) return _invoke;
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') {
      _invoke = mod.invoke;
      return _invoke;
    }
  } catch (_) {
    // module not resolvable outside the Tauri bundler — fall through
  }
  if (window.__TAURI__ && window.__TAURI__.core && typeof window.__TAURI__.core.invoke === 'function') {
    _invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
    return _invoke;
  }
  return null;
}

const isTauri = () => typeof window.__TAURI__ !== 'undefined';

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}

const RING_SMALL = 150.8; // 2·π·r, r=24 (active cards)
const RING_LARGE = 251.3; // 2·π·r, r=40 (priority pane)
const DEFAULT_DIM = 'rgba(255,255,255,0.13)';
const DEFAULT_PROG = '#9d8bff';

// Mastery ladder steps — Exploring 0 → Drilling 15 → Ingraining 30 → Ready 50.
const LADDER = [
  { name: 'EXPLORING', at: 0 },
  { name: 'DRILLING', at: 15 },
  { name: 'INGRAINING', at: 30 },
  { name: 'READY', at: 50 },
];

// Score at which an objective is "Ready" — gates Complete vs Complete-Early.
const READY_SCORE = 50;

// Criteria-metric options fall back to this static list if the snapshot didn't
// carry criteriaMetrics (keeps the picker usable in degraded / old-sample cases).
// Keys MUST match Revu.Core.Services.ObjectiveCriteria.Metrics.
const FALLBACK_METRICS = [
  { index: 0, key: '', label: 'Free text only', lowerIsBetter: false },
  { index: 1, key: 'cs_per_min', label: 'CS per minute', lowerIsBetter: false },
  { index: 2, key: 'deaths', label: 'Deaths', lowerIsBetter: true },
  { index: 3, key: 'vision_score', label: 'Vision score', lowerIsBetter: false },
  { index: 4, key: 'wards_placed', label: 'Wards placed', lowerIsBetter: false },
  { index: 5, key: 'control_wards', label: 'Control wards bought', lowerIsBetter: false },
  { index: 6, key: 'kda', label: 'KDA', lowerIsBetter: false },
  { index: 7, key: 'kill_participation', label: 'Kill participation (%)', lowerIsBetter: false },
  { index: 8, key: 'cs_total', label: 'Total CS', lowerIsBetter: false },
  { index: 9, key: 'cs_at_10', label: 'CS at 10 min', lowerIsBetter: false },
  { index: 10, key: 'gold_diff_at_10', label: 'Gold diff at 10 min', lowerIsBetter: false },
];

// Trackable-event tokens fall back to this static catalog if the snapshot didn't
// carry eventTypeOptions. MUST mirror Revu.Core.Models.GameEvent.TrackableTokens.Catalog.
const FALLBACK_EVENT_TOKENS = [
  { token: 'KILL', group: 'Combat', label: 'Kill', color: '#28c76f' },
  { token: 'DEATH', group: 'Combat', label: 'Death', color: '#ea5455' },
  { token: 'ASSIST', group: 'Combat', label: 'Assist', color: '#0099ff' },
  { token: 'MULTI_KILL', group: 'Combat', label: 'Multikill', color: '#fbbf24' },
  { token: 'FIRST_BLOOD', group: 'Combat', label: 'First Blood', color: '#ef4444' },
  { token: 'DRAGON', group: 'Objectives', label: 'Dragon', color: '#c89b3c' },
  { token: 'BARON', group: 'Objectives', label: 'Baron', color: '#8b5cf6' },
  { token: 'HERALD', group: 'Objectives', label: 'Herald', color: '#06b6d4' },
  { token: 'TURRET', group: 'Objectives', label: 'Turret', color: '#f97316' },
  { token: 'INHIBITOR', group: 'Objectives', label: 'Inhibitor', color: '#ec4899' },
  { token: 'SPELL_FLASH', group: 'Summoners', label: 'Flash', color: '#7fd4ff' },
  { token: 'SPELL_IGNITE', group: 'Summoners', label: 'Ignite', color: '#ff7043' },
  { token: 'SPELL_TELEPORT', group: 'Summoners', label: 'Teleport', color: '#5c8dff' },
  { token: 'SPELL_SMITE', group: 'Summoners', label: 'Smite', color: '#9ccc65' },
  { token: 'SPELL_EXHAUST', group: 'Summoners', label: 'Exhaust', color: '#ffca5f' },
  { token: 'SPELL_HEAL', group: 'Summoners', label: 'Heal', color: '#7fe3c0' },
  { token: 'SPELL_BARRIER', group: 'Summoners', label: 'Barrier', color: '#ffd54f' },
  { token: 'SPELL_CLEANSE', group: 'Summoners', label: 'Cleanse', color: '#80deea' },
  { token: 'SPELL_GHOST', group: 'Summoners', label: 'Ghost', color: '#b39ddb' },
  { token: 'RECALL', group: 'Macro', label: 'Recall', color: '#a9c8ff' },
  { token: 'TEAMFIGHT', group: 'Fights', label: 'Teamfight', color: '#f3a3a8' },
];

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchObjectives() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_objectives');
  }
  const res = await fetch('./sample-objectives.json');
  if (!res.ok) throw new Error(`sample-objectives.json ${res.status}`);
  return res.json();
}

// ── ring drawing (shared) ───────────────────────────────────────────────────
// Start empty (full offset = 0% drawn), then transition to the target on the
// next frame so the CSS transition on .ring-prog animates the arc filling.
function drawRing(scope, progress, circumference, progHex, dimHex) {
  const p = Math.max(0, Math.min(1, Number(progress) || 0));
  const offset = circumference * (1 - p);
  const track = scope.querySelector('.ring-track');
  const prog = scope.querySelector('.ring-prog');
  track.setAttribute('stroke', dimHex || DEFAULT_DIM);
  prog.setAttribute('stroke', progHex || DEFAULT_PROG);
  prog.setAttribute('stroke-dashoffset', String(circumference));
  requestAnimationFrame(() => requestAnimationFrame(() => {
    prog.setAttribute('stroke-dashoffset', String(offset));
  }));
}

// ── mastery ladder ──────────────────────────────────────────────────────────
// Lights the highest step whose threshold the score has reached. Mini/focus
// objectives don't level, so they skip the ladder entirely.
function buildLadder(host, score, litHex) {
  clear(host);
  let litIdx = 0;
  for (let i = 0; i < LADDER.length; i++) {
    if (score >= LADDER[i].at) litIdx = i;
  }
  LADDER.forEach((step, i) => {
    const span = document.createElement('span');
    span.textContent = `${step.name} · ${step.at}`;
    if (i === litIdx) {
      span.classList.add('on');
      if (litHex) span.style.color = litHex;
    }
    host.appendChild(span);
  });
}

// ── score sparkline ─────────────────────────────────────────────────────────
// Maps a score-history array to a polyline across the 96×30 viewBox.
function sparkPoints(history) {
  const h = Array.isArray(history) ? history.filter((n) => Number.isFinite(n)) : [];
  if (h.length < 2) return '';
  const W = 96, H = 30, pad = 3;
  const min = Math.min(...h), max = Math.max(...h);
  const span = max - min || 1;
  const step = (W - pad * 2) / (h.length - 1);
  return h.map((v, i) => {
    const x = pad + i * step;
    const y = H - pad - ((v - min) / span) * (H - pad * 2);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
}

// ── champion gate ───────────────────────────────────────────────────────────
// Show the gate only when the objective is scoped to specific champions.
function fillGate(gateEl, valEl, obj) {
  const scoped = Array.isArray(obj.champions) && obj.champions.length > 0;
  show(gateEl, scoped);
  if (scoped) valEl.textContent = obj.championsSummary || obj.champions.join(', ');
}

// ── measured criterion + hit-rate chip ───────────────────────────────────────
// Renders the "Success: …" line and a mono HIT x/y GAMES chip whose color comes
// from the server (criteriaHitRateHex: muted / win / loss). The whole row hides
// when the objective carries neither a structured criterion nor free text.
function fillCriteria(card, o) {
  const wrap = card.querySelector('.obj-card-crit');
  if (!wrap) return;
  const textEl = wrap.querySelector('.obj-crit-text');
  const hitEl = wrap.querySelector('.obj-crit-hit');

  const hasText = !!(o.hasCriteriaText && o.criteriaText);
  if (hasText) textEl.textContent = o.criteriaText;
  show(textEl, hasText);

  const hasHit = !!(o.hasStructuredCriteria && o.criteriaHitRateText);
  if (hasHit) {
    hitEl.textContent = o.criteriaHitRateText;
    if (o.criteriaHitRateHex) hitEl.style.color = o.criteriaHitRateHex;
  }
  show(hitEl, hasHit);

  show(wrap, hasText || hasHit);
}

// ── score sparkline ───────────────────────────────────────────────────────────
// Draws the per-game cumulative score polyline into the card's <svg>, tinted by
// the objective's level color, and reveals it only when hasScoreHistory.
function fillSpark(svgEl, o) {
  if (!svgEl) return;
  const show2 = !!o.hasScoreHistory && Array.isArray(o.scoreHistory) && o.scoreHistory.length >= 2;
  if (show2) {
    const line = svgEl.querySelector('.obj-spark-line');
    if (line) {
      line.setAttribute('points', sparkPoints(o.scoreHistory));
      line.setAttribute('stroke', o.levelColorHex || DEFAULT_PROG);
    }
  }
  show(svgEl, show2);
}

// ── header ──────────────────────────────────────────────────────────────────
function renderHeader(d) {
  const active = Array.isArray(d.activeObjectives) ? d.activeObjectives : [];
  const completed = Array.isArray(d.completedObjectives) ? d.completedObjectives : [];
  const parts = [];
  if (!d.hasObjectives) {
    parts.push('No objectives yet.');
  } else {
    const n = active.length;
    parts.push(n === 0 ? 'No active objectives.' : `${n} active objective${n === 1 ? '' : 's'}.`);
    const priority = active.find((o) => o.isPriority);
    if (priority) parts.push(`Priority: ${priority.title}`);
    else if (completed.length) parts.push(`${completed.length} completed`);
  }
  $('status-text').textContent = parts[0];
  const line = $('statusline');
  const statusB = line.querySelector('b');
  while (line.lastChild && line.lastChild !== statusB) line.removeChild(line.lastChild);
  const tail = parts.slice(1);
  if (tail.length) line.appendChild(document.createTextNode(' · ' + tail.join(' · ')));
}

// Map objective type → the accent class used on its tag chip.
function typeClass(o) {
  if (o.isMini || o.type === 'mini') return 'obj-tag-focus';
  if (o.isMental || o.type === 'mental') return 'obj-tag-mental';
  return 'obj-tag-gameplay';
}

// ── focus mini-drills ───────────────────────────────────────────────────────
function renderFocus(d) {
  const focus = Array.isArray(d.focusObjectives) ? d.focusObjectives : [];
  const host = $('focus-list');
  clear(host);
  show($('focus-label'), focus.length > 0);
  if (focus.length === 0) return;

  for (const o of focus) {
    const el = tpl('tpl-focus');
    if (o.id != null) el.dataset.objId = String(o.id);
    el.querySelector('.obj-mini-name').textContent = o.title || '';
    el.querySelector('.obj-mini-prog').textContent =
      o.focusProgressText || `${o.gameCount} of ${o.targetGameCount} games`;
    const fill = el.querySelector('.obj-mini-fill');
    const p = Math.max(0, Math.min(1, Number(o.progress) || 0));
    fill.style.width = `${Math.round(p * 100)}%`;
    if (o.levelColorHex) fill.style.background = o.levelColorHex;

    // Minis are "ready" once they hit their target game count; otherwise the
    // quiet Complete-Early shows (mirrors the score gate for leveling objectives).
    const miniReady = o.targetGameCount > 0 && (Number(o.gameCount) || 0) >= o.targetGameCount;
    const win = el.querySelector('.obj-act-win');
    const early = el.querySelector('.obj-act-early');
    if (win) win.hidden = !miniReady;
    if (early) early.hidden = miniReady;

    host.appendChild(el);
  }
}

// ── objectives (one unified list) ───────────────────────────────────────────
// Every non-mini active objective as a calm glass ring card under a single
// "Objectives" label. The priority objective sorts FIRST and carries a PRIORITY
// chip + accent rim; its "Make Priority" button is suppressed. Mini focus drills
// render separately in the Focus section above. With one list there is never an
// empty "active" label when the only objective is the priority one.
function renderObjectives(d) {
  const active = Array.isArray(d.activeObjectives) ? d.activeObjectives : [];
  // Non-mini objectives, priority first, otherwise original order.
  const list = active
    .filter((o) => !o.isMini)
    .sort((a, b) => (b.isPriority ? 1 : 0) - (a.isPriority ? 1 : 0));
  const host = $('active-list');
  clear(host);
  show($('active-label'), list.length > 0);
  if (list.length === 0) return;

  for (const o of list) {
    const el = tpl('tpl-active');
    if (o.id != null) el.dataset.objId = String(o.id);
    if (o.isPriority) el.classList.add('obj-card-priority');

    drawRing(el, o.progress, RING_SMALL, o.levelColorHex, o.levelDimColorHex);
    el.querySelector('.pc').textContent = `${Math.round((Number(o.progress) || 0) * 100)}%`;

    // PRIORITY chip only on the priority card.
    show(el.querySelector('.obj-card-pri'), !!o.isPriority);

    const typeEl = el.querySelector('.obj-card-type');
    typeEl.textContent = (o.type || '').toUpperCase();
    typeEl.classList.add(typeClass(o));

    el.querySelector('.obj-card-name').textContent = o.title || '';
    el.querySelector('.obj-card-meta').textContent =
      o.metaText || [o.levelName, o.phaseLabel, `${o.score} PTS`].filter(Boolean).join(' · ').toUpperCase();

    fillCriteria(el, o);
    fillSpark(el.querySelector('.obj-card-spark'), o);

    buildLadder(el.querySelector('.obj-card-ladder'), Number(o.score) || 0, o.levelColorHex);
    fillMastery(el, o);
    fillGate(el.querySelector('.obj-card-gate'), el.querySelector('.obj-card-gate .obj-gate-v'), o);

    // The priority objective can't be "made priority" — drop that button.
    if (o.isPriority) {
      const mk = el.querySelector('[data-action="set_objective_priority"]');
      if (mk) mk.remove();
    }

    // P-037: completion is gated by MASTERY (skill held over a horizon), not the
    // EFFORT score. The server OR's masteryMet with the legacy score>=50 so an
    // objective already Ready never regresses (forward-only).
    toggleCompleteButtons(el, o);

    host.appendChild(el);
  }
}

// P-037 mastery meter: fill the bar to masteryPct and caption it. Hidden for
// minis and when the server reports no mastery text (e.g. mini drills).
function fillMastery(card, o) {
  const wrap = card.querySelector('.obj-card-mastery');
  if (!wrap) return;
  const showMeter = !o.isMini && !!o.masteryText;
  show(wrap, showMeter);
  if (!showMeter) return;

  const pct = Math.max(0, Math.min(100, Number(o.masteryPct) || 0));
  wrap.classList.toggle('is-met', !!o.masteryMet);
  const fill = wrap.querySelector('.obj-mastery-fill');
  if (fill) {
    fill.style.width = pct + '%';
    fill.classList.toggle('is-met', !!o.masteryMet);
  }
  const textEl = wrap.querySelector('.obj-mastery-text');
  if (textEl) textEl.textContent = o.masteryText || '';
  const gateEl = wrap.querySelector('.obj-mastery-gate');
  if (gateEl) {
    gateEl.textContent = o.masteryMet ? '' : (o.masteryGateText || '');
    show(gateEl, !o.masteryMet && !!o.masteryGateText);
  }
}

// Show exactly one of the two completion buttons on a card based on readiness.
// P-037: readiness = masteryMet (skill held over the horizon), NOT score>=50.
// The green "Mark Complete" shows when mastered; else the quiet "Complete Early".
function toggleCompleteButtons(card, o) {
  // Back-compat: a bare number still works (legacy callers / tests).
  const ready = typeof o === 'number'
    ? o >= READY_SCORE
    : !!o.masteryMet;
  const win = card.querySelector('.obj-act-win');
  const early = card.querySelector('.obj-act-early');
  if (win) win.hidden = !ready;
  if (early) early.hidden = ready;
}

// ── spotted problems (clickable game rows) ──────────────────────────────────
function renderSpotted(d) {
  const items = Array.isArray(d.spottedProblems) ? d.spottedProblems : [];
  const host = $('spotted-list');
  clear(host);
  show($('spotted-label'), items.length > 0);
  if (items.length === 0) return;

  for (const s of items) {
    const el = tpl('tpl-spotted');
    const wl = el.querySelector('.obj-spot-wl');
    wl.textContent = s.resultText || '';
    if (s.resultColorHex) wl.style.color = s.resultColorHex;

    el.querySelector('.obj-spot-champ').textContent = s.championDisplay || s.championName || '';
    el.querySelector('.obj-spot-date').textContent = s.datePlayed || '';
    el.querySelector('.obj-spot-text').textContent = s.problemText || '';

    if (s.gameId != null) el.dataset.gameId = String(s.gameId);
    // Left edge bar rests in the game's win/loss color, energizes to accent on hover.
    if (s.resultColorHex) el.style.setProperty('--wl', s.resultColorHex);

    host.appendChild(el);
  }
}

// ── completed objectives (collapsed by default) ─────────────────────────────
let _completedOpen = false;
function renderCompleted(d) {
  const items = Array.isArray(d.completedObjectives) ? d.completedObjectives : [];
  const host = $('completed-list');
  clear(host);
  const has = items.length > 0;
  show($('completed-toggle-wrap'), has);
  if (!has) { show(host, false); return; }

  $('completed-toggle-label').textContent =
    `${_completedOpen ? 'Hide' : 'Show'} completed (${items.length})`;
  $('completed-toggle').setAttribute('aria-expanded', String(_completedOpen));
  $('completed-toggle').querySelector('.arr').textContent = _completedOpen ? '▾' : '▸';
  show(host, _completedOpen);

  for (const c of items) {
    const el = tpl('tpl-completed');
    // id is needed so the row's Edit / Games / Notes / Delete actions resolve.
    if (c.id != null) el.dataset.objId = String(c.id);
    el.querySelector('.obj-done-name').textContent = c.title || '';
    el.querySelector('.obj-done-phase').textContent = (c.phaseLabel || '').toUpperCase();
    el.querySelector('.obj-done-sum').textContent =
      c.summaryText || `${c.score} pts · ${c.gameCount} games`;
    host.appendChild(el);
  }
}

// ── empty state ─────────────────────────────────────────────────────────────
function renderEmpty(d) {
  show($('empty'), !d.hasObjectives);
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  const panel = $('errpanel');
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show(panel, true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
// Only on the FIRST render of a page load (not on every refresh).
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('active-list'),
    $('focus-list'),
    $('spotted-list'),
    $('completed-toggle-wrap'),
  ].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderObjectives(d);
  renderFocus(d);
  renderSpotted(d);
  renderCompleted(d);
  renderEmpty(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _lastData = null;
let _loading = false;
async function loadObjectives() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchObjectives();
    _lastData = data;
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[objectives] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── objective lookup ─────────────────────────────────────────────────────────
// Resolve a card's objId back to the full objective object from the last fetch,
// so Edit can prefill and the mutations can carry the right id.
function objectiveById(id) {
  if (_lastData == null) return null;
  const pools = [_lastData.activeObjectives, _lastData.focusObjectives, _lastData.completedObjectives];
  for (const pool of pools) {
    if (!Array.isArray(pool)) continue;
    const hit = pool.find((o) => String(o.id) === String(id));
    if (hit) return hit;
  }
  return null;
}

// ── create / edit form ───────────────────────────────────────────────────────
// One <form> serves both create_objective (no id) and update_objective (id set).
// _editId holds the id under edit, or null when creating. The form's richer
// surface — custom prompts, champion gate, focus phase, structured criterion — is
// held in module state and serialized into the submit payload.
let _editId = null;
// Custom-prompt drafts: { id, phaseIndex, label } (id 0 = new). Order = sortOrder.
let _promptRows = [];
// Picked champion gate (user casing preserved; de-duped case-insensitively).
let _champs = [];
// Picker data (from the snapshot's playedChampions / criteriaMetrics, or per-
// objective hydration on edit). Defaulted to the static fallback.
let _criteriaMetrics = FALLBACK_METRICS;
let _playedChampions = [];
// Tracked-event gate: the set of selected event tokens + the catalog to render.
let _eventTypes = [];           // Set-like array of selected tokens (UPPERCASE)
let _eventTypeOptions = FALLBACK_EVENT_TOKENS;

function setChecked(id, on) { const el = $(id); if (el) el.checked = !!on; }
function setVal(id, v) { const el = $(id); if (el) el.value = v == null ? '' : String(v); }
function getVal(id) { const el = $(id); return el ? el.value.trim() : ''; }
function getChecked(id) { const el = $(id); return !!(el && el.checked); }

// Reveal the target-game-count field only for the mini type (focus drills).
function syncTargetVisibility() {
  show($('f-target-wrap'), $('f-type').value === 'mini');
}

// ── criteria metric picker ────────────────────────────────────────────────────
// Populate the metric <select> from the snapshot's option list; op + value reveal
// only once a metric (index>0) is chosen, with the comparator default flipped for
// "lower is better" metrics (e.g. deaths → At most).
function fillMetricOptions() {
  const sel = $('f-crit-metric');
  if (!sel) return;
  clear(sel);
  for (const m of _criteriaMetrics) {
    const opt = document.createElement('option');
    opt.value = String(m.index);
    opt.textContent = m.label;
    sel.appendChild(opt);
  }
}

// Show/hide the op + value inputs based on the chosen metric. When flipDefault is
// true (a fresh user pick) the comparator resets from the metric's lowerIsBetter.
function syncCriteriaVisibility(flipDefault) {
  const metricIdx = parseInt(getVal('f-crit-metric'), 10) || 0;
  const on = metricIdx > 0;
  const op = $('f-crit-op'); const val = $('f-crit-val');
  show(op, on); show(val, on);
  const row = $('f-crit-metric').closest('.obj-crit-row');
  if (row) row.classList.toggle('metric-only', !on);
  if (on && flipDefault) {
    const m = _criteriaMetrics.find((x) => x.index === metricIdx);
    if (m) op.value = m.lowerIsBetter ? '1' : '0';
  }
}

// ── custom-prompt editor ──────────────────────────────────────────────────────
// First checked practice phase (pre→in→post) → default phase index for new rows.
function defaultPromptPhaseIndex() {
  if (getChecked('f-pre')) return 0;
  if (getChecked('f-in')) return 1;
  if (getChecked('f-post')) return 2;
  return 1;
}

function renderPromptRows() {
  const host = $('f-prompts');
  clear(host);
  _promptRows.forEach((row, i) => {
    const el = tpl('tpl-prompt-row');
    el.dataset.idx = String(i);
    const phase = el.querySelector('.obj-prompt-phase');
    phase.value = String(row.phaseIndex == null ? 1 : row.phaseIndex);
    phase.addEventListener('change', () => { _promptRows[i].phaseIndex = parseInt(phase.value, 10) || 0; });
    const label = el.querySelector('.obj-prompt-label');
    label.value = row.label || '';
    label.addEventListener('input', () => { _promptRows[i].label = label.value; });
    host.appendChild(el);
  });
}

function addPromptRow() {
  _promptRows.push({ id: 0, phaseIndex: defaultPromptPhaseIndex(), label: '' });
  renderPromptRows();
  // Focus the new row's label.
  const host = $('f-prompts');
  const last = host.lastElementChild;
  if (last) { const t = last.querySelector('.obj-prompt-label'); if (t) t.focus(); }
}

function removePromptRow(idx) {
  if (idx < 0 || idx >= _promptRows.length) return;
  _promptRows.splice(idx, 1);
  renderPromptRows();
}

// ── tracked-event gate ─────────────────────────────────────────────────────────
// Render the event-token picker as grouped pill-toggles. Each pill toggles its
// token in _eventTypes; a selected pill lights up in its own event color. Groups
// (Combat / Objectives / Summoners / Fights) get a tiny eyebrow label.
function renderEventPicker() {
  const host = $('f-events');
  if (!host) return;
  clear(host);
  const selected = new Set(_eventTypes.map((t) => String(t).toUpperCase()));
  // Preserve catalog order; bucket into groups in first-seen order.
  const groups = [];
  const byGroup = new Map();
  for (const opt of _eventTypeOptions) {
    if (!byGroup.has(opt.group)) { byGroup.set(opt.group, []); groups.push(opt.group); }
    byGroup.get(opt.group).push(opt);
  }
  for (const group of groups) {
    const sec = document.createElement('div');
    sec.className = 'obj-evtgroup';
    const eyebrow = document.createElement('div');
    eyebrow.className = 'obj-evtgroup-k';
    eyebrow.textContent = group;
    sec.appendChild(eyebrow);
    const row = document.createElement('div');
    row.className = 'obj-evtrow';
    for (const opt of byGroup.get(group)) {
      const token = String(opt.token).toUpperCase();
      const pill = document.createElement('button');
      pill.type = 'button';
      pill.className = 'obj-evtpill' + (selected.has(token) ? ' on' : '');
      pill.dataset.token = token;
      pill.style.setProperty('--evt', opt.color || 'var(--accent)');
      pill.textContent = opt.label || token;
      pill.setAttribute('aria-pressed', selected.has(token) ? 'true' : 'false');
      pill.addEventListener('click', (e) => { e.preventDefault(); toggleEventToken(token); });
      row.appendChild(pill);
    }
    sec.appendChild(row);
    host.appendChild(sec);
  }
}

function toggleEventToken(token) {
  const t = String(token).toUpperCase();
  const i = _eventTypes.findIndex((x) => String(x).toUpperCase() === t);
  if (i >= 0) _eventTypes.splice(i, 1); else _eventTypes.push(t);
  renderEventPicker();
}

// ── champion gate ─────────────────────────────────────────────────────────────
function renderChampChips() {
  const host = $('f-champs');
  clear(host);
  for (const name of _champs) {
    const chip = tpl('tpl-champ-chip');
    chip.querySelector('.obj-chip-name').textContent = name;
    chip.dataset.champ = name;
    host.appendChild(chip);
  }
  syncChampDatalist();
}

// The datalist offers played champions not already picked (case-insensitive).
function syncChampDatalist() {
  const list = $('f-champ-list');
  if (!list) return;
  clear(list);
  const picked = new Set(_champs.map((c) => c.toLowerCase()));
  for (const name of _playedChampions) {
    if (picked.has(String(name).toLowerCase())) continue;
    const opt = document.createElement('option');
    opt.value = name;
    list.appendChild(opt);
  }
}

function addChamp(raw) {
  const name = (raw == null ? '' : String(raw)).trim();
  if (!name) return;
  if (_champs.some((c) => c.toLowerCase() === name.toLowerCase())) return; // de-dupe
  _champs.push(name);
  renderChampChips();
}

function removeChamp(name) {
  if (!name) return;
  const i = _champs.findIndex((c) => c.toLowerCase() === String(name).toLowerCase());
  if (i < 0) return;
  _champs.splice(i, 1);
  renderChampChips();
}

function clearFormError() { show($('form-err'), false); $('form-err').textContent = ''; }
function setFormError(msg) { const e = $('form-err'); e.textContent = msg; show(e, true); }

// Reset every editor field to its create-form default (mirrors ResetFormFields).
function resetFormFields() {
  setVal('f-title', ''); setVal('f-skill', ''); setVal('f-criteria', '');
  setVal('f-desc', ''); setVal('f-target', '3');
  $('f-type').value = 'primary';
  $('f-focus').value = '0';
  setChecked('f-pre', true); setChecked('f-in', true); setChecked('f-post', false);
  fillMetricOptions();
  $('f-crit-metric').value = '0';
  $('f-crit-op').value = '0';
  setVal('f-crit-val', '');
  syncCriteriaVisibility(false);
  _promptRows = [];
  renderPromptRows();
  _champs = [];
  setVal('f-champ-input', '');
  renderChampChips();
  _eventTypes = [];
  renderEventPicker();
}

function revealForm() {
  clearFormError();
  syncTargetVisibility();
  show($('obj-form'), true);
  $('obj-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('f-title').focus();
}

// Open the form in create mode: blank fields, default phases, "Create" label.
function openCreateForm() {
  _editId = null;
  // Pull picker data from the last snapshot (created form needs it up front).
  if (_lastData) {
    if (Array.isArray(_lastData.criteriaMetrics) && _lastData.criteriaMetrics.length) {
      _criteriaMetrics = _lastData.criteriaMetrics;
    }
    _playedChampions = Array.isArray(_lastData.playedChampions) ? _lastData.playedChampions : [];
    if (Array.isArray(_lastData.eventTypeOptions) && _lastData.eventTypeOptions.length) {
      _eventTypeOptions = _lastData.eventTypeOptions;
    }
  }
  $('form-title').textContent = 'New Objective';
  $('form-submit').textContent = 'Create';
  resetFormFields();
  revealForm();
}

// Open the form in edit mode for objId. Active + completed objectives both edit
// through this top form; full hydration comes from get_objective (prompts /
// champions / criterion / focus phase aren't on the list snapshot).
async function openEditForm(id) {
  if (id == null) return;

  // Hydrate FIRST, before opening the form. The Edit form's submit replaces the
  // objective's side tables (prompts, tied events, structured criterion, focus
  // phase) WHOLESALE. The list snapshot (objectiveById) does NOT carry those
  // fields, so opening the form from it and saving would silently delete them.
  // If full hydration fails (transient 404 / locked DB / sidecar restart), refuse
  // to open a lossy edit form — show a retry message instead of destroying data.
  const hydrated = await fetchObjectiveForEdit(id);
  if (!hydrated) {
    // Surface on the always-visible statusline (the form is still closed, so its
    // own #form-err wouldn't show). Non-destructive: the objective is left intact.
    const statusB = document.querySelector('#statusline b');
    if (statusB) statusB.textContent = "Couldn't load that objective to edit — try again.";
    return;
  }

  _editId = Number(id);
  $('form-title').textContent = 'Edit Objective';
  $('form-submit').textContent = 'Save Changes';
  resetFormFields();
  revealForm();

  const o = hydrated;

  if (Array.isArray(hydrated && hydrated.playedChampions)) _playedChampions = hydrated.playedChampions;
  if (Array.isArray(hydrated && hydrated.criteriaMetrics) && hydrated.criteriaMetrics.length) {
    _criteriaMetrics = hydrated.criteriaMetrics;
    fillMetricOptions();
  }
  if (Array.isArray(hydrated && hydrated.eventTypeOptions) && hydrated.eventTypeOptions.length) {
    _eventTypeOptions = hydrated.eventTypeOptions;
  }

  setVal('f-title', o.title || '');
  setVal('f-skill', o.skillArea || '');
  setVal('f-criteria', o.completionCriteria || '');
  setVal('f-desc', o.description || '');
  setVal('f-target', o.targetGameCount || '3');
  $('f-type').value = (o.isMini || o.type === 'mini') ? 'mini'
    : (o.isMental || o.type === 'mental') ? 'mental' : 'primary';
  syncTargetVisibility();

  // Phase checkboxes from explicit bools (hydrated) or the list's phasesSummary.
  applyPhases(o);

  // Focus phase + structured criterion (only on hydrated edit payloads).
  if (o.focusPhaseIndex != null) $('f-focus').value = String(o.focusPhaseIndex);
  if (o.criteriaMetricIndex != null) {
    $('f-crit-metric').value = String(o.criteriaMetricIndex);
    $('f-crit-op').value = String(o.criteriaOpIndex || 0);
    setVal('f-crit-val', o.criteriaValueText || '');
  }
  syncCriteriaVisibility(false);

  // Custom prompts.
  _promptRows = Array.isArray(o.prompts)
    ? o.prompts.map((p) => ({ id: p.id || 0, phaseIndex: p.phaseIndex == null ? 1 : p.phaseIndex, label: p.label || '' }))
    : [];
  renderPromptRows();

  // Champion gate.
  _champs = Array.isArray(o.champions) ? o.champions.slice() : [];
  renderChampChips();

  // Tracked-event gate.
  _eventTypes = Array.isArray(o.eventTypes) ? o.eventTypes.map((t) => String(t).toUpperCase()) : [];
  renderEventPicker();

  $('f-title').focus();
}

// Map the persisted phasesSummary ("PRE + IN", "ALL CHAMPIONS"…) → checkbox set.
// Prefers explicit booleans when the objective carries practicePre/In/Post.
function applyPhases(o) {
  if (o && (o.practicePre != null || o.practiceIn != null || o.practicePost != null)) {
    setChecked('f-pre', !!o.practicePre);
    setChecked('f-in', !!o.practiceIn);
    setChecked('f-post', !!o.practicePost);
    return;
  }
  const s = ((o && (o.phasesSummary || o.phaseLabel)) || '').toUpperCase();
  if (!s || s.includes('ALL')) {
    setChecked('f-pre', true);
    setChecked('f-in', true);
    setChecked('f-post', s.includes('ALL'));
    return;
  }
  setChecked('f-pre', s.includes('PRE'));
  setChecked('f-in', s.includes('IN'));
  setChecked('f-post', s.includes('POST'));
}

// Fetch the full edit hydration for one objective. Returns the `objective` object
// (with prompts/champions/criterion/focus + playedChampions/criteriaMetrics merged
// on) or null. Falls back to the bundled sample in browser preview.
async function fetchObjectiveForEdit(id) {
  try {
    const invoke = await getInvoke();
    let res;
    if (invoke) {
      res = await invoke('get_objective', { id: Number(id) });
    } else {
      const r = await fetch('./sample-objective.json');
      if (!r.ok) throw new Error(`sample-objective.json ${r.status}`);
      res = await r.json();
    }
    if (!res || !res.objective) return null;
    const obj = res.objective;
    obj.criteriaMetrics = Array.isArray(res.criteriaMetrics) ? res.criteriaMetrics : null;
    obj.playedChampions = Array.isArray(obj.playedChampions) ? obj.playedChampions : [];
    return obj;
  } catch (err) {
    console.error('[objectives] get_objective failed:', err);
    return null;
  }
}

function closeForm() {
  _editId = null;
  clearFormError();
  show($('obj-form'), false);
}

// Assemble the create/update payload from the form. Returns null (+ inline error)
// when the required title is missing or no practice phase is checked.
function readFormPayload() {
  const title = getVal('f-title');
  if (!title) { setFormError('Title is required.'); $('f-title').focus(); return null; }
  if (!getChecked('f-pre') && !getChecked('f-in') && !getChecked('f-post')) {
    setFormError('Pick at least one practice phase (Pre / In / Post).');
    return null;
  }
  const type = $('f-type').value;

  // Prompts: drop blank-label rows; sortOrder is the kept-row index server-side.
  const prompts = _promptRows
    .filter((r) => (r.label || '').trim().length > 0)
    .map((r) => ({ id: r.id || 0, phase: phaseFromIndex(r.phaseIndex), label: r.label.trim() }));

  const n = parseInt(getVal('f-target'), 10);
  const payload = {
    title,
    skillArea: getVal('f-skill'),
    type,
    completionCriteria: getVal('f-criteria'),
    description: getVal('f-desc'),
    practicePre: getChecked('f-pre'),
    practiceIn: getChecked('f-in'),
    practicePost: getChecked('f-post'),
    targetGameCount: (type === 'mini') ? (Number.isFinite(n) && n > 0 ? n : 3) : 0,
    prompts,
    champions: _champs.slice(),
    eventTypes: _eventTypes.slice(),
    focusPhaseIndex: parseInt(getVal('f-focus'), 10) || 0,
    criteriaMetricIndex: parseInt(getVal('f-crit-metric'), 10) || 0,
    criteriaOpIndex: parseInt(getVal('f-crit-op'), 10) || 0,
    criteriaValueText: getVal('f-crit-val'),
  };
  return payload;
}

// WinUI PhaseIndex (0 pre / 1 in / 2 post) → the persisted phase string.
function phaseFromIndex(idx) {
  return idx === 0 ? 'pregame' : idx === 2 ? 'postgame' : 'ingame';
}

// Submit the form → create_objective or update_objective, then reload.
async function submitForm(submitBtn) {
  const payload = readFormPayload();
  if (!payload) return;

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[objectives] (preview) submit — no Tauri backend.');
    closeForm();
    return;
  }

  if (_editId != null) payload.id = _editId;
  const cmd = _editId != null ? 'update_objective' : 'create_objective';

  if (submitBtn) submitBtn.disabled = true;
  try {
    await invoke(cmd, { payload });
    closeForm();
    await loadObjectives();
  } catch (err) {
    setFormError((err && err.message) ? err.message : String(err));
    console.error(`[objectives] ${cmd} failed:`, err);
  } finally {
    if (submitBtn) submitBtn.disabled = false;
  }
}

// ── celebration overlay ───────────────────────────────────────────────────────
// Shown after a successful mark-complete; auto-dismisses after 5s. Stats line
// mirrors the WinUI "{score} pts • {games} games played".
let _celebrateTimer = null;
function showCelebration(obj) {
  if (!obj) return;
  $('celebrate-title').textContent = obj.title || '';
  const games = Number(obj.gameCount) || 0;
  $('celebrate-stats').textContent = `${Number(obj.score) || 0} pts  •  ${games} games played`;
  show($('celebrate'), true);
  if (_celebrateTimer) clearTimeout(_celebrateTimer);
  _celebrateTimer = setTimeout(dismissCelebration, 5000);
}
function dismissCelebration() {
  if (_celebrateTimer) { clearTimeout(_celebrateTimer); _celebrateTimer = null; }
  show($('celebrate'), false);
}

// ── delete confirm ────────────────────────────────────────────────────────────
// No ContentDialog here — a native confirm() matches the desktop shell's simple
// surfaces and keeps the destructive op gated behind an explicit yes.
function confirmDeleteObjective(obj) {
  const title = (obj && obj.title) ? `"${obj.title}"` : 'this objective';
  return window.confirm(`Delete ${title}? This also removes its prompts, notes and game links. This can't be undone.`);
}

// ── single delegated action handler ─────────────────────────────────────────
// Local (no backend):
//   toggle_completed = expand/collapse the completed list.
//   new_objective    = open the create form.
//   edit_objective   = open the edit form, prefilled.
//   cancel_form      = close the form.
//   open_obj_games   = navigate to the objective's Games drill-down (?id=N).
//   open_obj_notes   = navigate to the objective's Notes aggregator (?id=N).
// Form submit:
//   submit_form      = create_objective / update_objective.
// Per-objective mutations (carry {id}):
//   set_objective_priority, complete_objective.
// Navigation (carry {gameId}):
//   open_review (spotted-problem row → review page, deferred).
// Local actions never touch the backend (form chrome + navigation + celebration).
const LOCAL_ACTIONS = new Set([
  'toggle_completed', 'new_objective', 'edit_objective', 'cancel_form',
  'open_obj_games', 'open_obj_notes', 'open_review',
  'add_prompt', 'remove_prompt', 'remove_champ', 'dismiss_celebration',
]);
// Backend mutations that carry only {id} resolved from the enclosing card.
const ID_ACTIONS = new Set(['set_objective_priority']);
const ACTIONS = new Set([
  'open_review', 'toggle_completed', 'new_objective', 'edit_objective',
  'cancel_form', 'submit_form', 'set_objective_priority', 'complete_objective',
  'delete_objective', 'open_obj_games', 'open_obj_notes',
  'add_prompt', 'remove_prompt', 'remove_champ', 'dismiss_celebration',
]);

// Resolve the objective id for an id-scoped button by walking up to its card.
function objIdForTarget(target) {
  const card = target.closest('[data-obj-id]');
  return card ? card.dataset.objId : null;
}

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Local-only actions never touch the backend.
  if (LOCAL_ACTIONS.has(action)) {
    if (action === 'toggle_completed') {
      _completedOpen = !_completedOpen;
      if (_lastData) renderCompleted(_lastData);
    } else if (action === 'new_objective') {
      openCreateForm();
    } else if (action === 'edit_objective') {
      await openEditForm(objIdForTarget(target));
    } else if (action === 'cancel_form') {
      closeForm();
    } else if (action === 'open_obj_games') {
      const id = objIdForTarget(target);
      if (id != null) window.location.href = `objectivegames.html?id=${encodeURIComponent(id)}`;
    } else if (action === 'open_obj_notes') {
      const id = objIdForTarget(target);
      if (id != null) window.location.href = `objectivenotes.html?id=${encodeURIComponent(id)}`;
    } else if (action === 'open_review') {
      // A spotted-problem row → that game's review page (which surfaces its VOD /
      // clip if available). Read the gameId off the row, not a backend call.
      const row = target.closest('[data-game-id]');
      const gid = row ? row.dataset.gameId : target.dataset.gameId;
      if (gid != null) window.location.href = `review.html?gameId=${encodeURIComponent(gid)}`;
    } else if (action === 'add_prompt') {
      addPromptRow();
    } else if (action === 'remove_prompt') {
      const row = target.closest('.obj-prompt-row');
      if (row) removePromptRow(parseInt(row.dataset.idx, 10));
    } else if (action === 'remove_champ') {
      removeChamp(target.dataset.champ);
    } else if (action === 'dismiss_celebration') {
      dismissCelebration();
    }
    return;
  }

  if (action === 'submit_form') {
    await submitForm(target);
    return;
  }

  // ── Backend mutations ──
  const objId = objIdForTarget(target);

  // Delete: confirm first, then call delete_objective.
  if (action === 'delete_objective') {
    if (objId == null) return;
    const obj = objectiveById(objId);
    if (!confirmDeleteObjective(obj)) return;
    const invokeDel = await getInvoke();
    if (!invokeDel) { console.info('[objectives] (preview) delete — no Tauri backend.'); return; }
    if ('disabled' in target) target.disabled = true;
    try {
      await invokeDel('delete_objective', { payload: { id: Number(objId) } });
      await loadObjectives();
    } catch (err) {
      renderError(err);
      console.error('[objectives] delete_objective failed:', err);
    } finally {
      if ('disabled' in target) target.disabled = false;
    }
    return;
  }

  // Complete (early or ready): both route to complete_objective; on success show
  // the milestone celebration with the objective's pre-complete stats.
  if (action === 'complete_objective') {
    if (objId == null) return;
    const obj = objectiveById(objId);
    const invokeC = await getInvoke();
    if (!invokeC) { console.info('[objectives] (preview) complete — no Tauri backend.'); return; }
    if ('disabled' in target) target.disabled = true;
    try {
      await invokeC('complete_objective', { payload: { id: Number(objId) } });
      await loadObjectives();
      showCelebration(obj);
    } catch (err) {
      renderError(err);
      console.error('[objectives] complete_objective failed:', err);
    } finally {
      if ('disabled' in target) target.disabled = false;
    }
    return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    // Browser preview: no backend to talk to. Acknowledge in the console.
    console.info(`[objectives] (preview) action "${action}" — no Tauri backend.`);
    return;
  }

  const args = {};
  if (ID_ACTIONS.has(action)) {
    if (objId == null) return;
    args.id = Number(objId);
  }
  if (target.dataset.gameId != null) args.gameId = Number(target.dataset.gameId);

  // `disabled` only exists on form controls; rows/divs lack it, so guard.
  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  try {
    // Only GROUP A commands (set_objective_priority — the sole ID_ACTION) reach
    // this dynamic site; all reads/navigation returned earlier. GROUP A takes a
    // single `payload` arg, so wrap the named args in { payload }.
    await invoke(action, { payload: args });
    await loadObjectives();
  } catch (err) {
    renderError(err);
    console.error(`[objectives] action "${action}" failed:`, err);
  } finally {
    if (canDisable) target.disabled = false;
  }
});

// Form change wiring: type→target visibility, metric→op/value visibility, and
// practice-phase toggles re-default new prompt rows' phase.
document.addEventListener('change', (ev) => {
  const id = ev.target && ev.target.id;
  if (id === 'f-type') syncTargetVisibility();
  else if (id === 'f-crit-metric') syncCriteriaVisibility(true);
});

// Champion typeahead: Enter (or picking a datalist suggestion) adds the champ.
document.addEventListener('keydown', (ev) => {
  if (ev.target && ev.target.id === 'f-champ-input' && ev.key === 'Enter') {
    ev.preventDefault();
    addChamp(ev.target.value);
    ev.target.value = '';
    return;
  }
  // Keyboard activation for the role="button" spotted rows (Enter / Space).
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// Picking a datalist suggestion fires an 'input' (and on commit, 'change') on the
// champ input — commit it as a chip when the value matches a played champion.
document.addEventListener('change', (ev) => {
  if (!(ev.target && ev.target.id === 'f-champ-input')) return;
  const v = ev.target.value.trim();
  if (!v) return;
  // Only auto-commit when it matches a known played champion (datalist pick);
  // free typed text commits on Enter (handled above) to avoid premature adds.
  if (_playedChampions.some((c) => String(c).toLowerCase() === v.toLowerCase())) {
    addChamp(v);
    ev.target.value = '';
  }
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', loadObjectives);
} else {
  loadObjectives();
}
