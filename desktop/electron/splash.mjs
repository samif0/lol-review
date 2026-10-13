// A minimal startup window, shown while the sidecar starts or prepares data, so a
// cold start never looks like a click that did nothing. Static markup in its own
// in-memory session; no script, no preload, no network. It opens in the colors the
// shell last reported (electron/appearance.mjs), or the app default before there are any.
import { splashPalettes, validateWindowAppearance, windowPalette } from './appearance.mjs';

export const SPLASH_TEXT = Object.freeze({
  starting: 'Starting Revu…',
  preparing: 'Updating your Revu data. This can take a few minutes on a large match library. Revu opens when it is done.',
});

const escapeHtml = text => String(text).replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);

// Only validated #rrggbb values reach the stylesheet; anything else means the defaults.
const appearanceOf = value => { try { return value ? validateWindowAppearance(value) : null; } catch { return null; } };
const colors = p => `html,body{background:${p.background};color:${p.text}}p{color:${p.muted}}`;

export function splashPage(status, appearance = null) {
  const { base, light } = splashPalettes(appearanceOf(appearance));
  return 'data:text/html;charset=utf-8,' + encodeURIComponent('<!doctype html><html lang="en"><head><meta charset="utf-8">'
    + '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'">'
    + '<title>Revu</title><style>html,body{margin:0;height:100%;'
    + 'font:14px/1.45 "Segoe UI",system-ui,sans-serif;-webkit-app-region:drag;user-select:none;cursor:default}'
    + 'main{height:100%;box-sizing:border-box;padding:24px;display:flex;flex-direction:column;align-items:center;'
    + 'justify-content:center;gap:10px;text-align:center}h1{margin:0;font-size:26px;font-weight:600}'
    + `p{margin:0;max-width:320px}${colors(base)}${light ? `@media (prefers-color-scheme:light){${colors(light)}}` : ''}`
    + `</style></head><body><main><h1>Revu</h1><p role="status">${escapeHtml(status)}</p>`
    + '</main></body></html>');
}

// `dark` is the current Windows app mode (nativeTheme.shouldUseDarkColors), only to
// match the native backdrop to what the page will paint.
export function createSplash({ BrowserWindow, iconPath, appearance = null, dark = true }) {
  let window = null, status = SPLASH_TEXT.starting, preparing = false;
  const saved = appearanceOf(appearance);
  // Nothing saved yet: the app default, the same color the main window and the pages open in.
  const backgroundColor = windowPalette(saved, { dark }).background;
  const alive = () => !!window && !window.isDestroyed();
  const render = () => { if (alive()) window.loadURL(splashPage(status, saved)).catch(() => {}); };
  return {
    show() {
      if (alive()) { if (window.isMinimized()) window.restore(); window.show(); window.focus(); return; }
      window = new BrowserWindow({ width: 400, height: 220, frame: false, resizable: false, maximizable: false,
        fullscreenable: false, show: false, center: true, title: 'Revu', icon: iconPath, backgroundColor,
        webPreferences: { partition: 'revu-splash', sandbox: true, contextIsolation: true, nodeIntegration: false,
          javascript: false, devTools: false, spellcheck: false } });
      window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
      window.webContents.on('will-navigate', event => event.preventDefault());
      // Closing while data is being prepared would stop the backend mid-update.
      window.on('close', event => { if (preparing) event.preventDefault(); });
      window.once('ready-to-show', () => { if (alive()) window.show(); });
      render();
    },
    preparing() {
      preparing = true; status = SPLASH_TEXT.preparing; render();
    },
    close() {
      preparing = false;
      if (alive()) window.destroy();
      window = null;
    },
  };
}

/**
 * What the host shows while the backend upgrades data. A login launch stays hidden in the
 * tray. Any launch still upgrading after slowAfterMs surfaces and asks (askSlow resolves
 * 'wait' or 'quit'), so a stuck upgrade always offers a way out. finish() closes an open
 * question once startup ends either way.
 */
export function createStartupProgress({ splash, login = false, askSlow, slowAfterMs = 3 * 60_000 }) {
  let timer = null, finished = false;
  const question = new AbortController();
  const arm = () => {
    timer = setTimeout(async () => {
      timer = null;
      if (finished) return;
      splash.show();
      let choice = 'wait';
      try { choice = await askSlow(question.signal); } catch { /* keep waiting; the backend bound still applies */ }
      if (choice === 'wait' && !finished) arm();
    }, slowAfterMs);
  };
  return {
    preparing() {
      if (finished) return;
      splash.preparing();
      if (!login) splash.show();
      if (!timer) arm();
    },
    finish() {
      finished = true;
      clearTimeout(timer);
      timer = null;
      question.abort();
    },
  };
}
