// Crash and relaunch decisions for the main process. Pure where possible so the
// policy is testable without Electron; main.mjs supplies the dialog and the app.

export const RECOVERY_BUTTONS = Object.freeze(['Restart Revu', 'Open logs', 'Quit']);

// User-facing copy: sentence case, no em dashes.
const RECOVERY_COPY = Object.freeze({
  sidecar: { message: 'Revu stopped working',
    detail: "Revu's background service closed unexpectedly. Restart Revu to continue. Changes you had not saved may be lost." },
  renderer: { message: "Revu's window stopped working",
    detail: 'The window closed unexpectedly again. Restart Revu to continue. Changes you had not saved may be lost.' },
  startup: { message: 'Revu could not start', detail: 'Restart Revu to try again, or open the logs to see what happened.' },
});

/** A login launch must not make a reset/restore/crash relaunch start hidden in the tray. */
export function relaunchArgs(argv) {
  return argv.slice(1).filter(arg => arg !== '--revu-startup');
}

/**
 * Windows ending the session (logoff, restart, shutdown) also ends the backend. Watch
 * the window's session-end notices so that exit is not taken for a crash. Returns a
 * getter: true while Windows is ending the session. 'query-session-end' only asks, and
 * another app can still cancel the logoff, so it counts for `queryWindowMs`; Windows
 * ends the processes well within that when the logoff goes ahead. 'session-end' fires
 * once the end is certain and counts from then on. (powerMonitor's 'shutdown' event
 * never fires on Windows, so it is not watched.)
 */
export function watchSessionEnd({ window, now = Date.now, queryWindowMs = 60_000 }) {
  let ended = false, queriedAt = -Infinity;
  window?.on('query-session-end', () => { queriedAt = now(); });
  window?.on('session-end', () => { ended = true; });
  return () => ended || now() - queriedAt < queryWindowMs;
}

/** A backend exit: ignored while Revu closes, a quiet close while Windows ends the session, otherwise a crash. */
export function backendExitAction({ quitting, sessionEnding }) {
  if (quitting) return 'ignore';
  return sessionEnding ? 'quit' : 'recover';
}

/**
 * The host needs only the window size from the settings at launch. A briefly locked
 * config.json is read again after a short pause; if it stays unreadable Revu still
 * opens at the default size, and each page reports the problem when it reads its
 * settings. Nothing is written here, so the file is never replaced with defaults.
 */
export async function readLaunchConfig(request, { delaysMs = [250, 500, 1000], log,
  wait = ms => new Promise(resolve => setTimeout(resolve, ms)) } = {}) {
  for (let attempt = 0; ; attempt++) {
    try { return await request('/api/config') || {}; }
    catch (error) {
      // Only a backend that answered it could not read the file is asked again. A request
      // that timed out, was cancelled or found no backend would only hold the splash longer.
      if (attempt >= delaysMs.length || !(error?.status >= 500)) {
        log?.warn('Settings could not be read at launch; opening at the default window size', error);
        return {};
      }
      await wait(delaysMs[attempt]);
    }
  }
}

/** The first renderer crash reloads the window; another within the window asks the user. */
export function createRendererRecovery({ now = Date.now, windowMs = 5 * 60_000 } = {}) {
  let lastReload = -Infinity;
  return () => {
    const time = now();
    if (time - lastReload < windowMs) return 'ask';
    lastReload = time;
    return 'reload';
  };
}

/** The shell's current iframe page (review.html?gameId=5), restorable through its hash. */
export function shellPage(value) {
  try {
    const url = new URL(value);
    const page = url.pathname.replace(/^\//, '');
    if (url.protocol !== 'revu-app:' || url.host !== 'ui' || url.username || url.password
      || !/^[A-Za-z0-9-]+\.html$/.test(page) || page === 'index.html') return '';
    return page + url.search;
  } catch { return ''; }
}

export function shellUrl(origin, page) {
  return page ? `${origin}/index.html#${page}` : `${origin}/index.html`;
}

/**
 * Ask how to recover. Opening the logs keeps the choice open; only Restart or Quit
 * ends it. Resolves 'restart' or 'quit'.
 */
export async function askRecovery({ dialog, parent, kind, detail, openLogs, maxPrompts = 10 }) {
  const copy = RECOVERY_COPY[kind];
  const options = { type: 'error', title: 'Revu', message: copy.message,
    detail: detail ? `${detail}\n\n${copy.detail}` : copy.detail,
    buttons: [...RECOVERY_BUTTONS], defaultId: 0, cancelId: 2, noLink: true };
  for (let prompt = 0; prompt < maxPrompts; prompt++) {
    const { response } = parent ? await dialog.showMessageBox(parent, options) : await dialog.showMessageBox(options);
    if (response !== 1) return response === 0 ? 'restart' : 'quit';
    try { await openLogs(); } catch { /* The folder may be unavailable; offer the choice again. */ }
  }
  return 'quit';
}

export const SLOW_START_BUTTONS = Object.freeze(['Keep waiting', 'Open logs', 'Quit']);

/**
 * A data upgrade that runs long offers a way out. Opening the logs keeps the choice open;
 * Escape or an aborted `signal` (startup ended) means keep waiting. Resolves 'wait' or 'quit'.
 */
export async function askSlowStart({ dialog, signal, openLogs, maxPrompts = 10 }) {
  const options = { type: 'warning', title: 'Revu', message: 'Revu is taking longer than expected to start',
    detail: 'Revu is still updating your data. You can keep waiting, or quit and open Revu again later. '
      + 'If you quit, Revu finishes the update the next time it opens.',
    buttons: [...SLOW_START_BUTTONS], defaultId: 0, cancelId: 0, noLink: true, signal };
  for (let prompt = 0; prompt < maxPrompts && !signal?.aborted; prompt++) {
    const { response } = await dialog.showMessageBox(options);
    if (signal?.aborted) return 'wait';
    if (response !== 1) return response === 2 ? 'quit' : 'wait';
    try { await openLogs(); } catch { /* The folder may be unavailable; offer the choice again. */ }
  }
  return 'wait';
}

/** A relaunch during the shutdown drain gets one notice instead of a silent no-op. */
export function createClosingNotice(dialog) {
  let shown = false;
  return () => {
    if (shown) return;
    shown = true;
    void Promise.resolve().then(() => dialog.showMessageBox({ type: 'info', title: 'Revu', message: 'Revu is still closing',
      detail: 'Revu is finishing a task and will close shortly. Open it again in a moment.', buttons: ['OK'], noLink: true }))
      .catch(() => {});
  };
}
