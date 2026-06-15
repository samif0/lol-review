// Shared app shell — highlights the active item on the left nav rail and handles
// the LCU live auto-show. Each page includes this with
// <script type="module" src="./shell.js" data-page="dashboard"></script>.
//
// Navigation is plain file routing (page = its own .html) which is robust under
// Tauri's webview. The active page is highlighted from the script's data-page.
//
// The rail itself is AUTHORED AS STATIC HTML in every page (a <nav class="nav">
// before .shell), INCLUDING the .active class on the current page's item, so the
// whole rail — active highlight and all — is parsed + painted with the first frame.
// It used to be built (and the active item marked) by JS after DOMContentLoaded —
// but a deferred module runs after first paint, so on every navigation the rail,
// and later just the active-item highlight, arrived a frame late and the .nav-i
// transition animated the highlight INTO place, which read as the rail "jiggling"
// on each page swap. Authoring .active statically removes that frame entirely (no
// JS, no transition on first paint). This module now only RE-ASSERTS the active
// item as a safety net (a no-op when the static markup already matches data-page).
// (Pre-game, in-game, review and the VOD player are NOT rail items — they're
// auto-shown / drilled-into surfaces — so no item lights up there, which is right.)

// Re-assert the active item on the static rail. classList.toggle(...,bool) does NOT
// remove-then-re-add when the state already matches, so on the normal path (static
// .active already correct) this fires NO style change and therefore no transition —
// it only corrects a mismatch (e.g. a page whose data-page and stamped item differ).
function markActive(active) {
  const nav = document.querySelector('.nav');
  if (!nav) return; // static rail should already be in the page markup
  nav.querySelectorAll('.nav-i').forEach((a) => {
    a.classList.toggle('active', a.dataset.nav === active);
  });
  // .nav-ready is authored into the static markup and stays on for the page's life
  // (the rail is never re-created), so the energy-trail animation + nav-i
  // transitions are live from first paint. Re-assert it here defensively in case a
  // page ever ships the rail without the class.
  nav.classList.add('nav-ready');
}

// Resolve the active page from this script tag's data-page attribute.
const me = document.currentScript || document.querySelector('script[data-page]');
const activePage = me?.dataset.page || 'dashboard';

// Are we loaded inside the persistent app shell's iframe? If so, the SHELL owns the
// rail + the LCU auto-show; this page must NOT manage a nav or open its own SSE
// stream (that would double-subscribe and double-navigate). framed.js (a <head>
// script) has already added html.framed and CSS hides this page's own rail.
let FRAMED = false;
try { FRAMED = window.self !== window.top; } catch (_) { FRAMED = true; }

if (!FRAMED) {
  // Standalone (top-level) fallback: manage our own static rail's active item.
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => markActive(activePage));
  } else {
    markActive(activePage);
  }
}

// ── Sidebar energy-trail gate (mirrors SidebarEnergyDrainAnimator.Enabled) ────
// The streaming energy trails on the rail (styles.css .nav::after) are gated by
// the SidebarAnimationEnabled config flag, exactly like the WinUI animator. We
// read the config once and stamp body[data-sidebar-anim]; CSS does the rest.
// Default ON (no attribute) so the look is preserved if config can't be read.
async function applySidebarAnimGate() {
  let invoke = null;
  try {
    const core = await import('@tauri-apps/api/core');
    if (core && typeof core.invoke === 'function') invoke = core.invoke;
  } catch (_) { /* fall through */ }
  if (!invoke && window.__TAURI__?.core?.invoke) invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
  if (!invoke) return; // browser preview — leave trails on
  try {
    const cfg = await invoke('get_config');
    // Only flip OFF when explicitly disabled; treat missing/true as enabled.
    if (cfg && cfg.sidebarAnimationEnabled === false) {
      document.body.setAttribute('data-sidebar-anim', 'off');
    } else {
      document.body.removeAttribute('data-sidebar-anim');
    }
  } catch (_) { /* sidecar not ready — leave trails on (default) */ }
}
if (!FRAMED) applySidebarAnimGate(); // framed: the shell (index.html) owns this rail + its gate

// ── LCU live auto-show (replaces ShellViewModel navigation) ───────────────────
// Champ Select and In-Game are not nav items — they're surfaces the app brings up
// automatically when the LCU stream reports a champ select or a live game, exactly
// like the WinUI shell auto-navigated. We open (or join) the SSE stream on EVERY
// page and route to pregame.html / ingame.html on the discrete transition events.
// Best-effort: outside Tauri the invoke/listen resolvers return null → silent no-op.
//
// We only navigate on the TRANSITION events (champSelectStarted / gameInProgress),
// never on the periodic liveState replay — so a user who manually leaves the live
// page during champ select isn't yanked back on the next LCU tick. The one
// exception is a FRESH page load that joins mid-flow (handled below via a one-shot
// guard on the first liveState).
async function wireLiveAutoShow() {
  let invoke = null;
  try {
    const core = await import('@tauri-apps/api/core');
    if (core && typeof core.invoke === 'function') invoke = core.invoke;
  } catch (_) { /* fall through */ }
  if (!invoke && window.__TAURI__?.core?.invoke) invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);

  let listen = null;
  try {
    const ev = await import('@tauri-apps/api/event');
    if (ev && typeof ev.listen === 'function') listen = ev.listen;
  } catch (_) { /* fall through */ }
  if (!listen && window.__TAURI__?.event?.listen) listen = window.__TAURI__.event.listen.bind(window.__TAURI__.event);

  if (!invoke || !listen) return;

  const here = (file) => {
    const path = (window.location.pathname || '').toLowerCase();
    return path.endsWith('/' + file) || path.endsWith(file) || (file === 'index.html' && (path === '/' || path.endsWith('/')));
  };
  const goto = (file) => { if (!here(file)) window.location.href = file; };
  // Leave a live surface only if we're currently ON one (don't disturb other pages).
  const leaveLiveSurface = () => { if (here('pregame.html') || here('ingame.html')) goto('index.html'); };

  // One-shot: the first liveState (the connect replay) may indicate we joined the
  // stream mid-flow. Auto-show then, but only once, so later ticks don't re-yank.
  let sawFirstLiveState = false;

  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    const t = msg.type;
    const p = msg.payload || {};
    switch (t) {
      case 'champSelectStarted':
        goto('pregame.html');
        break;
      case 'gameInProgress':
        goto('ingame.html');
        break;
      case 'gameEnded':
      case 'champSelectCancelled':
        leaveLiveSurface();
        break;
      case 'liveState':
        if (!sawFirstLiveState) {
          sawFirstLiveState = true;
          // Mid-flow join: in-game wins over champ select. Only auto-show from a
          // non-live page so we never bounce a user who's already navigating.
          if (p.isGameInProgress) goto('ingame.html');
          else if (p.sessionKey) goto('pregame.html');
        }
        break;
      default:
        break;
    }
  });
  try { await invoke('start_lcu_events'); } catch (_) { /* sidecar may still be starting */ }
}
if (!FRAMED) wireLiveAutoShow(); // framed: the shell owns the single LCU SSE + auto-show

export { activePage };
