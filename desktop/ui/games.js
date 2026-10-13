import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke } from './platform/index.mjs';

// Revu desktop: Match library page renderer.
// Renders the JSON returned by the Electron command `get_games`
// (see Revu.Sidecar GET /api/games). Mirrors app.js conventions exactly:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron it fetches ./sample-games.json (./sample-games-queue.json for
//     To review) so the page previews in a plain browser.
//   • Every server string is written via textContent (never innerHTML), and
//     server colors are ignored: results and states read theme tokens in CSS.
//   • ONE delegated [data-action] click handler.
//
// VIEWS: server-side filters. All = History (paged), To review = Queue
// (unreviewed, 14 days), With recordings = VOD, Removed (each with Restore).
// Today stays callable (?view=today) and shows its own pill while selected.
// The page opens on ?view= when given, otherwise on To review. Switching a view
// refetches from the backend; History additionally supports "Load more" paging
// (?page=N). Search filters the loaded rows by champion or enemy, in the page.
// Rows are grouped by day. Recording-link notifications update rows in place.

// ── module state ────────────────────────────────────────────────────────────
// _data    : the last loaded snapshot (server-owned per view).
// _view    : active view key: 'queue' | 'today' | 'history' | 'vod' | 'removed'.
// _page    : current History page (0-based); only History paginates.
// _rows    : accumulated rows across appended pages (History Load more).
const VIEWS = ['queue', 'today', 'history', 'vod', 'removed'];
const DEFAULT_VIEW = 'queue';
let _data = null;
let _view = DEFAULT_VIEW;
let _page = 0;
let _rows = [];
const _linkedRecordingGames = new Set();
let _loadVersion = 0;
let _vodListRefreshRequested = false;
let _vodListRefreshing = false;
// Game ids in the last loaded To review list (the pill's count), or null while unknown.
let _queueIds = null;
let _now = () => new Date();

// ── data fetch ──────────────────────────────────────────────────────────────
// Prefer the REAL backend (Electron invoke → sidecar → your DB); fall back to the
// bundled sample only when invoke is genuinely unavailable (browser preview). The
// To review sample is its own queue snapshot, so the preview opens on To review too.
async function fetchGames(view, page) {
  return readSnapshot('get_games', view === 'queue' ? 'sample-games-queue.json' : 'sample-games.json', { view, page });
}

// games.html?view=queue|history|vod|removed|today opens that filter ("all" is History).
function viewFromSearch(search) {
  const match = /[?&]view=([^&#]*)/.exec(String(search || ''));
  let value = '';
  try { value = match ? decodeURIComponent(match[1]).trim().toLowerCase() : ''; } catch (_) { value = ''; }
  if (value === 'all') value = 'history';
  return VIEWS.includes(value) ? value : DEFAULT_VIEW;
}

// ── render: header status line ──────────────────────────────────────────────
// Only says "Loading" until the first answer; after that the filters and the
// summary carry the view, so the line would just repeat them.
function renderHeader() {
  show($('statusline'), false);
}

// ── render: filter pills ────────────────────────────────────────────────────
// The To review count is the last loaded queue, kept current while games are
// removed or brought back here; it hides when this page can no longer know it.
function syncQueue() {
  if (_view === 'queue' && _data && !_data.errorText) _queueIds = new Set(_rows.map((g) => String(g.gameId)));
}

function renderSeg() {
  const seg = $('seg');
  if (!seg) return;
  for (const btn of seg.querySelectorAll('button')) {
    const on = btn.dataset.view === _view;
    btn.classList.toggle('is-on', on);
    btn.classList.toggle('on', on);
    btn.setAttribute('aria-selected', on ? 'true' : 'false');
    if (btn.dataset.view === 'today') show(btn, on);
  }
  const count = $('queue-count');
  const n = _queueIds ? _queueIds.size : 0;
  if (count) { count.textContent = n ? String(n) : ''; show(count, n > 0); }
}

// ── render: summary (the whole selected view, from the server) ──────────────
// The sidecar counts the entire view (History: every visible game, not just
// the pages loaded), and a remake (League early surrender) is listed but never
// counted, like every other count.
const REMAKE_TITLE = 'Ended early. Does not count toward your rules, stats or review queue.';
const isRemake = (g) => g?.isRemake === true;
const SUMMARY_SCOPE = { queue: 'Last 14 days', today: 'Today' };

function renderStrip(d) {
  const strip = $('strip');
  clear(strip);
  const totals = d?.totals;
  show(strip, !!totals);
  if (!totals) return;

  const games = Math.max(0, Number(totals.games) || 0);
  const wins = Math.max(0, Number(totals.wins) || 0);
  const reviewed = Math.max(0, Number(totals.reviewed) || 0);
  const parts = [];
  if (SUMMARY_SCOPE[_view]) parts.push({ v: '', k: SUMMARY_SCOPE[_view] });
  parts.push({ v: String(games), k: games === 1 ? 'game' : 'games' });
  if (games) parts.push({ v: `${Math.round((wins / games) * 100)}%`, k: 'won' });
  // Every game waiting for review is unreviewed by definition.
  if (_view !== 'queue') parts.push({ v: String(reviewed), k: 'reviewed' });

  for (const part of parts) {
    const el = tpl('tpl-stat');
    el.querySelector('.v').textContent = part.v;
    el.querySelector('.k').textContent = part.k;
    strip.appendChild(el);
  }
}

// ── days: group rows under Today, Yesterday or the date played ───────────────
// A row's day is its local date when the server sends a timestamp, otherwise the
// date part of datePlayed ("Jun 14, 12:32"). Without a timestamp that text has
// no year, so Today and Yesterday are said only when they can be proven: the
// date matches the clock and, in the long views (newest first, years deep), the
// group is the newest one. To review (14 days) and Today cannot repeat a date.
const DAY_FORMAT = new Intl.DateTimeFormat('en-US', { month: 'short', day: '2-digit' });
const titleMonths = (text) => text.replace(/\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)\b/gi,
  (month) => month[0].toUpperCase() + month.slice(1).toLowerCase());

function playedParts(g) {
  const text = typeof g?.datePlayed === 'string' ? g.datePlayed.trim() : '';
  const at = text.indexOf(', ');
  return at > 0 ? { day: text.slice(0, at), time: text.slice(at + 2).trim() } : { day: text, time: '' };
}

function stampOf(g) {
  const value = Number(g?.timestamp);
  if (g?.timestamp == null || !Number.isFinite(value) || value <= 0) return null;
  const date = new Date(value < 1e11 ? value * 1000 : value);
  return Number.isNaN(date.getTime()) ? null : date;
}

const startOfDay = (date, offset = 0) => new Date(date.getFullYear(), date.getMonth(), date.getDate() + offset);

function dayGroups(rows, { now = new Date(), view = '' } = {}) {
  const groups = [];
  for (const row of rows) {
    const stamp = stampOf(row);
    const day = stamp ? startOfDay(stamp) : null;
    const raw = stamp ? '' : playedParts(row).day;
    const key = day ? `d:${day.getFullYear()}-${day.getMonth() + 1}-${day.getDate()}` : raw.toLowerCase();
    const last = groups[groups.length - 1];
    if (last && last.key === key) last.rows.push(row);
    else groups.push({ key, day, raw: titleMonths(raw), label: '', rows: [row] });
  }
  const today = startOfDay(now), yesterday = startOfDay(now, -1);
  const todayKey = DAY_FORMAT.format(today).toLowerCase(), yesterdayKey = DAY_FORMAT.format(yesterday).toLowerCase();
  const anywhere = view === 'queue' || view === 'today';
  groups.forEach((group, index) => {
    if (group.day) {
      const at = group.day.getTime();
      group.label = at === today.getTime() ? 'Today' : at === yesterday.getTime() ? 'Yesterday'
        : DAY_FORMAT.format(group.day) + (group.day.getFullYear() !== today.getFullYear() ? `, ${group.day.getFullYear()}` : '');
      return;
    }
    const newest = index === 0, afterToday = index === 1 && groups[0].label === 'Today';
    if (group.key && group.key === todayKey && (anywhere || newest)) group.label = 'Today';
    else if (group.key && group.key === yesterdayKey && (anywhere || newest || afterToday)) group.label = 'Yesterday';
    else group.label = group.raw;
  });
  return groups;
}

// ── search: champion or enemy, across the rows this page has loaded ─────────
// Case, accents and punctuation are ignored (kaisa finds Kai'Sa); every word
// must match one of the two names, and "vs" is ignored.
const fold = (text) => String(text ?? '').normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
const compact = (text) => fold(text).replace(/[^\p{L}\p{N}]/gu, '');

function searchTerms(value) {
  return fold(value).split(/\s+/).map(compact).filter((term) => term && term !== 'vs');
}

function matchesSearch(g, terms) {
  if (!terms.length) return true;
  const names = [g.championName, g.enemyChampion].map(compact);
  return terms.every((term) => names.some((name) => name.includes(term)));
}

// ── render: one game row ────────────────────────────────────────────────────
// The whole row retains its server-selected primary action and keyboard behavior.
// The status column shows at most one tag (Remake or To review) and small marks
// for a recording and objective evidence; every status stays in the row's text.
function initials(name) {
  const letters = String(name || '').replace(/[^\p{L}\p{N}]/gu, '');
  return letters ? letters[0].toUpperCase() + (letters[1] || '').toLowerCase() : '';
}

function buildRow(g) {
  const el = tpl('tpl-gamerow');
  const wl = el.querySelector('.grow-wl');
  const name = el.querySelector('.lib-name');
  const kdaN = el.querySelector('.lib-kda-n');
  const kdaR = el.querySelector('.lib-kda-r');

  // A remake is checked before the stored result: its win flag is not a result.
  const remake = isRemake(g);
  const win = remake ? null : knownBoolean(g.win, g.winLossText, ['w', 'win', 'victory'], ['l', 'loss', 'defeat']);
  wl.textContent = win === true ? 'W' : win === false ? 'L' : '';
  wl.title = remake ? REMAKE_TITLE : win === true ? 'Win' : win === false ? 'Loss' : '';
  if (wl.title) wl.setAttribute('aria-label', remake ? 'Remake' : wl.title);
  el.dataset.result = wl.dataset.result = remake ? 'remake' : win === true ? 'win' : win === false ? 'loss' : 'unknown';
  name.textContent = matchName(g, 'Match');
  const champ = el.querySelector('.lib-champ');
  if (champ) champ.textContent = initials(g.championName);
  el.querySelector('.lib-meta').textContent = gameMetadata(g);

  // KDA in its own right-aligned column so the numbers read fast.
  const numericKda = [g.kills, g.deaths, g.assists];
  const textKda = String(g.kdaText || '').match(/^\s*(\d+)\s*\/\s*(\d+)\s*\/\s*(\d+)\s*$/);
  kdaN.textContent = numericKda.every(n => Number.isSafeInteger(n) && n >= 0)
    ? numericKda.join(' / ') : textKda ? textKda.slice(1).join(' / ') : g.kdaText || '';
  const ratioText = String(g.kdaRatioText || '').trim();
  const parsedRatio = ratioText.match(/^\(?\s*(\d+(?:\.\d+)?)\s*\)?\s*(?:KDA)?$/i);
  const ratio = typeof g.kdaRatio === 'number' ? g.kdaRatio : parsedRatio ? Number(parsedRatio[1]) : null;
  kdaR.textContent = Number.isFinite(ratio) && ratio >= 0 ? `${Number(ratio.toFixed(2))} KDA` : ratioText;

  // Journal: the takeaway written in this game's review, when the server sends it.
  const take = el.querySelector('.lib-take');
  const notes = typeof g.reviewNotes === 'string' ? g.reviewNotes.trim() : '';
  if (take) { take.textContent = notes; take.title = notes; show(take, !!notes); }

  // History rows offer Remove from history; the Removed view offers Restore.
  const remove = el.querySelector('.lib-remove');
  if (remove) {
    remove.dataset.gameId = String(g.gameId ?? '');
    if (_view === 'removed') {
      remove.dataset.action = 'restore_game';
      remove.textContent = 'Restore';
      remove.title = '';
      remove.classList.remove('ui-iconbtn');
      remove.classList.add('ui-btn', 'ui-btn-quiet', 'is-restore');
      remove.setAttribute('aria-label', `Restore ${name.textContent} to history`);
    } else {
      remove.setAttribute('aria-label', `Remove ${name.textContent} from history`);
    }
  }
  updateRowRecording(el, g);
  return el;
}

function updateRowRecording(el, g) {
  const cell = el.querySelector('.lib-st');
  const cue = el.querySelector('.lib-go');
  const statuses = gameStatuses(g);
  clear(cell);
  const tag = statuses.find((s) => s.kind === 'remake') || statuses.find((s) => s.kind === 'review' && s.text === 'To review');
  if (tag) {
    const t = tpl('tpl-tag');
    t.textContent = tag.text;
    t.dataset.status = tag.kind;
    if (tag.kind === 'review') t.classList.add('is-pending');
    if (tag.title) t.title = tag.title;
    cell.appendChild(t);
  }
  for (const [kind, template] of [['recording', 'tpl-mark-video'], ['objective', 'tpl-mark-objective']]) {
    const spec = statuses.find((s) => s.kind === kind && s.text !== 'No recording');
    if (!spec) continue;
    const mark = tpl(template);
    mark.dataset.status = kind;
    mark.title = spec.title || spec.text;
    cell.appendChild(mark);
  }
  const label = tpl('tpl-sr');
  label.textContent = statuses.map((s) => s.text).join(', ');
  cell.appendChild(label);

  // The whole row carries the gameId + a re-review/open/VOD cue label.
  if (g.gameId != null) el.dataset.gameId = String(g.gameId);
  if (g.action) el.dataset.action = g.action;
  const reviewed = reviewedState(g);
  if (cue) cue.textContent = el.dataset.action === 'watch_vod' ? 'Watch'
    : el.dataset.action === 'open_review' ? (reviewed === true || String(g.primaryAction || '').toLowerCase() === 'open' ? 'Open' : 'Review')
      : 'Open';
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

// A remake verdict changed (startup check, Match-V5, or the Review toggle): the
// server owns labels and the queue, so refetch the current view. The queue may
// have changed too, so its count waits for the next queue load.
window.addEventListener('revu:remake-updated', () => {
  if (_view !== 'queue') { _queueIds = null; renderSeg(); }
  if (!_loading) void loadView(_view);
});

// Recovery added games while the library is open. Refetch the current view in place
// instead of reloading the page, so a typed search and its filtered list survive.
window.addEventListener('revu:games-recovered', () => {
  if (_view !== 'queue') { _queueIds = null; renderSeg(); }
  if (!_loading) void loadView(_view);
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

// ── Remove from history (soft hide with Undo) and Restore ──────────────────
// set_game_hidden keeps the game and everything tied to it; a hidden game
// leaves every list, count, streak and the review queue. The list and summary
// update in place (History pages and scroll stay put), and Undo restores it.
// Later, the Removed view lists it again with Restore. A failed write is told
// in the toast, never as the page's "Couldn't load your games" panel.
const UNDO_MS = 10_000;
let _undo = null;       // { game, index, view, inQueue } for the latest removal while Undo is offered
let _toastTimer = 0;
let _shiftedRows = 0;   // History rows removed since the last page fetch

function adjustTotals(game, sign) {
  if (!_data) return;
  _data.totalCount = Math.max(0, (Number(_data.totalCount) || 0) + sign);
  const totals = _data.totals;
  if (!totals || isRemake(game)) return;
  totals.games = Math.max(0, totals.games + sign);
  if (game.win === true) totals.wins = Math.max(0, totals.wins + sign);
  if (reviewedState(game) === true) totals.reviewed = Math.max(0, totals.reviewed + sign);
}

function renderRows() {
  syncQueue();
  renderSeg();
  renderStrip(_data);
  renderList(_data);
}

// The sidecar's sentence, without the Electron IPC prefix.
function errText(err) {
  const s = (err && err.message) ? err.message : String(err);
  return s.replace(/^Error invoking remote method '[^']+':\s*/, '').replace(/^(?:\w*Error:\s*)+/, '');
}

function matchName(game, fallback = 'The match') {
  return game.championName ? `${game.championName}${game.enemyChampion ? ` vs ${game.enemyChampion}` : ''}` : fallback;
}

function showToast(text, undo = null) {
  clearTimeout(_toastTimer);
  _undo = undo;
  $('games-undo-text').textContent = text;
  $('games-undo-btn').disabled = false;
  show($('games-undo-btn'), !!undo);
  show($('games-undo'), true);
  _toastTimer = setTimeout(dismissUndo, UNDO_MS);
}

function dismissUndo() {
  clearTimeout(_toastTimer);
  _undo = null;
  show($('games-undo'), false);
}

// The match rows, without the day headings between them ([data-game-id]).
function rowElements() {
  return [...$('games-list').children].filter((el) => el.dataset && 'gameId' in el.dataset);
}

// Rebuilding the list drops the focused button: keep keyboard focus in the
// list, on the row that took its place (or Undo when the list is now empty).
function focusAfterRemoval(index) {
  const rows = rowElements();
  const pill = [...($('seg')?.querySelectorAll('button') || [])].find((b) => b.getAttribute('aria-selected') === 'true');
  const target = rows.length ? rows[Math.min(Math.max(index, 0), rows.length - 1)]
    : _undo ? $('games-undo-btn') : pill;
  target?.focus?.({ preventScroll: true });
}

// The shell's sidebar (recent matches, review count) refreshes when a page changes the
// match list without loading a new page (shell-recent.mjs).
function announceGamesChanged() {
  try { window.top?.dispatchEvent(new CustomEvent('revu:games-changed')); } catch (_) { /* no shell */ }
}

async function setHidden(gameId, hidden) {
  const invoke = await getInvoke();
  if (!invoke) { console.info(`[games] (preview) set_game_hidden ${gameId} ${hidden}`); return false; }
  await invoke('set_game_hidden', { payload: { gameId: Number(gameId), hidden } });
  announceGamesChanged();
  return true;
}

async function hideGame(button) {
  const gameId = button.dataset.gameId;
  const index = _rows.findIndex((g) => String(g.gameId) === gameId);
  if (!gameId || index < 0 || _loading || button.disabled) return;
  const shownAt = rowElements().findIndex((row) => row.dataset.gameId === gameId);
  button.disabled = true;
  try {
    if (!await setHidden(gameId, true)) { button.disabled = false; return; }
  } catch (err) {
    button.disabled = false;
    showToast(`Couldn't remove this game. ${errText(err)}`);
    console.error('[games] remove from history failed:', err);
    return;
  }
  const [game] = _rows.splice(index, 1);
  if (_view === 'history') _shiftedRows++;
  // A removed game leaves To review too; Undo puts it back in the count.
  const inQueue = !!_queueIds?.delete(String(game.gameId));
  adjustTotals(game, -1);
  showToast(`${matchName(game)} was removed from history. You can restore it from Removed.`, { game, index, view: _view, inQueue });
  renderRows();
  focusAfterRemoval(shownAt);
}

async function undoHide() {
  const undo = _undo;
  if (!undo) return;
  $('games-undo-btn').disabled = true;
  try {
    if (!await setHidden(undo.game.gameId, false)) { $('games-undo-btn').disabled = false; return; }
  } catch (err) {
    showToast(`Couldn't bring ${matchName(undo.game)} back. ${errText(err)}`, undo);
    console.error('[games] undo remove from history failed:', err);
    return;
  }
  dismissUndo();
  if (undo.inQueue && _queueIds) _queueIds.add(String(undo.game.gameId));
  if (undo.view !== _view) { renderSeg(); if (!_loading) void loadView(_view); return; }
  if (_rows.some((g) => String(g.gameId) === String(undo.game.gameId))) return;
  _rows.splice(Math.min(undo.index, _rows.length), 0, undo.game);
  if (_view === 'history') _shiftedRows = Math.max(0, _shiftedRows - 1);
  adjustTotals(undo.game, 1);
  renderRows();
}

// Removed view: Restore puts the game back in every list, count and streak.
async function restoreGame(button) {
  const gameId = button.dataset.gameId;
  const index = _rows.findIndex((g) => String(g.gameId) === gameId);
  if (!gameId || index < 0 || _loading || button.disabled || _view !== 'removed') return;
  const shownAt = rowElements().findIndex((row) => row.dataset.gameId === gameId);
  button.disabled = true;
  try {
    if (!await setHidden(gameId, false)) { button.disabled = false; return; }
  } catch (err) {
    button.disabled = false;
    showToast(`Couldn't restore this game. ${errText(err)}`);
    console.error('[games] restore to history failed:', err);
    return;
  }
  const [game] = _rows.splice(index, 1);
  // A restored game may rejoin To review; the count waits for the next queue load.
  if (reviewedState(game) !== true && !isRemake(game)) _queueIds = null;
  adjustTotals(game, -1);
  showToast(`${matchName(game)} is back in your history.`);
  renderRows();
  focusAfterRemoval(shownAt);
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
  if (isRemake(g)) states.push({ kind: 'remake', text: 'Remake', title: REMAKE_TITLE });
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

// Mode · time · duration. The day heading above the row carries the date.
function gameMetadata(g) {
  // The official queue names, spelled as Home and the empty states spell them.
  const modes = { 'ranked solo/duo': 'Ranked Solo/Duo', 'ranked flex': 'Ranked Flex',
    'normal draft': 'Normal Draft', 'normal blind': 'Normal Blind', quickplay: 'Quickplay', aram: 'ARAM', arena: 'Arena' };
  const readable = value => {
    const text = typeof value === 'string' ? value.trim() : '';
    return modes[text.toLowerCase()] || text.replace(/\b(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\b/g,
      month => month[0] + month.slice(1).toLowerCase());
  };
  const stamp = stampOf(g);
  const pad = (n) => String(n).padStart(2, '0');
  const time = playedParts(g).time || (stamp && !g.datePlayed ? `${pad(stamp.getHours())}:${pad(stamp.getMinutes())}` : '');
  const parts = [g.gameMode, time, g.duration].map(readable).filter(Boolean);
  if (parts.length) return parts.join(' · ');
  return String(g.metaLine || g.statsLine || '').split(/\s*·\s*/).map(readable).join(' · ');
}

// ── render: the game list ────────────────────────────────────────────────────
// Renders the accumulated _rows (History Load more appends; the others replace),
// grouped by day, filtered by the search field.
function renderList(d) {
  const host = $('games-list');
  clear(host);
  host.dataset.view = _view;
  const terms = searchTerms($('lib-search')?.value);
  let shown = 0;
  for (const group of dayGroups(_rows, { now: _now(), view: _view })) {
    const rows = group.rows.filter((g) => matchesSearch(g, terms));
    if (!rows.length) continue;
    if (group.label) {
      const heading = tpl('tpl-day');
      heading.textContent = group.label;
      heading.dataset.day = group.key;
      host.appendChild(heading);
    }
    for (const g of rows) host.appendChild(buildRow(g));
    shown += rows.length;
  }

  // Every loaded History row was removed but later pages still hold games:
  // offer Load more, never "No games recorded yet".
  const morePages = _view === 'history' && !!(d && d.hasMore);
  renderSearchStatus(terms, shown, morePages);
  const count = $('lib-count');
  if (count) {
    count.textContent = `Showing ${_rows.length} of ${d?.totalCount ?? _rows.length}`;
    show(count, _view === 'history' && _rows.length > 0);
  }
  show($('loadwrap'), morePages);
  if (_rows.length === 0) {
    show($('lib-search-empty'), false);
    // A failed read shows the error panel, never "no games yet".
    show($('games-empty'), !d?.errorText && !morePages);
    const emptyH = $('games-empty-h');
    const emptyP = $('games-empty-p');
    if (emptyH) emptyH.textContent = (d && d.emptyMessage) || 'No games recorded yet.';
    if (emptyP) emptyP.textContent = emptyHint(_view);
    return;
  }
  show($('games-empty'), false);
  show($('lib-search-empty'), shown === 0);
}

function renderSearchStatus(terms, shown, morePages) {
  syncSearchClear();
  const status = $('lib-search-status');
  if (!status) return;
  show(status, terms.length > 0 && _rows.length > 0);
  if (!terms.length) { status.textContent = ''; return; }
  const loaded = _view === 'history' ? `${_rows.length} loaded` : `${_rows.length}`;
  status.textContent = (shown ? `${shown} of ${loaded} games match.` : `No match in ${loaded} games.`)
    + (morePages ? ' Load more to search older games.' : '');
}

function emptyHint(view) {
  switch (view) {
    case 'queue':   return 'New Ranked Solo/Duo games appear here while Revu is open. Find older games under All, or add one.';
    case 'today':   return 'Games you play today show up here for a same-day review.';
    case 'vod':     return 'Games with an available recording appear here. Enable built-in recording in Settings → Recording, or connect your Ascent folder and scan to link existing videos.';
    case 'removed': return 'Games you remove from history are listed here, so you can restore them.';
    case 'history':
    default:        return 'Keep Revu open during a Ranked Solo/Duo game, or use Add a game.';
  }
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
  show($('statusline'), false);
}
function clearError() { show($('errpanel'), false); }

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  _data = d;
  if (d?.errorText) renderError(new Error(d.errorText));
  else clearError();
  renderHeader();
  renderRows();
}

// ── load orchestration ──────────────────────────────────────────────────────
// loadView(view): switch to a view: resets page 0, REPLACES rows.
// loadMore():     History only: increments page, APPENDS rows.
let _loading = false;

async function loadView(view) {
  if (_loading) return;
  if (!VIEWS.includes(view)) view = DEFAULT_VIEW;
  _loading = true;
  ++_loadVersion;
  const prevView = _view;
  _view = view;
  _page = 0;
  _shiftedRows = 0;
  try {
    const data = await fetchGames(_view, _page);
    // Trust the server's echoed view if present (keeps the pills honest if it
    // coerced an unknown view to queue).
    if (data && typeof data.view === 'string' && VIEWS.includes(data.view)) {
      _view = data.view;
    }
    _rows = (Array.isArray(data?.items) ? data.items : []).map(withLinkedRecording);
    render(data);
  } catch (err) {
    // The switch failed: roll _view back to the view whose rows are still on
    // screen and re-highlight its pill, so the pills, summary and list agree
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
    // Rows removed from history move later games onto pages already loaded:
    // read again from the first page that now holds an unseen game.
    const size = Number(_data.pageSize) || 30;
    const first = _shiftedRows > 0 ? Math.max(0, Math.floor((next * size - _shiftedRows) / size)) : next;
    let data, more = [];
    for (let page = first; page <= next; page++) {
      data = await fetchGames('history', page);
      more = more.concat(Array.isArray(data?.items) ? data.items : []);
    }
    const seen = new Set(_rows.map((g) => String(g.gameId)));
    _page = next;
    _shiftedRows = 0;
    _rows = _rows.concat(more.filter((g) => !seen.has(String(g.gameId))).map(withLinkedRecording));
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
// view         = a filter pill (server-side view switch, page reset).
// open_review  = clicking a whole game row (→ review page); watch_vod (→ VOD).
// load_more    = History pagination (append next page).
// hide_game    = Remove from history on a row; undo_hide = its Undo.
// restore_game = Restore on a row in the Removed view.
// clear_search = empty the search field.
const ACTIONS = new Set(['view', 'open_review', 'watch_vod', 'load_more', 'manual_entry', 'hide_game', 'undo_hide',
  'restore_game', 'clear_search']);

// The field's own clear button shows while it holds text.
function syncSearchClear() {
  show($('lib-search-clear'), !!String($('lib-search')?.value || ''));
}

function clearSearch() {
  const field = $('lib-search');
  if (field) field.value = '';
  syncSearchClear();
  if (_data) renderList(_data);
  field?.focus?.();
}

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Switch the active view: server-side fetch of that view's data source.
  if (action === 'view') {
    const v = target.dataset.view || DEFAULT_VIEW;
    if (v !== _view) await loadView(v);
    return;
  }

  // Clicking a game row opens THAT game's review page.
  if (action === 'open_review') {
    const gid = target.dataset.gameId;
    window.location.href = gid ? `review.html?gameId=${encodeURIComponent(gid)}` : 'review.html';
    return;
  }

  // A row whose cue reads "Watch" opens the VOD player: the cue and the
  // click must agree (rows without a recording keep open_review).
  if (action === 'watch_vod') {
    const gid = target.dataset.gameId;
    window.location.href = gid ? `vodplayer.html?gameId=${encodeURIComponent(gid)}` : 'games.html';
    return;
  }

  // History pagination: append the next page.
  if (action === 'load_more') {
    await loadMore();
    return;
  }

  if (action === 'hide_game') { await hideGame(target); return; }
  if (action === 'undo_hide') { await undoHide(); return; }
  if (action === 'restore_game') { await restoreGame(target); return; }
  if (action === 'clear_search') { clearSearch(); return; }

  // Add a game: log a game by hand.
  if (action === 'manual_entry') {
    window.location.href = 'manualentry.html';
    return;
  }
});

// Search filters as you type; nothing is sent to the sidecar.
document.addEventListener('input', (ev) => {
  if (ev.target?.id !== 'lib-search') return;
  syncSearchClear();
  if (_data) renderList(_data);
});

document.addEventListener('keydown', (ev) => {
  // Escape empties a non-empty search field.
  if (ev.key === 'Escape' && ev.target?.id === 'lib-search' && ev.target.value) {
    ev.preventDefault();
    clearSearch();
    return;
  }
  // Keyboard activation for the role="button" game rows (Enter / Space).
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  // A real button inside a row (Remove from history, Restore) handles its own keys.
  if (ev.target.tagName === 'BUTTON') return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// ── boot ────────────────────────────────────────────────────────────────────
function boot() { loadView(viewFromSearch(window.location?.search)); }
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
