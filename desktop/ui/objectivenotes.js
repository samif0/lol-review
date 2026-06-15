// Revu desktop — Objective Notes page renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_objective_notes`
// (see Revu.Sidecar GET /api/objective/notes?id=N): a read-only aggregation of
// review notes + execution notes + clips/bookmarks for ONE objective. Each row
// jumps back to its source (review page / VOD player) via plain file routing.
// Mirrors app.js conventions exactly:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-objective-notes.json (browser preview).
//   • Every server string is written via textContent (never innerHTML).
//   • ONE delegated [data-action] click handler.

// ── invoke resolver ────────────────────────────────────────────────────────
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

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}

// The objective id this page was opened for (?id=N).
function objectiveId() {
  const v = Number(new URLSearchParams(window.location.search).get('id') || 0);
  return Number.isFinite(v) && v > 0 ? v : 0;
}

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchObjectiveNotes() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_objective_notes', { id: objectiveId() });
  }
  const res = await fetch('./sample-objective-notes.json');
  if (!res.ok) throw new Error(`sample-objective-notes.json ${res.status}`);
  return res.json();
}

// ── render: header ────────────────────────────────────────────────────────
function renderHeader(d) {
  $('obj-title').textContent = d.objectiveTitle || 'Objective';
  $('obj-status').textContent = d.objectiveStatus || '';

  const parts = [];
  const r = Array.isArray(d.reviewNotes) ? d.reviewNotes.length : 0;
  const e = Array.isArray(d.executionNotes) ? d.executionNotes.length : 0;
  const c = Array.isArray(d.bookmarks) ? d.bookmarks.length : 0;
  if (r) parts.push(r === 1 ? '1 review note' : `${r} review notes`);
  if (e) parts.push(e === 1 ? '1 execution note' : `${e} execution notes`);
  if (c) parts.push(c === 1 ? '1 clip' : `${c} clips`);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.length ? parts.join(' · ') : 'Nothing linked yet';
}

// ── render: review notes ────────────────────────────────────────────────────
function renderReview(d) {
  const items = Array.isArray(d.reviewNotes) ? d.reviewNotes : [];
  const host = $('review-list');
  clear(host);
  show($('review-sec'), !!d.hasReviewNotes && items.length > 0);
  for (const n of items) {
    const el = tpl('tpl-review');
    if (n.gameId != null) el.dataset.gameId = String(n.gameId);
    el.querySelector('.on-card-header').textContent = n.header || '';
    el.querySelector('.on-card-body').textContent = n.notes || '';
    host.appendChild(el);
  }
}

// ── render: execution notes (no jump button — read-only display) ────────────
function renderExec(d) {
  const items = Array.isArray(d.executionNotes) ? d.executionNotes : [];
  const host = $('exec-list');
  clear(host);
  show($('exec-sec'), !!d.hasExecutionNotes && items.length > 0);
  for (const n of items) {
    const el = tpl('tpl-exec');
    if (n.gameId != null) el.dataset.gameId = String(n.gameId);
    el.querySelector('.on-card-header').textContent = n.header || '';
    el.querySelector('.on-card-body').textContent = n.note || '';
    host.appendChild(el);
  }
}

// ── render: clips & bookmarks ───────────────────────────────────────────────
function renderClips(d) {
  const items = Array.isArray(d.bookmarks) ? d.bookmarks : [];
  const host = $('clip-list');
  clear(host);
  show($('clip-sec'), !!d.hasBookmarks && items.length > 0);
  for (const b of items) {
    const el = tpl('tpl-clip');
    if (b.gameId != null) el.dataset.gameId = String(b.gameId);
    el.dataset.seek = String(Number(b.gameTimeSeconds) || 0);
    el.querySelector('.on-clip-time').textContent = b.timeLabel || '';
    el.querySelector('.on-clip-game').textContent = b.gameLabel || '';

    const note = el.querySelector('.on-card-body');
    show(note, !!b.hasNote && !!b.note);
    if (b.hasNote && b.note) note.textContent = b.note;

    const tags = el.querySelector('.on-clip-tags');
    show(tags, !!b.hasTags && !!b.tags);
    if (b.hasTags && b.tags) tags.textContent = b.tags;

    host.appendChild(el);
  }
}

// ── render: empty state ─────────────────────────────────────────────────────
function renderEmpty(d) {
  show($('empty'), !d.hasAnything);
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [$('review-sec'), $('exec-sec'), $('clip-sec')].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderReview(d);
  renderExec(d);
  renderClips(d);
  renderEmpty(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function load() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchObjectiveNotes();
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[objectivenotes] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── navigation helpers ──────────────────────────────────────────────────────
function gameIdForTarget(target) {
  const row = target.closest('[data-game-id]');
  return row ? row.dataset.gameId : null;
}

// ── single delegated action handler ─────────────────────────────────────────
// back        = pop history (returns to objectives page).
// open_review = open the full review page for that game (review-note rows only).
// play_clip   = open the VOD player seeked to the bookmark timestamp.
const ACTIONS = new Set(['back', 'open_review', 'play_clip']);

document.addEventListener('click', (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  if (action === 'back') {
    if (window.history.length > 1) window.history.back();
    else window.location.href = 'objectives.html';
    return;
  }

  const gid = gameIdForTarget(target);
  if (!gid) return;

  if (action === 'open_review') {
    window.location.href = `review.html?gameId=${encodeURIComponent(gid)}`;
  } else if (action === 'play_clip') {
    // Seek the VOD to the bookmark timestamp (vodplayer reads ?t=seconds).
    const row = target.closest('[data-seek]');
    const t = row ? Number(row.dataset.seek) || 0 : 0;
    const seek = t > 0 ? `&t=${encodeURIComponent(t)}` : '';
    window.location.href = `vodplayer.html?gameId=${encodeURIComponent(gid)}${seek}`;
  }
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', load);
} else {
  load();
}
