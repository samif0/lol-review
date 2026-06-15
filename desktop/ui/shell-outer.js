// Persistent app-shell controller (index.html). Owns the ONE nav rail (static
// markup that never reloads) and the content <iframe>. Nav clicks reload ONLY the
// iframe, so the rail can never move on navigation. This module also runs the two
// app-wide concerns that used to live per-page in shell.js — the sidebar-animation
// gate and the LCU live auto-show — ONCE here, retargeting auto-show at the iframe.
//
// Content pages still ship their own static rail + shell.js as a standalone
// fallback, but framed.js hides that rail inside the iframe (html.framed), and
// shell.js skips its own LCU wiring when framed (the shell owns it).

const frame = document.getElementById('app-frame');
const nav = document.querySelector('.nav');

// Map an iframe URL → the nav item id to light up. Pages share data-page with the
// rail's data-nav; the three objective sub-pages map to 'objectives'; manual entry
// is reached from Games (and is no longer a rail item) so it keeps Games lit;
// live/drill-in surfaces (pregame/ingame/review/vodplayer) map to nothing.
function fileToNav(pathname) {
  const file = (pathname || '').split('/').pop().toLowerCase() || 'dashboard.html';
  if (file === '' || file === 'index.html' || file === 'dashboard.html') return 'dashboard';
  if (file.startsWith('objective')) return 'objectives'; // objectives / objectivegames / objectivenotes
  if (file === 'manualentry.html') return 'games'; // reached from Games; keep Games active
  return file.replace(/\.html$/, ''); // games.html → games, etc.
}

function markActive(navId) {
  if (!nav) return;
  nav.querySelectorAll('.nav-i').forEach((a) => {
    a.classList.toggle('active', a.dataset.nav === navId);
  });
}

// Navigate the iframe (NOT the top document). Accepts a bare file or file?query.
function frameGoto(target) {
  if (!frame) return;
  // Resolve relative to the UI root so 'games.html' and 'games.html?x=1' both work.
  const url = new URL(target, frame.src || location.href);
  if (frame.contentWindow && frame.contentWindow.location.href === url.href) return; // already there
  frame.src = url.pathname.split('/').pop() + url.search + url.hash;
}

// Current iframe file (best-effort; same-origin in-app so this is readable).
function frameFile() {
  try { return frame.contentWindow.location.pathname.split('/').pop() || 'dashboard.html'; }
  catch (_) { try { return new URL(frame.src, location.href).pathname.split('/').pop(); } catch (e) { return ''; } }
}
function frameHas(file) { return frameFile().toLowerCase() === file.toLowerCase(); }

// ── Nav clicks → reload only the iframe ──────────────────────────────────────
// One delegated handler on the persistent rail. preventDefault the anchor's own
// top-level navigation and point the iframe at it instead; mark active instantly
// (the rail never reloads, so this is the only place active state changes from a
// user click — the iframe 'load' handler re-syncs for in-page navigations).
nav?.addEventListener('click', (ev) => {
  const a = ev.target.closest('.nav-i');
  if (!a) return;
  ev.preventDefault();
  const href = a.getAttribute('href');
  if (!href) return;
  markActive(a.dataset.nav);
  frameGoto(href);
});

// ── Sync active item whenever the iframe finishes loading a page ─────────────
// Covers in-page navigations (a page doing location.href=... inside the iframe,
// e.g. open_review → review.html) so the rail reflects wherever the iframe landed.
frame?.addEventListener('load', () => {
  try { markActive(fileToNav(frame.contentWindow.location.pathname)); }
  catch (_) { /* cross-origin (shouldn't happen in-app) — leave as-is */ }
});

// ── Deep-link support on the shell URL ───────────────────────────────────────
// index.html#vodplayer.html?gameId=5 opens that page in the iframe at startup, and
// reacts to later hash changes. (Normal use doesn't need this, but it keeps the
// shell linkable and lets external callers target a page.)
function openFromHash() {
  const h = (location.hash || '').replace(/^#/, '');
  if (h) frameGoto(h);
}
window.addEventListener('hashchange', openFromHash);
if ((location.hash || '').length > 1) openFromHash();

// ── Sidebar energy-trail gate (mirrors SidebarEnergyDrainAnimator.Enabled) ────
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
    if (cfg && cfg.sidebarAnimationEnabled === false) document.body.setAttribute('data-sidebar-anim', 'off');
    else document.body.removeAttribute('data-sidebar-anim');
  } catch (_) { /* sidecar not ready — leave trails on (default) */ }
}
applySidebarAnimGate();

// ── Title-bar version ─────────────────────────────────────────────────────────
// Populate the branded strip's version (REVU <version>) from the app package info.
// Best-effort: in browser preview (no Tauri) the version chip just stays blank.
async function fillAppVersion() {
  let invoke = null;
  try {
    const core = await import('@tauri-apps/api/core');
    if (core && typeof core.invoke === 'function') invoke = core.invoke;
  } catch (_) { /* fall through */ }
  if (!invoke && window.__TAURI__?.core?.invoke) invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
  const el = document.getElementById('appbar-ver');
  if (!invoke || !el) return;
  try { const v = await invoke('app_version'); if (v) el.textContent = String(v); } catch (_) { /* leave blank */ }
}
fillAppVersion();

// ── LCU (League Client) connection indicator ─────────────────────────────────
// Toggles the appbar's LCU chip between connected (green dot) and not. Called from
// the SSE listener below: seeded by the replayed liveState, updated live by the
// lcuConnection event. Pure DOM toggle — safe to call before the stream is wired.
function setLcuIndicator(connected) {
  const el = document.getElementById('appbar-lcu');
  if (!el) return;
  el.classList.toggle('on', !!connected);
  el.title = connected ? 'League Client: connected' : 'League Client: not connected';
}

// ── Custom window controls (frameless title bar) ─────────────────────────────
// The window is decorations:false, so the appbar's min/max/close buttons drive the
// native window via the Tauri window API. withGlobalTauri exposes window.__TAURI__.
// In browser preview these are absent → the buttons no-op silently.
async function wireWindowControls() {
  let getWin = null;
  try {
    const w = await import('@tauri-apps/api/window');
    if (w && typeof w.getCurrentWindow === 'function') getWin = w.getCurrentWindow;
  } catch (_) { /* fall through */ }
  if (!getWin && window.__TAURI__?.window?.getCurrentWindow) getWin = window.__TAURI__.window.getCurrentWindow;
  if (!getWin) return; // preview — no window API
  const win = getWin();
  const on = (id, fn) => { const b = document.getElementById(id); if (b) b.addEventListener('click', () => { fn().catch(() => {}); }); };
  on('win-min', () => win.minimize());
  on('win-max', () => win.toggleMaximize());
  on('win-close', () => win.close());
}
wireWindowControls();

// ── LCU live auto-show (retargeted at the iframe) ────────────────────────────
// Same contract as the old per-page shell.js, but it navigates the CONTENT IFRAME
// instead of the top document, and lives ONCE on the persistent shell (so there's
// exactly one SSE listener for the app's lifetime — no per-navigation re-subscribe).
async function wireLiveAutoShow() {
  let invoke = null;
  try {
    const core = await import('@tauri-apps/api/core');
    if (core && typeof core.invoke === 'function') invoke = core.invoke;
  } catch (_) { /* fall through */ }
  if (!invoke && window.__TAURI__?.core?.invoke) invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);

  let listen = null;
  try {
    const evm = await import('@tauri-apps/api/event');
    if (evm && typeof evm.listen === 'function') listen = evm.listen;
  } catch (_) { /* fall through */ }
  if (!listen && window.__TAURI__?.event?.listen) listen = window.__TAURI__.event.listen.bind(window.__TAURI__.event);

  if (!invoke || !listen) return;

  const goto = (file) => { if (!frameHas(file)) frameGoto(file); };
  const leaveLiveSurface = () => { if (frameHas('pregame.html') || frameHas('ingame.html')) frameGoto('dashboard.html'); };

  let sawFirstLiveState = false;
  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    const t = msg.type;
    const p = msg.payload || {};
    switch (t) {
      case 'champSelectStarted': goto('pregame.html'); break;
      case 'gameInProgress': goto('ingame.html'); break;
      case 'gameEnded':
      case 'champSelectCancelled': leaveLiveSurface(); break;
      case 'liveState':
        // The replayed snapshot carries the current client state — seed the LCU
        // indicator from it (live changes arrive via 'lcuConnection' below).
        setLcuIndicator(!!p.lcuConnected);
        if (!sawFirstLiveState) {
          sawFirstLiveState = true;
          if (p.isGameInProgress) goto('ingame.html');
          else if (p.sessionKey) goto('pregame.html');
        }
        break;
      case 'lcuConnection': setLcuIndicator(!!p.connected); break;
      default: break;
    }
  });
  try { await invoke('start_lcu_events'); } catch (_) { /* sidecar may still be starting */ }
}
wireLiveAutoShow();
