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
