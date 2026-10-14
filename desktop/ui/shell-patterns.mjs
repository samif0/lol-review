import { readSnapshot as defaultRead } from './data.mjs';
import { getListen as defaultGetListen } from './platform/index.mjs';

// The Patterns count in the sidebar: a quiet "ready to review" nudge. The sidecar
// decides (get_pattern_nudge): a mistake trend needs four watchable moments across three
// games since its last review, and nothing nudges for 48 hours after any review or while
// the player is in champ select or a game. This module only reads and paints the count,
// like the Match library count. No toasts, no OS notifications, no tray changes.
//
// It never navigates, reloads the frame or reads a page's active-work flag. Finishing a
// review on the Patterns page announces it with
// window.top.dispatchEvent(new CustomEvent('revu:patterns-changed')), which asks again
// at once so the count clears without a reload.

// Navigation asks again at most this often (moments are saved and rated in other pages).
export const NUDGE_THROTTLE_MS = 15_000;
// A slow re-check while the window is shown, so the quiet period can end on its own.
export const NUDGE_POLL_MS = 15 * 60_000;
export const EVENT_DEBOUNCE_MS = 600;
// Champ select or a game starting hides the count at once; their end asks again, and
// the sidecar stays the authority on whether a lobby or game is still open.
export const HIDE_ON = Object.freeze(['champSelectStarted', 'gameInProgress']);
export const ASK_ON = Object.freeze(['champSelectCancelled', 'gameEnded', 'gameRecovered', 'liveState', 'lcuConnection']);

/**
 * What a live event means for the nudge: 'hide' (a lobby or game began), 'ask' (one may
 * have ended: read the sidecar again) or ''. Shared by the sidebar count and Home's pill.
 */
export function nudgeLiveChange(message) {
  const type = message?.type, payload = message?.payload || {};
  if (HIDE_ON.includes(type)) return 'hide';
  // The replayed state on every (re)connect: a game in progress holds the nudge.
  if (type === 'liveState') return payload.isGameInProgress ? 'hide' : 'ask';
  // Closing the League client ends champ select without a cancel event.
  if (type === 'lcuConnection') return payload.connected === false ? 'ask' : '';
  return ASK_ON.includes(type) ? 'ask' : '';
}

/**
 * Ready patterns to show from a get_pattern_nudge reply. 0 hides the count: the nudge is
 * held back, nothing is ready, or the check failed (a broken check never nags).
 */
export function nudgeCount(reply) {
  if (!reply || typeof reply !== 'object') return 0;
  if (typeof reply.errorText === 'string' && reply.errorText.trim()) return 0;
  if (reply.notify !== true) return 0;
  const n = Number(reply.readyCount);
  return Number.isSafeInteger(n) && n > 0 ? n : 0;
}

/** Screen-reader text for the count. */
export function nudgeLabel(n) {
  return n === 1 ? '1 pattern ready to review' : `${n} patterns ready to review`;
}

/** Wires the Patterns count to the shell document. */
export function createPatternNudge({
  doc = globalThis.document,
  win = globalThis.window,
  read = defaultRead,
  getListen = defaultGetListen,
  now = () => Date.now(),
  schedule = (fn, ms) => setTimeout(fn, ms),
  cancel = id => clearTimeout(id),
} = {}) {
  const $ = id => doc?.getElementById?.(id) || null;
  const count = $('nav-patterns-count'), countText = $('nav-patterns-count-text');
  const visible = () => doc?.visibilityState !== 'hidden';
  let shown = 0, live = false, running = null, again = false, stale = false, timer = null;
  let lastAsk = -Infinity;

  function show(n) {
    shown = live ? 0 : n;
    if (count) {
      count.textContent = shown ? String(shown) : '';
      count.hidden = !shown;
    }
    if (countText) countText.textContent = shown ? nudgeLabel(shown) : '';
  }

  // One read at a time; a trigger that lands mid-flight asks once more after it.
  function refresh() {
    if (running) { again = true; return running; }
    // A read now makes any waiting trailing read redundant.
    if (timer !== null) { cancel(timer); timer = null; }
    lastAsk = now();
    stale = false;
    running = (async () => {
      let reply = null;
      try { reply = await read('get_pattern_nudge', 'sample-pattern-nudge.json'); } catch (_) { reply = null; }
      show(nudgeCount(reply));
    })().finally(() => {
      running = null;
      if (again) { again = false; void refresh(); }
    });
    return running;
  }

  // A hidden window only remembers that the count is stale and asks when shown again.
  function requestRefresh(delay = EVENT_DEBOUNCE_MS) {
    if (!visible()) { stale = true; return; }
    if (timer !== null) cancel(timer);
    timer = schedule(() => { timer = null; void refresh(); }, Math.max(0, delay));
  }

  /** The frame finished loading a page: ask again, at most once per throttle window. */
  function frameLoaded() {
    const wait = lastAsk + NUDGE_THROTTLE_MS - now();
    if (wait <= 0 && visible()) void refresh();
    else requestRefresh(wait);
  }

  function onLive(message) {
    const change = nudgeLiveChange(message);
    if (change === 'hide') { live = true; if (timer !== null) { cancel(timer); timer = null; } show(0); return; }
    if (change !== 'ask') return;
    live = false;
    requestRefresh();
  }

  async function subscribe() {
    let listen = null;
    try { listen = await getListen(); } catch (_) { listen = null; }
    if (!listen) return;
    try { await listen('lcu-event', (event) => onLive(event?.payload)); } catch (_) { /* navigation still asks */ }
  }

  function poll() {
    schedule(() => { if (visible()) void refresh(); else stale = true; poll(); }, NUDGE_POLL_MS);
  }

  function start() {
    void refresh();
    void subscribe();
    poll();
    doc?.addEventListener?.('visibilitychange', () => { if (stale && visible()) requestRefresh(); });
    win?.addEventListener?.('revu:patterns-changed', () => { void refresh(); });
  }

  return { start, refresh, frameLoaded, onLive, get count() { return shown; } };
}
