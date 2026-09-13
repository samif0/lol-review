import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';

// Revu desktop — Match library page renderer.
// Renders the JSON returned by the Electron command `get_games`
// (see Revu.Sidecar GET /api/games). Mirrors app.js conventions exactly:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron it fetches ./sample-games.json so the page previews in a
//     plain browser.
//   • Every server string is written via textContent (never innerHTML) so the
//     surface stays XSS-free; colors arrive as *Hex strings applied to style
//     properties only.
//   • ONE delegated [data-action] click handler.
//
// VIEWS: a 4-way SERVER-SIDE segmented control mirroring the WinUI GamesPage —
// Queue (unreviewed, 14d) / Today / History (paged) / VOD (on-disk recordings).
// Switching a view refetches from the backend (the server owns each view's data
// source); History additionally supports append-mode "Load More" paging
// (?page=N). Recording-link notifications update existing rows in place.

// ── small DOM helpers ───────────────────────────────────────────────────────

// ── module state ────────────────────────────────────────────────────────────
// _data    : the last loaded snapshot (server-owned per view).
// _view    : active view key — 'queue' | 'today' | 'history' | 'vod'.
// _page    : current History page (0-based); only History paginates.
// _rows    : accumulated rows across appended pages (History Load More).
const VIEWS = ['queue', 'today', 'history', 'vod'];
let _data = null;
let _view = 'queue';
let _page = 0;
let _rows = [];
const _linkedRecordingGames = new Set();
let _loadVersion = 0;
let _vodListRefreshRequested = false;
let _vodListRefreshing = false;

// ── data fetch ──────────────────────────────────────────────────────────────
// Prefer the REAL backend (Electron invoke → sidecar → your DB); fall back to the
// bundled sample only when invoke is genuinely unavailable (browser preview).
async function fetchGames(view, page) {
  return readSnapshot('get_games', 'sample-games.json', { view, page });
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const parts = [];
  parts.push(d.heading || 'Games');
  parts.push(d.countText || `${d.totalCount ?? _rows.length} games`);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: workspace command bar (heading + count nudge) ────────────────────
// The count nudge stays "urgent gold" for the review Queue (games waiting), and
// goes calm green when the queue is clear; for the other views it's a neutral
// count of what's on screen.
function renderQueueBar(d) {
  const k = $('queue-k');
  const h = $('queue-h');
  const cnt = $('queue-cnt-text');
  const bar = $('queue-bar');
  const heading = d.heading || 'Games';

  if (k) k.textContent = 'Your games';
  if (h) h.textContent = heading;

  const shown = _rows.length;

  if (_view === 'queue') {
    if (shown === 0) {
      if (cnt) cnt.textContent = 'No games waiting for review';
      if (bar) bar.classList.add('queue-clear');
    } else {
      if (cnt) cnt.textContent = `${shown} game${shown === 1 ? '' : 's'} ready to review`;
      if (bar) bar.classList.remove('queue-clear');
    }
    return;
  }

  // Non-queue views: calm count of what's loaded (History shows the all-time
  // total; the rest show what's on screen).
  if (bar) bar.classList.add('queue-clear');
  if (cnt) {
    if (_view === 'history') {
      const total = d.totalCount ?? shown;
      cnt.textContent = `${shown} of ${total} loaded`;
    } else if (shown === 0) {
      cnt.textContent = 'nothing here yet';
    } else {
      cnt.textContent = `${shown} game${shown === 1 ? '' : 's'}`;
    }
  }
}

// ── render: stat strip (at-a-glance over the rows currently loaded) ──────────
function renderStrip(d) {
  const items = _rows;
  const strip = $('strip');
  clear(strip);

  const wins = items.filter((g) => g.win === true).length;
  const reviewed = items.filter((g) => g.reviewStateText === 'Reviewed' || g.hasReview === true).length;
  const withVod = items.filter((g) => g.hasVod === true).length;
  const wr = items.length ? Math.round((wins / items.length) * 100) : 0;

  // The first cell reflects the view: History = all-time total; others = loaded.
  const totalLabel = _view === 'history' ? 'IN HISTORY' : 'LOADED';
  const totalVal = _view === 'history'
    ? String(d.totalCount ?? items.length)
    : String(items.length);

  const cells = [
    { k: 'Total',    v: totalVal,                         sub: totalLabel,  flag: false },
    { k: 'Win Rate', v: items.length ? `${wr}%` : '—',    sub: 'LOADED',    flag: false },
    { k: 'Reviewed', v: `${reviewed}/${items.length}`,    sub: 'LOADED',    flag: false },
    { k: 'VOD',      v: String(withVod),                  sub: 'LINKED',    flag: true  },
  ];

  for (const c of cells) {
    const el = tpl('tpl-stat');
    if (c.flag) el.classList.add('flag');
    el.querySelector('.k').textContent = c.k;
    el.querySelector('.v').textContent = c.v;
    el.querySelector('.s').textContent = c.sub;
    strip.appendChild(el);
  }
}

// ── render: one game row ────────────────────────────────────────────────────
// The whole row retains its server-selected primary action and keyboard behavior.
// Plain status text adds only confirmed review/recording/objective information.
function buildRow(g) {
  const el = tpl('tpl-gamerow');
  const wl = el.querySelector('.grow-wl');
  const champ = el.querySelector('.grow-champ');
  const meta = el.querySelector('.grow-meta');
  const kdaN = el.querySelector('.grow-kda-n');
  const kdaR = el.querySelector('.grow-kda-r');

  const win = knownBoolean(g.win, g.winLossText, ['w', 'win', 'victory'], ['l', 'loss', 'defeat']);
  wl.textContent = win === true ? 'W' : win === false ? 'L' : g.winLossText || '—';
  wl.title = win === true ? 'Win' : win === false ? 'Loss' : '';
  if (wl.title) wl.setAttribute('aria-label', wl.title);
  el.dataset.result = wl.dataset.result = win === true ? 'win' : win === false ? 'loss' : 'unknown';
  const enemy = g.enemyChampion ? ` vs ${g.enemyChampion}` : '';
  champ.textContent = g.championName ? `${g.championName}${enemy}` : 'Match';

  meta.textContent = gameMetadata(g);

  // KDA in its own right-aligned column so the numbers read fast.
  const numericKda = [g.kills, g.deaths, g.assists];
  const textKda = String(g.kdaText || '').match(/^\s*(\d+)\s*\/\s*(\d+)\s*\/\s*(\d+)\s*$/);
  kdaN.textContent = numericKda.every(n => Number.isSafeInteger(n) && n >= 0)
    ? numericKda.join(' / ') : textKda ? textKda.slice(1).join(' / ') : g.kdaText || '—';
  const ratioText = String(g.kdaRatioText || '').trim();
  const parsedRatio = ratioText.match(/^\(?\s*(\d+(?:\.\d+)?)\s*\)?\s*(?:KDA)?$/i);
  const ratio = typeof g.kdaRatio === 'number' ? g.kdaRatio : parsedRatio ? Number(parsedRatio[1]) : null;
  kdaR.textContent = Number.isFinite(ratio) && ratio >= 0 ? `${Number(ratio.toFixed(2))} KDA` : ratioText;

  updateRowRecording(el, g);
  return el;
}

function updateRowRecording(el, g) {
  const tokens = el.querySelector('.grow-tokens');
  const cue = el.querySelector('.gamerow-cue');
  clear(tokens);
  for (const spec of gameStatuses(g)) {
    const t = tpl('tpl-token');
    t.textContent = spec.text;
    t.dataset.status = spec.kind;
    if (spec.title) t.title = spec.title;
    tokens.appendChild(t);
  }

  // The whole row carries the gameId + a re-review/open/VOD cue label.
  if (g.gameId != null) el.dataset.gameId = String(g.gameId);
  if (g.action) el.dataset.action = g.action;
  const reviewed = reviewedState(g);
  const cueText = el.dataset.action === 'watch_vod' ? 'Watch VOD'
    : el.dataset.action === 'open_review' ? (reviewed === true || String(g.primaryAction || '').toLowerCase() === 'open' ? 'Open' : 'Review')
    : 'Open';
  if (cue) cue.firstChild.textContent = `${cueText} `;

}

function withLinkedRecording(game) {
  return _linkedRecordingGames.has(Number(game.gameId))
    ? { ...game, hasVod: true, action: 'watch_vod', primaryAction: 'Watch VOD' } : game;
}

window.addEventListener('revu:vod-linked', event => {
  const gameId = Number(event.detail?.gameId);
  if (!Number.isSafeInteger(gameId) || gameId <= 0) return;
  _linkedRecordingGames.add(gameId);
  if (_linkedRecordingGames.size > 128) _linkedRecordingGames.delete(_linkedRecordingGames.values().next().value);
  const index = _rows.findIndex(game => Number(game.gameId) === gameId);
  if (index >= 0) {
    const game = _rows[index] = withLinkedRecording(_rows[index]);
    const row = [...$('games-list').children].find(element => Number(element.dataset.gameId) === gameId);
    if (row) updateRowRecording(row, game);
    if (_data) renderStrip(_data);
  } else if (_view === 'vod') {
    _vodListRefreshRequested = true;
    return refreshLinkedVodList();
  }
});

// VOD is not paginated. A newly linked match that is absent from that filter
// needs a fresh list; other filters keep their mounted rows and History pages.
async function refreshLinkedVodList() {
  if (_loading || _vodListRefreshing || !_vodListRefreshRequested || _view !== 'vod') return;
  _vodListRefreshing = true;
  try {
    while (_vodListRefreshRequested && !_loading && _view === 'vod') {
      _vodListRefreshRequested = false;
      const version = _loadVersion;
      const data = await fetchGames('vod', 0);
      if (_loading || _view !== 'vod' || version !== _loadVersion) {
        _vodListRefreshRequested = _view === 'vod';
        break;
      }
      const left = window.scrollX, top = window.scrollY;
      const focused = document.activeElement?.closest?.('[data-game-id]')?.dataset.gameId;
      _rows = (Array.isArray(data?.items) ? data.items : []).map(withLinkedRecording);
      render(data);
      if (focused) [...$('games-list').children].find(row => row.dataset.gameId === focused)?.focus({ preventScroll: true });
      window.scrollTo({ left, top, behavior: 'instant' });
    }
  } catch (error) {
    console.error('[games] linked recording refresh failed:', error);
  } finally {
    _vodListRefreshing = false;
  }
}

function knownBoolean(value, label, yes, no) {
  if (typeof value === 'boolean') return value;
  const normalized = String(label || '').trim().toLowerCase();
  return yes.includes(normalized) ? true : no.includes(normalized) ? false : null;
}

function reviewedState(g) {
  return knownBoolean(g.hasReview, g.reviewStateText, ['reviewed'], ['unreviewed', 'to review']);
}

function gameStatuses(g) {
  const states = [];
  const reviewed = reviewedState(g);
  if (reviewed !== null) states.push({ kind: 'review', text: reviewed ? 'Reviewed' : 'To review' });
  const recorded = knownBoolean(g.hasVod, g.vodStateText,
    ['vod linked', 'vod linked - no notes', 'recording', 'recording with notes'], ['no vod', 'no recording']);
  if (recorded === true) {
    // HasNotes means timestamped bookmarks, not the separate written Review.
    const notes = knownBoolean(g.hasNotes, g.vodStateText, ['recording with notes'], ['vod linked - no notes']);
    states.push({ kind: 'recording', text: notes === true ? 'Recording with notes' : 'Recording',
      title: notes === true ? 'A linked recording was available at the last check and has timestamped bookmarks.'
        : notes === false ? 'A linked recording was available at the last check. No timestamped bookmarks yet.'
          : 'A linked recording was available at the last check.' });
  } else if (recorded === false) {
    states.push({ kind: 'recording', text: 'No recording', title: 'No linked recording was available at the last check.' });
  }
  const evidence = knownBoolean(g.hasObjectiveEvidence, g.objectiveStateText, ['evidence tagged'], []);
  const practiced = knownBoolean(g.objectivePracticed, g.objectiveStateText, ['objective practiced', 'vod evidence pending'], []);
  if (evidence === true) states.push({ kind: 'objective', text: 'Evidence tagged' });
  else if (practiced === true) states.push({ kind: 'objective', text: 'Objective practiced' });
  return states;
}

function gameMetadata(g) {
  const modes = { 'ranked solo/duo': 'Ranked solo/duo', 'ranked flex': 'Ranked flex',
    'normal draft': 'Normal draft', 'normal blind': 'Normal blind', quickplay: 'Quickplay', aram: 'ARAM', arena: 'Arena' };
  const readable = value => {
    const text = typeof value === 'string' ? value.trim() : '';
    return modes[text.toLowerCase()] || text.replace(/\b(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\b/g,
      month => month[0] + month.slice(1).toLowerCase());
  };
  const parts = [g.gameMode, g.datePlayed, g.duration].map(readable).filter(Boolean);
  if (parts.length) return parts.join(' · ');
  return String(g.metaLine || g.statsLine || '').split(/\s*·\s*/).map(readable).join(' · ');
}

// ── render: the game list ────────────────────────────────────────────────────
// Renders the accumulated _rows (History Load More appends; the others replace).
function renderList(d) {
  const host = $('games-list');
  clear(host);
  const items = _rows;

  const sub = $('games-sub');
  if (sub) sub.textContent = `${items.length} shown`;

  if (items.length === 0) {
    show($('games-label'), false);
    show($('games-empty'), true);
    const emptyH = $('games-empty-h');
    const emptyP = $('games-empty-p');
    if (emptyH) emptyH.textContent = (d && d.emptyMessage) || 'No games recorded yet.';
    if (emptyP) emptyP.textContent = emptyHint(_view);
    show($('loadwrap'), false);
    return;
  }

  show($('games-empty'), false);
  show($('games-label'), true);
  for (const g of items) host.appendChild(buildRow(g));

  // Load More only on History when the server says there's another page.
  show($('loadwrap'), _view === 'history' && !!(d && d.hasMore));
}

function emptyHint(view) {
  switch (view) {
    case 'queue':   return 'New Ranked Solo/Duo games appear here while Revu is open. Find older games in History, or add one manually.';
    case 'today':   return 'Games you play today show up here for a same-day review.';
    case 'vod':     return 'Games with an available recording appear here. Connect your Ascent recordings folder in Settings → Recording, then scan to link existing videos.';
    case 'history':
    default:        return 'Keep Revu open during a Ranked Solo/Duo game, or use Add a game manually.';
  }
}

// ── render: active view segment ──────────────────────────────────────────────
function renderSeg() {
  const seg = $('seg');
  if (!seg) return;
  for (const btn of seg.querySelectorAll('button')) {
    const on = btn.dataset.view === _view;
    btn.classList.toggle('on', on);
    btn.setAttribute('aria-selected', on ? 'true' : 'false');
  }
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on first load ─────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('queue-bar'),
    $('strip'),
    $('games-list'),
    $('loadwrap'),
  ].filter(Boolean);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  _data = d;
  clearError();
  renderHeader(d);
  renderQueueBar(d);
  renderStrip(d);
  renderSeg();
  renderList(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
// loadView(view): switch to a view — resets page 0, REPLACES rows.
// loadMore():     History only — increments page, APPENDS rows.
let _loading = false;

async function loadView(view) {
  if (_loading) return;
  if (!VIEWS.includes(view)) view = 'queue';
  _loading = true;
  ++_loadVersion;
  const prevView = _view;
  _view = view;
  _page = 0;
  try {
    const data = await fetchGames(_view, _page);
    // Trust the server's echoed view if present (keeps seg honest if it
    // coerced an unknown view to queue).
    if (data && typeof data.view === 'string' && VIEWS.includes(data.view)) {
      _view = data.view;
    }
    _rows = (Array.isArray(data?.items) ? data.items : []).map(withLinkedRecording);
    render(data);
  } catch (err) {
    // The switch failed — roll _view back to the view whose rows are still on
    // screen and re-highlight its tab, so the seg, stat strip and list agree
    // under the error panel instead of asserting a view that never loaded.
    _view = prevView;
    renderSeg();
    renderError(err);
    console.error('[games] load failed:', err);
  } finally {
    _loading = false;
    if (_view === 'vod') void refreshLinkedVodList();
  }
}

async function loadMore() {
  if (_loading || _view !== 'history') return;
  if (!_data || !_data.hasMore) return;
  _loading = true;
  const btn = $('loadmore');
  if (btn) btn.disabled = true;
  try {
    const next = _page + 1;
    const data = await fetchGames('history', next);
    const more = Array.isArray(data?.items) ? data.items : [];
    _page = next;
    _rows = _rows.concat(more.map(withLinkedRecording));
    render(data); // re-renders the accumulated _rows + updated hasMore/page.
  } catch (err) {
    renderError(err);
    console.error('[games] load more failed:', err);
  } finally {
    if (btn) btn.disabled = false;
    _loading = false;
  }
}

// ── single delegated action handler ─────────────────────────────────────────
// view        = the segmented control (server-side view switch, page reset).
// open_review = clicking a whole game row (→ review page).
// load_more   = History pagination (append next page).
const ACTIONS = new Set(['view', 'open_review', 'watch_vod', 'load_more', 'manual_entry']);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Switch the active view — server-side fetch of that view's data source.
  if (action === 'view') {
    const v = target.dataset.view || 'queue';
    if (v !== _view) await loadView(v);
    return;
  }

  // Clicking a game row opens THAT game's review page.
  if (action === 'open_review') {
    const gid = target.dataset.gameId;
    window.location.href = gid ? `review.html?gameId=${encodeURIComponent(gid)}` : 'review.html';
    return;
  }

  // A row whose cue reads "WATCH VOD →" opens the VOD player — the cue and the
  // click must agree (rows without a recording keep open_review).
  if (action === 'watch_vod') {
    const gid = target.dataset.gameId;
    window.location.href = gid ? `vodplayer.html?gameId=${encodeURIComponent(gid)}` : 'games.html';
    return;
  }

  // History pagination — append the next page.
  if (action === 'load_more') {
    await loadMore();
    return;
  }

  // Manual Entry — log a game by hand (moved here from the nav rail).
  if (action === 'manual_entry') {
    window.location.href = 'manualentry.html';
    return;
  }
});

// Keyboard activation for the role="button" game rows (Enter / Space).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// ── boot ────────────────────────────────────────────────────────────────────
function boot() { loadView('queue'); }
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
