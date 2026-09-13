import { getInvoke, getListen, getWindow } from './platform/index.mjs';
import { createMatchViewStore } from './match-navigation.mjs';

// Own the snapshot store in the persistent shell's realm, so its methods never
// retain a discarded content window. Entries contain cloned plain data only.
window.__revuMatchViewsV1 = createMatchViewStore();

// Persistent app-shell controller (index.html). Owns the ONE nav rail (static
// markup that never reloads) and the content <iframe>. Nav clicks reload ONLY the
// iframe, so the rail can never move on navigation. This module also runs the two
// app-wide live-state concerns ONCE here, retargeting auto-show at the iframe.
//
// Content pages still ship their own static rail + shell.js as a standalone
// fallback, but framed.js hides that rail inside the iframe (html.framed), and
// shell.js skips its own LCU wiring when framed (the shell owns it).

const frame = document.getElementById('app-frame');
const nav = document.querySelector('.nav');
const pageLabels = { dashboard: 'Home', games: 'Match library', objectives: 'Learning objectives',
  patterns: 'Patterns', matchups: 'Matchup notes', tiltcheck: 'Mental check', rules: 'Rules',
  settings: 'Settings', onboarding: 'Account', review: 'Match review', vodplayer: 'Recording',
  pregame: 'Before the match', ingame: 'Current match', manualentry: 'Add a match' };

// Map an iframe URL → the nav item id to light up. Pages share data-page with the
// rail's data-nav; the three objective sub-pages map to 'objectives'; manual entry
// is reached from Games (and is no longer a rail item) so it keeps Games lit;
// live/drill-in surfaces (pregame/ingame/review/vodplayer) map to nothing.
function fileToNav(pathname) {
  const file = (pathname || '').split('/').pop().toLowerCase() || 'dashboard.html';
  if (file === '' || file === 'index.html' || file === 'dashboard.html') return 'dashboard';
  if (file.startsWith('objective')) return 'objectives'; // objectives / objectivegames / objectivenotes
  if (['manualentry.html', 'review.html', 'vodplayer.html'].includes(file)) return 'games';
  if (['pregame.html', 'ingame.html'].includes(file)) return 'dashboard';
  return file.replace(/\.html$/, ''); // games.html → games, etc.
}

function markActive(navId) {
  if (!nav) return;
  nav.querySelectorAll('.nav-i').forEach((a) => {
    const active = a.dataset.nav === navId;
    a.classList.toggle('active', active);
    if (active) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  });
  const context = document.getElementById('appbar-context');
  if (context) context.textContent = pageLabels[navId] || 'Revu';
}

// Navigate the iframe (NOT the top document). Accepts a bare file or file?query.
let _frameNavigation = 0;
async function frameGoto(target) {
  if (!frame) return false;
  const navigation = ++_frameNavigation;
  // Resolve relative to the UI root so 'games.html' and 'games.html?x=1' both work.
  const url = new URL(target, frame.src || location.href);
  const source = frame.contentWindow;
  const sourceUrl = source?.location.href;
  const sourceDocument = source?.document;
  if (sourceUrl === url.href) return false; // also cancels an earlier pending navigation
  try {
    if (typeof source?.revuBeforeNavigate === 'function' && await source.revuBeforeNavigate() === false) return false;
  } catch (err) {
    console.warn('[shell] could not save the current page before navigation:', err);
    return false;
  }
  // A later click or an in-page navigation wins while a save is in flight.
  if (navigation !== _frameNavigation || frame.contentWindow !== source ||
      source?.document !== sourceDocument || source?.location.href !== sourceUrl) return false;
  frame.src = url.pathname.split('/').pop() + url.search + url.hash;
  markActive(fileToNav(url.pathname));
  return true;
}

// Current iframe file (best-effort; same-origin in-app so this is readable).
function frameFile() {
  try { return frame.contentWindow.location.pathname.split('/').pop() || 'dashboard.html'; }
  catch (_) { try { return new URL(frame.src, location.href).pathname.split('/').pop(); } catch (e) { return ''; } }
}
function frameHas(file) { return frameFile().toLowerCase() === file.toLowerCase(); }

// ── Nav clicks → reload only the iframe ──────────────────────────────────────
// One delegated handler on the persistent rail. preventDefault the anchor's own
// top-level navigation and point the iframe at it after pending page writes.
// The rail changes selection once navigation succeeds; iframe load also syncs
// the selection for in-page navigations.
nav?.addEventListener('click', (ev) => {
  const a = ev.target.closest('.nav-i');
  if (!a) return;
  ev.preventDefault();
  const href = a.getAttribute('href');
  if (!href) return;
  frameGoto(href);
});

// ── Sync active item whenever the iframe finishes loading a page ─────────────
// Covers in-page navigations (a page doing location.href=... inside the iframe,
// e.g. open_review → review.html) so the rail reflects wherever the iframe landed.
frame?.addEventListener('load', () => {
  try {
    const pathname = frame.contentWindow.location.pathname;
    markActive(fileToNav(pathname));
    const page = pathname.split('/').pop().replace(/\.html$/, '');
    const context = document.getElementById('appbar-context');
    if (context && pageLabels[page]) context.textContent = pageLabels[page];
    frame.title = pageLabels[page] || pageLabels[fileToNav(pathname)] || 'Revu';
  }
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

// ── Title-bar version ─────────────────────────────────────────────────────────
// Populate the branded strip's version (REVU <version>) from the app package info.
// Best-effort: in browser preview (no Electron) the version chip just stays blank.
async function fillAppVersion() {
  const invoke = await getInvoke();
  const el = document.getElementById('appbar-ver');
  if (!invoke || !el) return;
  try { const v = await invoke('app_version'); if (v) el.textContent = String(v); } catch (_) { /* leave blank */ }
}
fillAppVersion();

// ── Auto-update (Velopack) ───────────────────────────────────────────────────
// On launch, ask the sidecar if a newer release exists; if so, show the banner.
// The button downloads + applies (the app relaunches into the new version). The
// check is best-effort: a dev run / offline / failed check just leaves it hidden.
// Settings drives its own manual check via the same commands (settings.js).


let _updateAvailable = false;
function showUpdateBanner(version) {
  const bar = document.getElementById('updbar');
  const txt = document.getElementById('updbar-txt');
  if (!bar) return;
  if (txt) txt.textContent = version
    ? `Revu ${version} is available.`
    : 'A new version of Revu is available.';
  bar.hidden = false;
  document.body.classList.add('has-updbar');
}
function hideUpdateBanner() {
  const bar = document.getElementById('updbar');
  if (bar) bar.hidden = true;
  document.body.classList.remove('has-updbar');
}

async function runUpdateAndRestart() {
  const invoke = await getInvoke();
  if (!invoke) return;
  const btn = document.getElementById('updbar-btn');
  const txt = document.getElementById('updbar-txt');
  if (btn) btn.disabled = true;
  try {
    if (txt) txt.textContent = 'Downloading update…';
    const d = await invoke('download_update');
    if (!d || d.ok === false) {
      if (txt) txt.textContent = (d && d.message) ? d.message : 'Download failed.';
      if (btn) btn.disabled = false;
      return;
    }
    if (txt) txt.textContent = 'Installing… the app will restart.';
    // apply_update relaunches the app — this call won't return on success.
    await invoke('apply_update');
  } catch (err) {
    if (txt) txt.textContent = 'Update failed. Try again later.';
    if (btn) btn.disabled = false;
    console.error('[shell] update failed:', err);
  }
}

async function checkForUpdateOnLaunch() {
  const invoke = await getInvoke();
  if (!invoke) return; // preview / no backend
  try {
    const r = await invoke('check_update');
    if (r && r.ok && r.available) {
      _updateAvailable = true;
      showUpdateBanner(r.newVersion);
    }
  } catch (_) { /* sidecar not ready / offline — silent, retried on next launch */ }
}

// Wire the banner buttons (delegated; the banner is static markup in the shell).
document.getElementById('updbar-btn')?.addEventListener('click', runUpdateAndRestart);
document.getElementById('updbar-x')?.addEventListener('click', hideUpdateBanner);

// Defer the check a touch so the sidecar handshake is up first.
setTimeout(checkForUpdateOnLaunch, 2500);

// ── LCU (League Client) connection indicator ─────────────────────────────────
// Toggles the appbar's LCU chip between connected (green dot) and not. Called from
// the SSE listener below: seeded by the replayed liveState, updated live by the
// lcuConnection event. Pure DOM toggle — safe to call before the stream is wired.
function setLcuIndicator(connected) {
  const el = document.getElementById('appbar-lcu');
  if (!el) return;
  el.classList.toggle('on', !!connected);
  el.title = connected ? 'League Client: connected' : 'League Client: not connected';
  const text = el.querySelector('.appbar-lcu-txt');
  if (text) text.textContent = connected ? 'League connected' : 'League offline';
}

// ── Custom window controls (frameless title bar) ─────────────────────────────
// The window is decorations:false, so the appbar's min/max/close buttons drive the
// native window through the shared platform API.
// In browser preview these are absent → the buttons no-op silently.
async function wireWindowControls() {
  const win = await getWindow();
  if (!win) return; // browser preview
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
// ── HARD STOP (v3.7) ─────────────────────────────────────────────────────────
// The lock screen + slim lock bar for an ENFORCED rule the sidecar just acted on
// (it cancelled the League client's queue). Driven by the 'hardStop' SSE event
// (fresh enforcement → modal, window brought to the front) and by the liveState
// replay (reload mid-lockout → bar only, so the lock survives navigation).
//
// Deliberately NOT a form. The player set the rule while calm; this surface only
// shows them the plan they wrote for this moment and a countdown. OK collapses it
// to the bar. "Queue anyway" is the one escape hatch and it only unlocks after a
// five-minute hold. An override is logged (override_hard_stop) and
// silences that rule for the rest of the day; the Rules page counts it.
// Every server string goes through textContent.
const HARDSTOP_HOLD_S = 5 * 60;
const hardStop = { snap: null, holdStartedAt: 0, timer: null };
const hsEl = (id) => document.getElementById(id);

function hardStopHolds(s) {
  if (!s) return false;
  const unlock = Number(s.unlockAt);
  return !(unlock > 0) || unlock * 1000 > Date.now();
}

function hardStopCountText(s) {
  const unlock = Number(s && s.unlockAt);
  if (!(unlock > 0)) return 'Holds for the rest of today.';
  const ms = unlock * 1000 - Date.now();
  if (ms <= 0) return '';
  const totalMin = Math.ceil(ms / 60000);
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  const at = new Date(unlock * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  const span = h > 0 ? `${h}h ${m}m` : `${m}m`;
  return `Unlocks in ${span} · at ${at}`;
}



// A fresh enforcement is the one moment the app is allowed to take the screen:
// the player is looking at the League client, which just dropped them out of
// queue with no explanation. Bring Revu forward so the reason is in view.
async function bringShellForward() {
  try {
    const win = await getWindow();
    if (!win) return;
    try { await win.unminimize(); } catch (_) { /* not minimized */ }
    try { await win.show(); } catch (_) { /* already shown */ }
    try { await win.setFocus(); } catch (_) { /* focus denied by the OS — the bar still shows */ }
  } catch (_) { /* preview — no window API */ }
}

function fillHardStop(s) {
  const name = hsEl('hardstop-rule');
  const reason = hsEl('hardstop-reason');
  const plan = hsEl('hardstop-plan');
  const planT = hsEl('hardstop-plan-t');
  if (name) name.textContent = s.ruleName || 'Rule';
  if (reason) reason.textContent = s.reason || s.conditionCue || '';
  if (plan && planT) {
    if (s.hasPlan && s.replacementPlan) { planT.textContent = s.replacementPlan; plan.hidden = false; }
    else plan.hidden = true;
  }
  const h = hsEl('hardstop-h');
  if (h) {
    h.textContent = s.action === 'declined_ready_check'
      ? 'Revu declined the match.'
      : 'Revu cancelled your queue.';
  }
  tickHardStop();
}

function tickHardStop() {
  const s = hardStop.snap;
  if (!s) return;
  if (!hardStopHolds(s)) { clearHardStop(); return; }

  const count = hsEl('hardstop-count');
  const text = hardStopCountText(s);
  if (count) { count.textContent = text; count.hidden = !text; }
  const barTxt = hsEl('hardbar-txt');
  if (barTxt) barTxt.textContent = text ? `${s.ruleName || 'Hard stop'} · ${text}` : (s.ruleName || 'Hard stop');

  // The override hold: counts down from the moment THIS enforcement first showed.
  const btn = hsEl('hardstop-override');
  if (btn) {
    const held = Math.floor((Date.now() - hardStop.holdStartedAt) / 1000);
    const left = HARDSTOP_HOLD_S - held;
    if (left > 0) {
      btn.disabled = true;
      btn.textContent = `Queue anyway (${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')})`;
    }
    else { btn.disabled = false; btn.textContent = 'Queue anyway'; }
  }
}

function startHardStopTimer() {
  if (hardStop.timer) return;
  hardStop.timer = setInterval(tickHardStop, 1000);
}
function stopHardStopTimer() {
  if (hardStop.timer) { clearInterval(hardStop.timer); hardStop.timer = null; }
}

// fresh=true: a new enforcement just happened (modal + bring forward, new hold).
// fresh=false: replayed from liveState — bar only, no window grab, no hold reset.
function showHardStop(snap, fresh) {
  if (!snap || !hardStopHolds(snap)) return;
  const isNew = !hardStop.snap || hardStop.snap.at !== snap.at || hardStop.snap.ruleId !== snap.ruleId;
  hardStop.snap = snap;
  if (fresh || isNew) hardStop.holdStartedAt = Date.now();
  fillHardStop(snap);
  const modal = hsEl('hardstop');
  const bar = hsEl('hardbar');
  if (fresh) {
    if (modal) modal.hidden = false;
    if (bar) bar.hidden = true;
    bringShellForward();
    const ok = hsEl('hardstop-ok');
    if (ok) { try { ok.focus(); } catch (_) { /* ignore */ } }
  } else {
    if (modal && modal.hidden) { if (bar) bar.hidden = false; }
  }
  startHardStopTimer();
}

function collapseHardStop() {
  const modal = hsEl('hardstop');
  const bar = hsEl('hardbar');
  if (modal) modal.hidden = true;
  if (bar) bar.hidden = !hardStop.snap;
}

function expandHardStop() {
  if (!hardStop.snap) return;
  fillHardStop(hardStop.snap);
  const modal = hsEl('hardstop');
  const bar = hsEl('hardbar');
  if (modal) modal.hidden = false;
  if (bar) bar.hidden = true;
}

function clearHardStop() {
  hardStop.snap = null;
  stopHardStopTimer();
  const modal = hsEl('hardstop');
  const bar = hsEl('hardbar');
  if (modal) modal.hidden = true;
  if (bar) bar.hidden = true;
}

async function overrideHardStop() {
  const s = hardStop.snap;
  if (!s) { clearHardStop(); return; }
  const btn = hsEl('hardstop-override');
  if (btn && btn.disabled) return; // hold not over
  const invoke = await getInvoke();
  if (!invoke) { clearHardStop(); return; } // preview
  if (btn) btn.disabled = true;
  try {
    await invoke('override_hard_stop', { payload: { ruleId: Number(s.ruleId) } });
    clearHardStop();
  } catch (err) {
    console.error('[shell] override_hard_stop failed:', err);
    if (btn) btn.disabled = false;
  }
}

function wireHardStop() {
  const ok = hsEl('hardstop-ok');
  const more = hsEl('hardbar-btn');
  const over = hsEl('hardstop-override');
  if (ok) ok.addEventListener('click', collapseHardStop);
  if (more) more.addEventListener('click', expandHardStop);
  if (over) over.addEventListener('click', () => { overrideHardStop(); });
  document.addEventListener('keydown', (ev) => {
    const modal = hsEl('hardstop');
    if (ev.key === 'Escape' && modal && !modal.hidden) { ev.preventDefault(); collapseHardStop(); }
  });
}
wireHardStop();

async function wireLiveAutoShow() {
  const invoke = await getInvoke();

  const listen = await getListen();

  if (!invoke || !listen) return;

  const goto = (file) => { if (!frameHas(file)) frameGoto(file); };
  const leaveLiveSurface = () => { if (frameHas('pregame.html') || frameHas('ingame.html')) frameGoto('dashboard.html'); };
  const handleTutorialGameEnded = async (payload) => {
    const gameId = Number(payload?.gameId);
    if (!payload?.saved || !Number.isSafeInteger(gameId) || gameId <= 0) return false;
    try {
      const cfg = await invoke('get_config');
      const activeTutorial = cfg
        && cfg.firstReviewTutorialStep
        && !cfg.firstReviewTutorialCompleted
        && !cfg.firstReviewTutorialDismissed;
      if (!activeTutorial) return false;
      await invoke('save_config', {
        payload: {
          firstReviewTutorialStep: 'wait_vod',
          firstReviewTutorialGameId: gameId,
        },
      });
      // Optional help updates in place. It never takes over navigation or
      // reloads an editor; the ordinary live-surface exit remains in charge.
      try {
        const guide = frame?.contentWindow?.RevuFirstReviewTutorial;
        if (typeof guide?.render === 'function') void Promise.resolve(guide.render()).catch(() => {});
      } catch (_) { /* A navigation in progress will load the saved step itself. */ }
      return false;
    } catch (err) {
      console.warn('[shell] first review tutorial game-ended handoff failed:', err);
      return false;
    }
  };

  // Pages where the user is actively ENTERING DATA — auto-show must NOT yank them
  // off these mid-task (an LCU champ-select/game tick reloading the iframe wipes the
  // unsaved form, the P-035 regression). Mirrors shell.js:125-133; this is the path
  // that actually runs in-app (shell.js's copy is gated !FRAMED, off for the iframe).
  // onboarding (email/OTP entry), rules (create/edit form) and patterns
  // (moment-note autosave) are form-bearing too — a champ-select tick reloading
  // the iframe from any of them wiped half-typed OTP codes / rule text. matchups
  // (card form + inline prior/observed autosave) is the page whose whole point
  // is writing a prior WHILE queuing — champ select must never reload it.
  const ACTIVE_WORK_PAGES = ['review.html', 'objectives.html', 'manualentry.html', 'settings.html', 'vodplayer.html', 'onboarding.html', 'rules.html', 'patterns.html', 'matchups.html'];
  const onActiveWorkPage = () => ACTIVE_WORK_PAGES.some((f) => frameHas(f));
  // Some pages aren't whole-page forms but still have a transient mid-edit state —
  // the dashboard's inline Start/End-Block editors. Those set window.__revuActiveWork
  // on the framed document while open; reloading the iframe then would wipe the
  // half-typed focus (the "Start Block refreshed the page and didn't save" report).
  const inActiveEdit = () => {
    try { return !!frame?.contentWindow?.__revuActiveWork; }
    catch (_) { return false; } // cross-origin (shouldn't happen in-app) — treat as idle
  };
  // Auto-show to a live surface, but never interrupt an active-work page or an
  // open inline editor — UNLESS we're already on a live surface (pregame↔ingame
  // switches are always honored).
  const liveGoto = (file) => {
    if (frameHas('pregame.html') || frameHas('ingame.html')) { goto(file); return; }
    if (onActiveWorkPage() || inActiveEdit()) return; // don't yank the user off a form / open editor
    goto(file);
  };

  let sawFirstLiveState = false;
  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    const t = msg.type;
    const p = msg.payload || {};
    switch (t) {
      case 'champSelectStarted': liveGoto('pregame.html'); break;
      case 'gameInProgress': liveGoto('ingame.html'); break;
      case 'gameEnded':
        handleTutorialGameEnded(p).then((handled) => {
          if (!handled) leaveLiveSurface();
        });
        break;
      case 'champSelectCancelled': leaveLiveSurface(); break;
      case 'vodLinked':
        // Recording discovery never navigates or reloads a working page.
        try {
          if ((frameHas('vodplayer.html') || frameHas('review.html') || frameHas('games.html')) && frame?.contentWindow) {
            frame.contentWindow.dispatchEvent(new CustomEvent('revu:vod-linked', { detail: p }));
          }
        } catch (_) { /* a fresh navigation reads the current link itself */ }
        break;
      case 'mapStateUpdated':
        // Post-game map-state pass finished (jungle proximity + fog deaths written).
        // If the framed page is the VOD player it may be SHOWING that game already —
        // hand the event in so it can soft-refresh its timeline markers (same-origin
        // iframe; the player checks the gameId itself and never touches playback).
        try {
          if (frameHas('vodplayer.html') && frame?.contentWindow) {
            frame.contentWindow.dispatchEvent(new CustomEvent('revu:map-state-updated', { detail: p }));
          }
        } catch (_) { /* best-effort — a fresh navigation always fetches fresh */ }
        break;
      case 'matchupUpdated':
        // v3.10: the post-game matchup pass filled enemy_laner / participant_map
        // from Match-V5 (~90s after EOG). The Review page may be open on that very
        // game with its hero reading a bare champion name, and the Matchups journal
        // may be showing "Couldn't tell the lane…" — hand the event in so each can
        // refresh just its header / last-game line (never the user's unsaved text).
        try {
          if ((frameHas('review.html') || frameHas('matchups.html')) && frame?.contentWindow) {
            frame.contentWindow.dispatchEvent(new CustomEvent('revu:matchup-updated', { detail: p }));
          }
        } catch (_) { /* best-effort */ }
        break;
      case 'eventsCorrected':
        // v3.11: a timeline correction landed (VOD panel, review death links, or the
        // legacy encounter form). Every page that draws game events refetches; each
        // listener gates on gameId and never touches playback or unsaved text.
        try {
          if ((frameHas('vodplayer.html') || frameHas('review.html') || frameHas('patterns.html')) && frame?.contentWindow) {
            frame.contentWindow.dispatchEvent(new CustomEvent('revu:events-corrected', { detail: p }));
          }
        } catch (_) { /* best-effort */ }
        break;
      case 'liveState':
        // The replayed snapshot carries the current client state — seed the LCU
        // indicator from it (live changes arrive via 'lcuConnection' below).
        setLcuIndicator(!!p.lcuConnected);
        // v3.7: a lock that still holds comes back as the slim bar (no window
        // grab — the player may be mid-navigation); a null replay means the
        // sidecar cleared it (game started / override), so drop ours too.
        if (p.hardStop && hardStopHolds(p.hardStop)) showHardStop(p.hardStop, false);
        else if (hardStop.snap) clearHardStop();
        if (!sawFirstLiveState) {
          sawFirstLiveState = true;
          if (p.isGameInProgress) liveGoto('ingame.html');
          else if (p.sessionKey) liveGoto('pregame.html');
        } else if (!p.isGameInProgress && !p.sessionKey) {
          // Replays fire on every SSE (re)connect. If the connection dropped
          // across the end of a game, the 'gameEnded' event is gone forever and
          // the shell would sit on In Game indefinitely (P-041 field report:
          // "i finished the game and this still shows"). The replayed state is
          // authoritative: no game, no champ select → leave the live surface.
          leaveLiveSurface();
        }
        break;
      // v3.7 hard stop: a fresh enforcement takes the screen; an override
      // (from any surface) clears it.
      case 'hardStop': showHardStop(p, true); break;
      case 'hardStopOverridden': clearHardStop(); break;
      case 'lcuConnection': setLcuIndicator(!!p.connected); break;
      default: break;
    }
  });
  try { await invoke('start_lcu_events'); } catch (_) { /* sidecar may still be starting */ }
}
wireLiveAutoShow();
