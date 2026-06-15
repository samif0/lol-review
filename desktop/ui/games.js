// Revu desktop — Games (workspace) page renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_games`
// (see Revu.Sidecar GET /api/games). Mirrors app.js conventions exactly:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-games.json so the page previews in a
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
// (?page=N). There is no message bus — writes (deferred) would refetch manually.

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

// ── data fetch ──────────────────────────────────────────────────────────────
// Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
// bundled sample only when invoke is genuinely unavailable (browser preview).
async function fetchGames(view, page) {
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_games', { view, page });
  }
  const res = await fetch('./sample-games.json');
  if (!res.ok) throw new Error(`sample-games.json ${res.status}`);
  return res.json();
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

  if (k) k.textContent = `Workspace · ${heading}`;
  if (h) h.textContent = heading;

  const shown = _rows.length;

  if (_view === 'queue') {
    if (shown === 0) {
      if (cnt) cnt.textContent = 'every game reviewed. nice';
      if (bar) bar.classList.add('queue-clear');
    } else {
      if (cnt) cnt.textContent = `${shown} game${shown === 1 ? '' : 's'} waiting. clear the queue`;
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
// The WHOLE ROW is the primary action (open_review) — role=button + tabindex
// for keyboard, with the hype hover animation defined on .gamerow in styles.css.
// State tokens (reviewed / VOD / objective) replace the dashboard's plain meta.
function buildRow(g) {
  const el = tpl('tpl-gamerow');
  const wl = el.querySelector('.grow-wl');
  const champ = el.querySelector('.grow-champ');
  const meta = el.querySelector('.grow-meta');
  const tokens = el.querySelector('.grow-tokens');
  const kdaN = el.querySelector('.grow-kda-n');
  const kdaR = el.querySelector('.grow-kda-r');
  const cue = el.querySelector('.gamerow-cue');

  // W/L capsule + matchup name. "Champ vs Enemy" is the headline.
  wl.textContent = g.winLossText || '';
  if (g.winLossColorHex) {
    wl.style.color = g.winLossColorHex;
    wl.style.borderColor = g.winLossColorHex;
  }
  const enemy = g.enemyChampion ? ` vs ${g.enemyChampion}` : '';
  champ.textContent = `${g.championName || ''}${enemy}`;

  // Meta line: GAMEMODE · DATE · DURATION (vision is OUT, per spec).
  meta.textContent = g.metaLine || g.statsLine || '';

  // KDA in its own right-aligned column so the numbers read fast.
  kdaN.textContent = g.kdaText || '';
  kdaR.textContent = g.kdaRatioText || '';

  // State tokens — review / VOD / objective. Colored by meaning.
  const tokenSpecs = [
    { text: g.reviewStateText, tone: g.reviewStateText === 'Reviewed' ? 'good' : 'warn' },
    { text: g.vodStateText, tone: g.hasVod ? 'good' : 'muted' },
    { text: g.objectiveStateText, tone: tokenTone(g) },
  ];
  for (const spec of tokenSpecs) {
    if (!spec.text) continue;
    const t = tpl('tpl-token');
    t.textContent = spec.text;
    if (spec.tone) t.classList.add(spec.tone);
    tokens.appendChild(t);
  }

  // The whole row carries the gameId + a re-review/open/VOD cue label.
  if (g.gameId != null) el.dataset.gameId = String(g.gameId);
  if (g.action) el.dataset.action = g.action;
  if (cue) cue.firstChild.textContent = (g.primaryAction ? g.primaryAction.toUpperCase() : 'REVIEW') + ' ';

  // Left edge bar rests in the game's win/loss color, energizes to accent on hover.
  if (g.winLossColorHex) el.style.setProperty('--wl', g.winLossColorHex);

  return el;
}

function tokenTone(g) {
  if (g.hasObjectiveEvidence) return 'good';
  if (g.objectivePracticed) return 'warn';
  return 'muted';
}

// ── render: the game list ────────────────────────────────────────────────────
// Renders the accumulated _rows (History Load More appends; the others replace).
function renderList(d) {
  const host = $('games-list');
  clear(host);
  const items = _rows;

  const sub = $('games-sub');
  if (sub) sub.textContent = `showing ${items.length}`;

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
    case 'queue':   return 'Play a ranked or normal game and it lands here for review.';
    case 'today':   return 'Games you play today show up here for a same-day review.';
    case 'vod':     return 'Link a recording to a game and it appears in this view.';
    case 'history':
    default:        return 'Play a ranked or normal game and it lands here for review.';
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
  _view = view;
  _page = 0;
  try {
    const data = await fetchGames(_view, _page);
    // Trust the server's echoed view if present (keeps seg honest if it
    // coerced an unknown view to queue).
    if (data && typeof data.view === 'string' && VIEWS.includes(data.view)) {
      _view = data.view;
    }
    _rows = Array.isArray(data?.items) ? data.items.slice() : [];
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[games] load failed:', err);
  } finally {
    _loading = false;
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
    _rows = _rows.concat(more);
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
const ACTIONS = new Set(['view', 'open_review', 'load_more', 'manual_entry']);

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
