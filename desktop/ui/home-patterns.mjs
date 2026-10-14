import { readSnapshot as defaultRead } from './data.mjs';
import { getListen as defaultGetListen } from './platform/index.mjs';
import { nudgeLiveChange } from './shell-patterns.mjs';

// Home's "Review 4 repeated mistakes" pill: a mistake trend ready for a back-to-back
// review. It reads get_pattern_nudge, like the Patterns count in the sidebar. The
// sidecar keeps it quiet until four watchable moments repeat across three games, for 48
// hours after any review and during champ select or a game. Home can stay open through
// champ select (the shell leaves it in place while the composer has text), so a lobby or
// game starting hides the pill at once and its end asks again. A failed check shows
// nothing. No toasts and no notifications.

/** Paints the pill from a get_pattern_nudge reply; anything but a clear "ready" hides it. */
export function renderPatternPill(doc, reply) {
  const pill = doc?.getElementById?.('home-suggest-patterns');
  if (!pill) return;
  const moments = Math.trunc(Number(reply?.momentCount) || 0);
  const ready = reply?.notify === true && !String(reply?.errorText || '').trim() && moments > 0 && !!reply?.patternKey;
  pill.hidden = !ready;
  if (!ready) return;
  pill.href = `patterns.html?pattern=${encodeURIComponent(reply.patternKey)}`;
  const text = doc.getElementById('home-suggest-patterns-text');
  if (text) text.textContent = `Review ${moments} repeated mistakes`;
  const games = Math.trunc(Number(reply.gameCount) || 0);
  pill.title = [String(reply.title || '').trim(), games > 0 ? `across ${games} games` : ''].filter(Boolean).join(', ');
}

/** Reads the nudge now and follows champ select and games. Returns its hooks for tests. */
export function mountPatternPill(doc = globalThis.document, { read = defaultRead, getListen = defaultGetListen } = {}) {
  let asked = 0;
  async function refresh() {
    const mine = ++asked;
    let reply = null;
    try { reply = await read('get_pattern_nudge', 'sample-pattern-nudge.json'); } catch (_) { reply = null; }
    // A lobby that began while this read was in flight keeps the pill hidden.
    if (mine === asked) renderPatternPill(doc, reply);
  }
  function onLive(message) {
    const change = nudgeLiveChange(message);
    if (change === 'hide') { asked += 1; renderPatternPill(doc, null); }
    else if (change === 'ask') void refresh();
  }
  async function follow() {
    let listen = null;
    try { listen = await getListen(); } catch (_) { listen = null; }
    if (!listen) return;
    try { await listen('lcu-event', event => onLive(event?.payload)); } catch (_) { /* the next visit to Home asks again */ }
  }
  const ready = Promise.all([refresh(), follow()]);
  return { refresh, onLive, ready };
}
