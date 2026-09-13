import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { objectiveDisplayText } from './objective-labels.mjs';

// Revu desktop — Objective Games page renderer for the glass-aurora layout.
// Renders the JSON returned by the Electron command `get_objective_games`
// (see Revu.Sidecar GET /api/objective/games?id=N): every game linked to ONE
// objective + its evidence ledger. Read-only — Watch VOD / Review jumps are
// plain file-route navigation. Mirrors app.js conventions exactly:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron it fetches ./sample-objective-games.json so the page
//     previews in a plain browser.
//   • Every server string is written via textContent (never innerHTML); colors
//     arrive as *Hex strings applied to style properties only.
//   • ONE delegated [data-action] click handler.

// ── small DOM helpers ───────────────────────────────────────────────────────

// The objective id this page was opened for (?id=N). Carried into Watch VOD so
// the VOD viewer can scope to just this objective (objectiveId query param).
function objectiveId() {
  const v = Number(new URLSearchParams(window.location.search).get('id') || 0);
  return Number.isFinite(v) && v > 0 ? v : 0;
}

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchObjectiveGames() {
  return readSnapshot('get_objective_games', 'sample-objective-games.json', { id: objectiveId() });
}

// ── render: header ────────────────────────────────────────────────────────
function renderHeader(d) {
  $('obj-title').textContent = d.objectiveTitle || 'Objective';
  $('obj-status').textContent = objectiveDisplayText(d.objectiveStatus);
  const counter = $('obj-counter');
  const hasCounter = !!d.hasGames && !!d.counterText;
  show(counter, hasCounter);
  if (hasCounter) counter.textContent = d.counterText;

  // Status line summary.
  const parts = [];
  const total = Number(d.totalCount) || 0;
  parts.push(total === 1 ? '1 game' : `${total} games`);
  const ev = Array.isArray(d.evidence) ? d.evidence.length : 0;
  if (ev > 0) parts.push(ev === 1 ? '1 evidence item' : `${ev} evidence items`);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: evidence ledger ─────────────────────────────────────────────────
function renderEvidence(d) {
  const items = Array.isArray(d.evidence) ? d.evidence : [];
  const host = $('ev-list');
  clear(host);
  show($('ledger'), !!d.hasEvidence && items.length > 0);
  $('ledger-sum').textContent = d.evidenceSummary || '';
  if (items.length === 0) return;

  for (const e of items) {
    const el = tpl('tpl-ev');
    if (e.polarityColorHex) el.style.setProperty('--ev', e.polarityColorHex);
    el.querySelector('.og-ev-title').textContent = e.title || '';
    el.querySelector('.og-ev-meta').textContent = e.metaText || '';
    const note = el.querySelector('.og-ev-note');
    show(note, !!e.hasDisplayNote && !!e.displayNote);
    if (e.hasDisplayNote && e.displayNote) note.textContent = e.displayNote;
    el.querySelector('.og-ev-chip').textContent = e.polarityLabel || 'Neutral';
    host.appendChild(el);
  }
}

// ── render: one game row ────────────────────────────────────────────────────
function buildGame(g) {
  const el = tpl('tpl-game');
  if (g.gameId != null) el.dataset.gameId = String(g.gameId);
  if (g.resultColorHex) el.style.setProperty('--wl', g.resultColorHex);

  const wl = el.querySelector('.og-grow-wl');
  wl.textContent = g.resultText || '';
  if (g.resultColorHex) wl.style.color = g.resultColorHex;

  el.querySelector('.og-grow-champ').textContent = g.championName || '';
  el.querySelector('.og-grow-kda').textContent = g.kdaText || '';
  el.querySelector('.og-grow-date').textContent = g.dateText || '';

  const badge = el.querySelector('.og-grow-practiced');
  badge.textContent = g.practicedText || '';
  if (g.practicedColorHex) badge.style.setProperty('--fg', g.practicedColorHex);
  if (g.practicedDimColorHex) badge.style.setProperty('--bg', g.practicedDimColorHex);

  const exec = el.querySelector('.og-grow-exec');
  show(exec, !!g.hasExecutionNote && !!g.executionNote);
  if (g.hasExecutionNote && g.executionNote) exec.textContent = g.executionNote;

  // Watch VOD only renders when a recording actually exists on disk — the
  // button used to show on every row and dead-end on games with no VOD.
  const vodBtn = el.querySelector('.og-act-vod');
  if (vodBtn && g.hasVod !== true) vodBtn.remove();

  return el;
}

// ── render: games list ──────────────────────────────────────────────────────
function renderGames(d) {
  const items = Array.isArray(d.games) ? d.games : [];
  const host = $('games-list');
  clear(host);
  show($('games-label'), !!d.hasGames && items.length > 0);
  const sub = $('games-sub');
  if (sub) sub.textContent = items.length ? `${items.length}` : '';
  for (const g of items) host.appendChild(buildGame(g));
}

// ── render: empty state ─────────────────────────────────────────────────────
function renderEmpty(d) {
  show($('empty'), !d.hasActivity);
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
  const order = [$('ledger'), $('games-list')].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderEvidence(d);
  renderGames(d);
  renderEmpty(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function load() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchObjectiveGames();
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[objectivegames] load failed:', err);
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
// back         = pop history (returns to objectives page).
// watch_vod    = open the VOD player for that game, scoped to this objective.
// review_game  = open the full review page for that game.
const ACTIONS = new Set(['back', 'watch_vod', 'review_game']);

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

  if (action === 'watch_vod') {
    // Open the VOD focused on just this objective (objectiveId scopes the
    // viewer's tag/objective picker downstream).
    const oid = objectiveId();
    const focus = oid > 0 ? `&objectiveId=${encodeURIComponent(oid)}` : '';
    window.location.href = `vodplayer.html?gameId=${encodeURIComponent(gid)}${focus}`;
  } else if (action === 'review_game') {
    window.location.href = `review.html?gameId=${encodeURIComponent(gid)}`;
  }
});

// Keyboard activation for the role="button" game rows (Enter / Space). The
// nested Watch VOD <button> handles its own keys natively (it isn't role=button),
// so a focused VOD button won't also fire the row's review_game.
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', load);
} else {
  load();
}
