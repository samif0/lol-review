// A minimal startup window, shown while the sidecar starts or prepares data, so a
// cold start never looks like a click that did nothing. Static markup in its own
// in-memory session; no script, no preload, no network.

export const SPLASH_TEXT = Object.freeze({
  starting: 'Starting Revu…',
  preparing: 'Updating your Revu data. This can take a few minutes on a large match library. Revu opens when it is done.',
});

const escapeHtml = text => String(text).replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);

export function splashPage(status) {
  return 'data:text/html;charset=utf-8,' + encodeURIComponent('<!doctype html><html lang="en"><head><meta charset="utf-8">'
    + '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'">'
    + '<title>Revu</title><style>html,body{margin:0;height:100%;background:#0d101b;color:#e8eaf2;'
    + 'font:14px/1.45 "Segoe UI",system-ui,sans-serif;-webkit-app-region:drag;user-select:none;cursor:default}'
    + 'main{height:100%;box-sizing:border-box;padding:24px;display:flex;flex-direction:column;align-items:center;'
    + 'justify-content:center;gap:10px;text-align:center}h1{margin:0;font-size:26px;font-weight:600}'
    + `p{margin:0;color:#a3abc0;max-width:320px}</style></head><body><main><h1>Revu</h1><p role="status">${escapeHtml(status)}</p>`
    + '</main></body></html>');
}

export function createSplash({ BrowserWindow, iconPath }) {
  let window = null, status = SPLASH_TEXT.starting, preparing = false;
  const alive = () => !!window && !window.isDestroyed();
  const render = () => { if (alive()) window.loadURL(splashPage(status)).catch(() => {}); };
  return {
    show() {
      if (alive()) { if (window.isMinimized()) window.restore(); window.show(); window.focus(); return; }
      window = new BrowserWindow({ width: 400, height: 220, frame: false, resizable: false, maximizable: false,
        fullscreenable: false, show: false, center: true, title: 'Revu', icon: iconPath, backgroundColor: '#0d101b',
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
