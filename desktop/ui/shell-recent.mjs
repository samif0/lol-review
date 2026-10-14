import { readSnapshot as defaultRead } from './data.mjs';
import { getListen as defaultGetListen } from './platform/index.mjs';
import { resultOf } from './meaning-tone.mjs';

// Sidebar data for the persistent shell (index.html): the six most recent matches, the
// Match library count of matches to review, the account row in the footer, and the
// title-bar crumb. The pure helpers are exported for tests; createShellSidebar wires
// them to the shell document.
//
// This module only reads. It never navigates or reloads the frame, and it never reads
// or changes window.__revuActiveWork: row clicks go through the shell's own delegated
// [data-shell-goto] handler, so the page's leave guard still runs first. It keeps its
// own live-event subscription, so the shell's auto-show switch stays untouched.

export const RECENT_LIMIT = 6;
// Live events that can add a match to history or change one of its dots.
export const REFRESH_EVENTS = Object.freeze(['gameEnded', 'gameRecovered', 'gamesRecovered', 'remakeUpdated', 'vodLinked']);
// Reviews, skips, removals and manual entries happen inside these pages; leaving one may
// change the list.
export const REFRESH_AFTER_PAGES = Object.freeze(['review', 'games', 'dashboard', 'manualentry']);
// Opening or leaving these pages rereads the sign-in and Riot ID shown in the footer:
// they change it, and the Account page should never disagree with the row that opened it.
export const IDENTITY_AFTER_PAGES = Object.freeze(['settings', 'onboarding']);
export const LOAD_THROTTLE_MS = 5000;
export const EVENT_DEBOUNCE_MS = 600;
// Frame pages that sit under the Match library in the title-bar crumb.
const LIBRARY_PAGES = new Set(['review', 'vodplayer', 'manualentry']);
const MATCH_PAGES = new Set(['review', 'vodplayer']);

const text = value => (typeof value === 'string' ? value.trim() : '');
const validId = value => {
  const id = Number(value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
};

/** The page key for a frame path: file name, lower case, without .html ('' and '/' are index). */
export function pageOf(pathname) {
  const file = String(pathname ?? '').split(/[?#]/)[0].split('/').pop().toLowerCase();
  return file.replace(/\.html$/, '') || 'index';
}

/** "Sivir vs Senna", or the one champion that is known, or "Match". */
export function matchLabel(row) {
  const own = text(row?.championName), enemy = text(row?.enemyChampion);
  if (own && enemy) return `${own} vs ${enemy}`;
  return own || (enemy ? `vs ${enemy}` : 'Match');
}

/** Opens the recording when the row's own action does, otherwise the review. */
export function matchHref(row) {
  const id = validId(row?.gameId);
  if (!id) return '';
  return row?.action === 'watch_vod' ? `vodplayer.html?gameId=${id}` : `review.html?gameId=${id}`;
}

/**
 * Up to `limit` sidebar rows from a get_games snapshot. Returns null when the snapshot
 * reports a load error, so the caller keeps what it already shows; [] means no matches.
 */
export function recentRows(snapshot, limit = RECENT_LIMIT) {
  if (!snapshot || typeof snapshot !== 'object' || text(snapshot.errorText)) return null;
  const items = Array.isArray(snapshot.items) ? snapshot.items : [];
  const rows = [];
  for (const item of items) {
    if (rows.length >= limit) break;
    const href = matchHref(item);
    if (!href) continue;
    const result = resultOf(item);
    const pending = result !== 'remake' && item.hasReview !== true;
    const label = matchLabel(item);
    const resultWord = result === 'win' ? 'Win' : result === 'loss' ? 'Loss' : 'Remake';
    const when = text(item.datePlayed);
    rows.push({
      gameId: validId(item.gameId), label, href, result, pending,
      ariaLabel: [label, resultWord.toLowerCase(), pending ? 'to review' : ''].filter(Boolean).join(', '),
      title: [label, resultWord, when, pending ? 'To review' : ''].filter(Boolean).join(' · '),
    });
  }
  return rows;
}

/**
 * Matches waiting for review, from a get_games {view:'queue'} snapshot. A snapshot of a
 * different view (the browser preview serves one fixture for every view) counts its own
 * unreviewed rows instead. Returns null when nothing trustworthy can be said.
 */
export function pendingCount(snapshot) {
  if (!snapshot || typeof snapshot !== 'object' || text(snapshot.errorText)) return null;
  if (snapshot.view === 'queue') {
    const total = Number(snapshot.totalCount);
    return Number.isSafeInteger(total) && total >= 0 ? total : null;
  }
  if (!Array.isArray(snapshot.items)) return null;
  return snapshot.items.filter(item => item && item.hasReview !== true && resultOf(item) !== 'remake').length;
}

/** The game name of a Riot ID ("bye#NA1" is "bye"), or '' when there is none. */
export function ownerName(riotId) {
  return text(riotId).split('#')[0].trim();
}

/** One uppercase letter for the footer avatar, or '' for no name. */
export function avatarInitial(name) {
  const first = [...text(name)][0] || '';
  return first.toLocaleUpperCase();
}

/**
 * The footer account row for a get_config snapshot. Signed in means a live Revu session
 * (the rule Settings uses): the initial and Riot game name, else the email. Anything
 * else, a saved Riot ID or an expired session included, reads "Sign in" with the glyph.
 * `state` is what a screen reader hears after the row's name, "Account".
 */
export function accountView(config) {
  const email = text(config?.riotSessionEmail);
  if (config?.riotAuthState !== 'loggedIn' || !email) {
    return { signedIn: false, name: 'Sign in', initial: '', state: 'Not signed in.' };
  }
  const name = ownerName(config?.riotId) || email;
  return { signedIn: true, name, initial: avatarInitial(name), state: `Signed in as ${email}.` };
}

/**
 * The title-bar crumb for a frame page. `navId` is the rail item that page lights up.
 * Home shows no crumb; review, VOD review and manual entry sit under the Match library.
 */
export function crumbFor(page, navId, labels = {}) {
  const key = text(page), nav = text(navId);
  if (!key || key === 'index' || key === 'dashboard') return { parent: '', current: '' };
  const own = labels[key] || '';
  const current = own || labels[nav] || '';
  const parent = own && LIBRARY_PAGES.has(key) ? (labels.games || '') : '';
  return { parent, current };
}

/** The game a match page shows (review or VOD review), from its query string. */
export function frameGameId(page, search) {
  if (!MATCH_PAGES.has(page)) return null;
  try { return validId(new URLSearchParams(String(search || '')).get('gameId')); } catch (_) { return null; }
}

/** Frame-load refresh rule: after a listing or match page, at most once per throttle window. */
export function shouldRefreshAfter(previousPage, { visible = true, now = 0, last = 0, throttleMs = LOAD_THROTTLE_MS } = {}) {
  if (!visible || !REFRESH_AFTER_PAGES.includes(previousPage)) return false;
  return now - last >= throttleMs;
}

/** Whether a live event changes the recent list; an unsaved game end changes nothing. */
export function refreshesOn(message) {
  const type = message?.type;
  if (!REFRESH_EVENTS.includes(type)) return false;
  if (type === 'gameEnded' || type === 'gameRecovered') return message?.payload?.saved !== false;
  return true;
}

function renderRows(doc, list, rows, currentId) {
  const fragment = doc.createDocumentFragment();
  for (const row of rows) {
    const item = doc.createElement('li');
    const link = doc.createElement('a');
    link.className = 'side-rec';
    link.setAttribute('data-shell-goto', '');
    link.setAttribute('href', row.href);
    link.setAttribute('data-game-id', String(row.gameId));
    link.setAttribute('aria-label', row.ariaLabel);
    link.title = row.title;
    if (currentId && row.gameId === currentId) link.setAttribute('aria-current', 'true');
    const dot = doc.createElement('i');
    dot.className = 'side-rec-r';
    dot.setAttribute('data-result', row.result);
    dot.setAttribute('aria-hidden', 'true');
    const name = doc.createElement('span');
    name.className = 'side-rec-t';
    name.textContent = row.label;
    link.append(dot, name);
    if (row.pending) {
      const todo = doc.createElement('i');
      todo.className = 'side-rec-todo';
      todo.setAttribute('aria-hidden', 'true');
      link.append(todo);
    }
    item.append(link);
    fragment.append(item);
  }
  list.replaceChildren(fragment);
}

/**
 * Wires the sidebar to the shell document. read(command, sample, args) is data.mjs's
 * readSnapshot (live command, or the sample fixture in a browser preview).
 */
export function createShellSidebar({
  doc = globalThis.document,
  win = globalThis.window,
  read = defaultRead,
  getListen = defaultGetListen,
  now = () => Date.now(),
  schedule = (fn, ms) => setTimeout(fn, ms),
  cancel = id => clearTimeout(id),
} = {}) {
  const $ = id => doc?.getElementById?.(id) || null;
  const section = $('side-recent'), list = $('side-recent-list');
  const count = $('nav-games-count'), countText = $('nav-games-count-text');
  const account = $('side-acct'), accountState = $('side-acct-state');
  const name = $('side-name'), avatar = $('side-avatar'), avatarText = $('side-avatar-i');
  const visible = () => doc?.visibilityState !== 'hidden';
  let lastRefresh = -Infinity, running = null, again = false, stale = false, timer = null;
  let rows = [], currentId = null, previousPage = '', identityRead = 0;

  function showRows(next) {
    if (!section || !list || !Array.isArray(next)) return;
    rows = next;
    renderRows(doc, list, rows, currentId);
    section.hidden = rows.length === 0;
  }
  function showCount(value) {
    if (!count) return;
    const n = Number.isSafeInteger(value) && value > 0 ? value : 0;
    count.textContent = n ? String(n) : '';
    count.hidden = !n;
    if (countText) countText.textContent = n ? (n === 1 ? '1 match to review' : `${n} matches to review`) : '';
  }
  function markCurrent(id) {
    currentId = id;
    if (!list) return;
    for (const link of list.querySelectorAll('.side-rec')) {
      if (id && Number(link.getAttribute('data-game-id')) === id) link.setAttribute('aria-current', 'true');
      else link.removeAttribute('aria-current');
    }
  }

  // One request pair at a time; a trigger that lands mid-flight runs once more after it.
  function refresh() {
    if (running) { again = true; return running; }
    lastRefresh = now();
    stale = false;
    running = (async () => {
      const [history, queue] = await Promise.allSettled([
        read('get_games', 'sample-games.json', { view: 'history', page: 0 }),
        read('get_games', 'sample-games-queue.json', { view: 'queue', page: 0 }),
      ]);
      if (history.status === 'fulfilled') showRows(recentRows(history.value));
      if (queue.status === 'fulfilled') {
        const n = pendingCount(queue.value);
        if (n !== null) showCount(n);
      }
    })().catch(() => { /* keep what the sidebar already shows */ }).finally(() => {
      running = null;
      if (again) { again = false; void refresh(); }
    });
    return running;
  }

  // Reads can overlap (sign in, then link a Riot ID); only the latest one is shown.
  async function refreshIdentity() {
    const ticket = ++identityRead;
    try {
      const view = accountView(await read('get_config', 'sample-settings.json'));
      if (ticket !== identityRead) return;
      if (name) name.textContent = view.name;
      if (avatarText) avatarText.textContent = view.initial;
      avatar?.classList.toggle('has-initial', !!view.initial);
      if (accountState) accountState.textContent = view.state;
      account?.setAttribute('data-account', view.signedIn ? 'signed-in' : 'signed-out');
    } catch (_) { /* the footer keeps its last state */ }
  }

  // Bursts (a recovery scan links many recordings) collapse into one refresh. A hidden
  // window only remembers that the list is stale and refreshes when it is shown again.
  function requestRefresh(delay = EVENT_DEBOUNCE_MS) {
    if (!visible()) { stale = true; return; }
    if (timer !== null) cancel(timer);
    timer = schedule(() => { timer = null; void refresh(); }, Math.max(0, delay));
  }

  /** The frame finished loading `pathname` (+ `search`). */
  function frameLoaded(pathname, search) {
    const page = pageOf(pathname);
    markCurrent(frameGameId(page, search));
    const left = previousPage;
    previousPage = page;
    if (left !== page && (IDENTITY_AFTER_PAGES.includes(left) || IDENTITY_AFTER_PAGES.includes(page))) void refreshIdentity();
    if (!REFRESH_AFTER_PAGES.includes(left)) return;
    const at = now();
    if (shouldRefreshAfter(left, { visible: visible(), now: at, last: lastRefresh })) void refresh();
    // Throttled: one trailing refresh when the window ends, so a review saved just
    // after the last refresh still clears its dot.
    else requestRefresh(lastRefresh + LOAD_THROTTLE_MS - at);
  }

  async function subscribe() {
    let listen = null;
    try { listen = await getListen(); } catch (_) { listen = null; }
    if (!listen) return;
    try {
      await listen('lcu-event', (event) => { if (refreshesOn(event?.payload)) requestRefresh(); });
    } catch (_) { /* the frame-load refresh still keeps the list current */ }
  }

  function start() {
    void refresh();
    void refreshIdentity();
    void subscribe();
    // Shown again (restored from the tray): the 30-day sign-in may have run out meanwhile.
    doc?.addEventListener?.('visibilitychange', () => {
      if (!visible()) return;
      void refreshIdentity();
      if (stale) requestRefresh();
    });
    // A framed page that changes the match list in place (skip, remove, a saved missed
    // game) may announce it: window.top.dispatchEvent(new CustomEvent('revu:games-changed')).
    win?.addEventListener?.('revu:games-changed', () => requestRefresh());
    // Signing in or out on the Account page (or to share a clip) announces itself the
    // same way, so the account row changes without leaving the page.
    win?.addEventListener?.('revu:account-changed', () => void refreshIdentity());
  }

  return { start, refresh, refreshIdentity, requestRefresh, frameLoaded, markCurrent,
    get rows() { return rows; } };
}
