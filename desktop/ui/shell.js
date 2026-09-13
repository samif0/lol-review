import { getInvoke, getListen } from './platform/index.mjs';

// Shared app shell — highlights the active item on the left nav rail and handles
// the LCU live auto-show. Each page includes this with
// <script type="module" src="./shell.js" data-page="dashboard"></script>.
//
// Navigation is plain file routing (page = its own .html) which is robust under
// Electron's webview. The active page is highlighted from the script's data-page.
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
  prepareNavigation(nav);
  if (['review', 'vodplayer', 'manualentry'].includes(active)) active = 'games';
  if (['pregame', 'ingame'].includes(active)) active = 'dashboard';
  if (active.startsWith('objective')) active = 'objectives';
  nav.querySelectorAll('.nav-i').forEach((a) => {
    const current = a.dataset.nav === active;
    a.classList.toggle('active', current);
    if (current) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  });
  // .nav-ready is authored into the static markup and stays on for the page's life
  // (the rail is never re-created), so the energy-trail animation + nav-i
  // transitions are live from first paint. Re-assert it here defensively in case a
  // page ever ships the rail without the class.
  nav.classList.add('nav-ready');
}

function prepareNavigation(nav) {
  if (nav.dataset.grouped) return;
  nav.dataset.grouped = 'true';
  nav.setAttribute('aria-label', 'Main navigation');
  const links = new Map([...nav.querySelectorAll('[data-nav]')].map(link => [link.dataset.nav, link]));
  const groups = [['Daily review', [['dashboard', 'Home'], ['games', 'Match library']]],
    ['Improve', [['objectives', 'Learning objectives'], ['patterns', 'Patterns'], ['matchups', 'Matchup notes']]],
    ['Session', [['tiltcheck', 'Mental check'], ['rules', 'Rules']]],
    ['', [['settings', 'Settings'], ['onboarding', 'Account']]]];
  const brand = document.createElement('div');
  brand.className = 'nav-brand'; brand.textContent = 'Revu'; nav.prepend(brand);
  nav.querySelector('.nav-spacer')?.remove();
  for (const [title, items] of groups) {
    const group = document.createElement('div'); group.className = title ? 'nav-group' : 'nav-footer';
    if (title) {
      const heading = document.createElement('div'); heading.className = 'nav-group-title'; heading.textContent = title;
      group.append(heading);
    }
    for (const [id, label] of items) {
      const link = links.get(id); if (!link) continue;
      link.setAttribute('aria-label', label);
      const text = link.querySelector('.tip'); if (text) text.textContent = label;
      if (id === 'onboarding') link.setAttribute('href', 'onboarding.html?signin=1');
      group.append(link);
    }
    nav.append(group);
  }
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

// ── LCU live auto-show (replaces ShellViewModel navigation) ───────────────────
// Champ Select and In-Game are not nav items — they're surfaces the app brings up
// automatically when the LCU stream reports a champ select or a live game, exactly
// like the WinUI shell auto-navigated. We open (or join) the SSE stream on EVERY
// page and route to pregame.html / ingame.html on the discrete transition events.
// Best-effort: outside Electron the invoke/listen resolvers return null → silent no-op.
//
// We only navigate on the TRANSITION events (champSelectStarted / gameInProgress),
// never on the periodic liveState replay — so a user who manually leaves the live
// page during champ select isn't yanked back on the next LCU tick. The one
// exception is a FRESH page load that joins mid-flow (handled below via a one-shot
// guard on the first liveState).
async function wireLiveAutoShow() {
  const invoke = await getInvoke();

  const listen = await getListen();

  if (!invoke || !listen) return;

  const here = (file) => {
    const path = (window.location.pathname || '').toLowerCase();
    return path.endsWith('/' + file) || path.endsWith(file) || (file === 'index.html' && (path === '/' || path.endsWith('/')));
  };
  const goto = (file) => { if (!here(file)) window.location.href = file; };
  // Leave a live surface only if we're currently ON one (don't disturb other pages).
  const leaveLiveSurface = () => { if (here('pregame.html') || here('ingame.html')) goto('index.html'); };

  // Pages where the user is actively ENTERING DATA — auto-show must NOT yank them
  // off these mid-task (a champ-select/game LCU tick used to navigate away on Save,
  // wiping the form — "the page refreshes on save review"). Auto-show to a live
  // surface only happens from a passive page (dashboard/games/etc.). The live
  // surfaces themselves are fine to switch BETWEEN (pregame↔ingame).
  const ACTIVE_WORK_PAGES = ['review.html', 'objectives.html', 'manualentry.html', 'settings.html', 'vodplayer.html', 'matchups.html'];
  const onActiveWorkPage = () => ACTIVE_WORK_PAGES.some((p) => here(p));
  // Auto-show to a live surface, but never interrupt an active-work page (unless
  // we're already on a live surface, e.g. pregame→ingame, which we always honor).
  const liveGoto = (file) => {
    if (here('pregame.html') || here('ingame.html')) { goto(file); return; }
    if (onActiveWorkPage()) return; // don't yank the user off a form mid-edit
    goto(file);
  };

  // One-shot: the first liveState (the connect replay) may indicate we joined the
  // stream mid-flow. Auto-show then, but only once, so later ticks don't re-yank.
  let sawFirstLiveState = false;

  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    const t = msg.type;
    const p = msg.payload || {};
    switch (t) {
      case 'vodLinked':
        window.dispatchEvent(new CustomEvent('revu:vod-linked', { detail: p }));
        break;
      case 'champSelectStarted':
        liveGoto('pregame.html');
        break;
      case 'gameInProgress':
        liveGoto('ingame.html');
        break;
      case 'gameEnded':
      case 'champSelectCancelled':
        leaveLiveSurface();
        break;
      case 'liveState':
        if (!sawFirstLiveState) {
          sawFirstLiveState = true;
          // Mid-flow join: in-game wins over champ select. Only auto-show from a
          // non-live, non-active-work page so we never bounce a user who's already
          // navigating OR actively filling out a review/objective form.
          if (p.isGameInProgress) liveGoto('ingame.html');
          else if (p.sessionKey) liveGoto('pregame.html');
        } else if (!p.isGameInProgress && !p.sessionKey) {
          // Mirror shell-outer.js: a reconnect replay that says "no game, no
          // champ select" is authoritative — if the gameEnded event was lost
          // across an SSE drop, this is what unsticks the In Game surface.
          leaveLiveSurface();
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
