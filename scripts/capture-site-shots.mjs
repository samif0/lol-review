// Regenerates the revu.lol product screenshots and og-image.jpg from the desktop app's
// sample data. It starts the desktop Vite dev server (or uses --base), opens each page
// in headless Edge (or Chrome) over the DevTools protocol in the Neutral dark theme,
// and writes 1600x1000 site/shot-<name>.webp files plus site/og-image.jpg. Outside the
// desktop host the pages read desktop/ui/sample-*.json, so no database is involved, and
// a capture aborts before writing if a personal string renders. Usage (repository root):
//   node scripts/capture-site-shots.mjs [--base <dev server url>] [--browser <exe>]
//     [--out <dir>] [--only home,review,og] [--list]
// Running it is a manual step; look at every image before committing it.
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { jpegSize, webpSize } from './image-size.mjs';

export const repositoryRoot = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
const desktopRoot = path.join(repositoryRoot, 'desktop');
const BROWSERS = ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe'];
// The review fixture's own game, so the highlighted sidebar row matches its header.
const GAME = '5581701721';
// Pages see a fixed evening on 2026-06-14, after the fixtures' latest game (20:42), so
// the time of day and every relative date render the same on every run.
const FIXED_NOW = '2026-06-14T21:00:00';
export const VIEWPORT = { width: 1600, height: 1000 };
export const OG_SIZE = { width: 1200, height: 630 };
// Home is the whole window. A feature shot is the 800x500 part of a page `top` px down,
// at deviceScaleFactor 2: still 1600x1000, and legible at the landing's tile size. The
// window is the sidebar plus an 800 px page; captureShot checks that width.
export const PANE = { width: 800, height: 500 };
const SIDEBAR = 256;

// d is the framed page's document; a shot waits for its ready predicate.
export const SHOTS = [
  { name: 'home', page: 'dashboard.html',
    ready: "d.getElementById('hero-title')?.textContent !== 'Welcome back' && d.querySelectorAll('#unreviewed > *').length > 0" },
  { name: 'review', page: `review.html?gameId=${GAME}`, top: 64, ready: "d.getElementById('rv-body')?.hidden === false" },
  { name: 'objectives', page: 'objectives.html', top: 190, ready: "d.querySelectorAll('#active-list > *').length > 0" },
  { name: 'patterns', page: 'patterns.html', top: 270, ready: "d.getElementById('pat-main')?.hidden === false" },
];

/** The site file one shot writes. */
export const shotFile = name => `shot-${name}.webp`;

// The owner's Riot name and old coaching stint, an email address, or a Riot ID
// (name#TAG). Any of them in the rendered text aborts the capture.
export const PERSONAL_TEXT = /\bbye\b|\bviolet\b|\bchapy\b|[\w.+-]+@[\w-]+\.[a-z]{2,}|[^\s#]#[a-z0-9]{2,5}\b/i;

const MARK = '<path d="M28 31.9L16 24V76L28 68.1" fill="none" stroke="currentColor" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>'
  + '<path d="M40 22.5v55a4 4 0 0 0 6.2 3.3l40.6-27.5a4 4 0 0 0 0-6.6L46.2 19.2A4 4 0 0 0 40 22.5Z" fill="currentColor"/>';

/** The 1200x630 link preview; font and shot are data: URLs. Literals mirror site/tokens.css. */
export function ogTemplate(fontUrl, shotUrl) {
  return `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:'Geist';src:url('${fontUrl}') format('woff2');font-weight:400 700}
html,body{margin:0;width:1200px;height:630px;overflow:hidden;background:#1c1d1f;color:#f3f4f6;font-family:'Geist',sans-serif}
.copy{position:absolute;left:72px;top:76px;width:440px}
.brand{display:flex;align-items:center;gap:12px;font-size:34px;font-weight:600;letter-spacing:-0.025em}
.brand svg{height:30px;width:auto}
h1{margin:104px 0 0;font-size:54px;font-weight:500;line-height:1.08;letter-spacing:-0.025em}
p{margin:24px 0 0;color:#979ba3;font-size:22px}
.shot{position:absolute;left:580px;top:104px;width:800px;height:500px;border:1px solid rgba(255,255,255,.17);border-radius:14px;overflow:hidden;background:#25272a}
.shot img{display:block;width:800px;height:500px}
</style></head><body>
<div class="copy"><div class="brand"><svg viewBox="12 18 77 64" aria-hidden="true">${MARK}</svg>Revu</div>
<h1>Review every ranked game.</h1><p>Free for Windows 10 and 11</p></div>
<div class="shot"><img src="${shotUrl}" alt=""></div></body></html>`;
}

// ── Dev server and browser ────────────────────────────────────────────────────

async function startVite() {
  const require = createRequire(path.join(desktopRoot, 'package.json'));
  const { createServer } = await import(pathToFileURL(require.resolve('vite')).href);
  const server = await createServer({ configFile: path.join(desktopRoot, 'vite.config.js'), root: path.join(desktopRoot, 'ui'),
    logLevel: 'warn', clearScreen: false, server: { host: '127.0.0.1', port: 0, strictPort: true, open: false } });
  await server.listen();
  return { origin: `http://127.0.0.1:${server.httpServer.address().port}`, close: () => server.close() };
}

class Cdp {
  constructor(socket) {
    Object.assign(this, { socket, nextId: 0, pending: new Map(), listeners: new Set() });
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      const call = message.id && this.pending.get(message.id);
      if (!call) return message.method && this.listeners.forEach(listener => listener(message));
      this.pending.delete(message.id);
      if (message.error) call.reject(new Error(`${call.method}: ${message.error.message}`));
      else call.resolve(message.result);
    });
  }
  static async connect(url) {
    const socket = new WebSocket(url);
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, { once: true });
      socket.addEventListener('error', () => reject(new Error(`Could not connect to ${url}`)), { once: true });
    });
    return new Cdp(socket);
  }
  send(method, params = {}) {
    const id = ++this.nextId;
    this.socket.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject, method }));
  }
  /** The next `method` event's params; rejects after `timeoutMs`. */
  once(method, timeoutMs = 20000) {
    return new Promise((resolve, reject) => {
      const done = (settle, value) => { clearTimeout(timer); this.listeners.delete(listener); settle(value); };
      const listener = message => message.method === method && done(resolve, message.params);
      const timer = setTimeout(() => done(reject, new Error(`Timed out waiting for ${method}`)), timeoutMs);
      this.listeners.add(listener);
    });
  }
  close() { this.socket.close(); }
}

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function launchBrowser(executable) {
  const profile = await mkdtemp(path.join(os.tmpdir(), 'revu-site-shots-'));
  const child = spawn(executable, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-sync', '--disable-gpu',
    '--hide-scrollbars', '--mute-audio', '--force-color-profile=srgb', '--font-render-hinting=none', 'about:blank'],
  { stdio: 'ignore', windowsHide: true });
  // Edge may hand the launch to another process and exit 0, so the port file, not the
  // child, says when the browser is up; Browser.close then ends whichever one runs.
  const deadline = Date.now() + 20000;
  let endpoint = null;
  while (!endpoint && Date.now() < deadline && !(child.exitCode > 0)) {
    try {
      const [port, browserPath] = (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split(/\r?\n/);
      if (Number(port) && browserPath) endpoint = { origin: `127.0.0.1:${port}`, browserPath };
    } catch { /* not written yet */ }
    if (!endpoint) await delay(100);
  }
  let cdp = null;
  const close = async () => {
    cdp?.close();
    if (endpoint) {
      try {
        const browser = await Cdp.connect(`ws://${endpoint.origin}${endpoint.browserPath}`);
        await Promise.race([browser.send('Browser.close'), delay(3000)]);
        browser.close();
      } catch { /* already gone */ }
    }
    if (child.exitCode === null) child.kill();
    await delay(500);
    await rm(profile, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 })
      .catch(() => console.warn(`Could not remove the temporary browser profile ${profile}.`));
  };
  try {
    if (!endpoint) throw new Error(`${executable} did not start a DevTools endpoint.`);
    const page = (await (await fetch(`http://${endpoint.origin}/json/list`)).json()).find(target => target.type === 'page');
    if (!page) throw new Error('No page target in the headless browser.');
    cdp = await Cdp.connect(page.webSocketDebuggerUrl);
  } catch (error) {
    await close();
    throw error;
  }
  return { cdp, close };
}

// ── Capture ───────────────────────────────────────────────────────────────────

async function evaluate(cdp, expression) {
  const { result, exceptionDetails } = await cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (exceptionDetails) throw new Error(`Page script failed: ${exceptionDetails.exception?.description || exceptionDetails.text}`);
  return result.value;
}

async function waitFor(cdp, expression, what, timeoutMs = 20000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await evaluate(cdp, `(() => { try { return !!(${expression}); } catch { return false; } })()`)) return;
    await delay(100);
  }
  throw new Error(`Timed out waiting for ${what}`);
}

const FRAME = "document.getElementById('app-frame')?.contentDocument";
const settle = cdp => evaluate(cdp, 'new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 400))))');
// Installed before every document: Date reads a fixed evening that still advances.
const FIXED_CLOCK = `(() => { const R = Date, start = R.now(), fixed = new R(${JSON.stringify(FIXED_NOW)}).getTime();
  const now = () => fixed + (R.now() - start);
  class FixedDate extends R { constructor(...a) { super(...(a.length ? a : [now()])); } static now() { return now(); } }
  globalThis.Date = FixedDate; })();`;

async function capture(cdp, file, format, size, clip) {
  const { data } = await cdp.send('Page.captureScreenshot', { format, quality: 90, captureBeyondViewport: false, ...(clip && { clip }) });
  const bytes = Buffer.from(data, 'base64');
  const actual = format === 'webp' ? webpSize(bytes) : jpegSize(bytes);
  if (actual?.width !== size.width || actual?.height !== size.height) {
    throw new Error(`${path.basename(file)} is ${actual?.width}x${actual?.height}, expected ${size.width}x${size.height}`);
  }
  await writeFile(file, bytes);
  console.log(`Wrote ${path.relative(repositoryRoot, file)} (${Math.round(bytes.length / 1024)} KB)`);
  return bytes;
}

async function captureShot(cdp, origin, shot, out) {
  const pane = shot.top !== undefined;
  await cdp.send('Emulation.setDeviceMetricsOverride', pane
    ? { width: SIDEBAR + PANE.width, height: VIEWPORT.height, deviceScaleFactor: 2, mobile: false }
    : { ...VIEWPORT, deviceScaleFactor: 1, mobile: false });
  const loaded = cdp.once('Page.loadEventFired');
  // shot= keeps each URL distinct, so the shell always loads anew instead of a hash change.
  await cdp.send('Page.navigate', { url: `${origin}/index.html?shot=${shot.name}&appearance=neutral-dark#${shot.page}` });
  await loaded;
  const file = shot.page.split('?')[0];
  await waitFor(cdp, `${FRAME}?.location.pathname.endsWith('/${file}') && ${FRAME}.readyState === 'complete'`, `${shot.name}: ${file} to load`);
  await evaluate(cdp, `Promise.all([document.fonts.ready, ${FRAME}.fonts.ready]).then(() => true)`);
  await waitFor(cdp, `(d => ${shot.ready})(${FRAME})`, `${shot.name}: its content`);
  await settle(cdp);
  const text = await evaluate(cdp, `[document.body.innerText, ${FRAME}.body.innerText,
    ...[...document.querySelectorAll('input, textarea'), ...${FRAME}.querySelectorAll('input, textarea')].map(field => field.value)].join('\\n')`);
  const personal = PERSONAL_TEXT.exec(text);
  if (personal) throw new Error(`${shot.name}: personal text "${personal[0]}" is on screen; nothing was written for this shot.`);
  const frame = pane && await evaluate(cdp, "(({ left, top, width }) => ({ left, top, width }))(document.getElementById('app-frame').getBoundingClientRect())");
  const clip = pane ? { x: frame.left, y: frame.top + shot.top, ...PANE, scale: 1 } : undefined;
  if (pane && (frame.width !== PANE.width || clip.y + PANE.height > VIEWPORT.height)) {
    throw new Error(`${shot.name}: the page is ${frame.width} px wide; expected ${PANE.width} px with ${PANE.height} px of it below ${clip.y}.`);
  }
  return capture(cdp, path.join(out, shotFile(shot.name)), 'webp', VIEWPORT, clip);
}

async function captureOg(cdp, out, homeShot) {
  const font = await readFile(path.join(desktopRoot, 'ui', 'fonts', 'geist.woff2'));
  const html = ogTemplate(`data:font/woff2;base64,${font.toString('base64')}`, `data:image/webp;base64,${homeShot.toString('base64')}`);
  await cdp.send('Emulation.setDeviceMetricsOverride', { ...OG_SIZE, deviceScaleFactor: 1, mobile: false });
  const loaded = cdp.once('Page.loadEventFired');
  await cdp.send('Page.navigate', { url: 'about:blank' });
  await loaded;
  const { frameTree } = await cdp.send('Page.getFrameTree');
  await cdp.send('Page.setDocumentContent', { frameId: frameTree.frame.id, html });
  await evaluate(cdp, 'document.fonts.ready.then(() => true)');
  await waitFor(cdp, "document.fonts.check('500 54px Geist') && document.querySelector('.shot img')?.complete", 'the OG image');
  await settle(cdp);
  await capture(cdp, path.join(out, 'og-image.jpg'), 'jpeg', OG_SIZE);
}

export function parseArgs(argv) {
  const options = { base: null, browser: null, out: path.join(repositoryRoot, 'site'), only: null, list: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--list') { options.list = true; continue; }
    const key = { '--base': 'base', '--browser': 'browser', '--out': 'out', '--only': 'only' }[arg];
    if (!key || i + 1 >= argv.length) throw new Error(`Unknown or incomplete option: ${arg}`);
    const value = argv[++i];
    if (key === 'only') options.only = value.split(',').map(name => name.trim()).filter(Boolean);
    else if (key === 'base') options.base = new URL(value).origin;
    else options[key] = path.resolve(value);
  }
  const unknown = (options.only || []).filter(name => name !== 'og' && !SHOTS.some(shot => shot.name === name));
  if (unknown.length) throw new Error(`Unknown shots: ${unknown.join(', ')}`);
  return options;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.list) {
    for (const shot of SHOTS) console.log(`${shot.name.padEnd(11)} ${shotFile(shot.name).padEnd(22)} ${shot.page}`);
    console.log(`${'og'.padEnd(11)} ${'og-image.jpg'.padEnd(22)} 1200x630 from the home shot`);
    return;
  }
  const executable = options.browser || BROWSERS.find(candidate => existsSync(candidate));
  if (!executable || !existsSync(executable)) throw new Error('No Edge or Chrome found; pass --browser <exe>.');
  const wanted = name => !options.only || options.only.includes(name);
  const vite = options.base ? null : await startVite();
  const origin = options.base || vite.origin;
  let browser = null;
  try {
    browser = await launchBrowser(executable);
    const { cdp } = browser;
    await cdp.send('Page.enable');
    await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: FIXED_CLOCK });
    await cdp.send('Emulation.setLocaleOverride', { locale: 'en-US' });
    await cdp.send('Emulation.setTimezoneOverride', { timezoneId: 'America/New_York' });
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' },
      { name: 'prefers-color-scheme', value: 'dark' }] });
    let homeShot = null;
    for (const shot of SHOTS.filter(item => wanted(item.name))) {
      console.log(`Capturing ${shot.name}`);
      const bytes = await captureShot(cdp, origin, shot, options.out);
      if (shot.name === 'home') homeShot = bytes;
    }
    if (wanted('og')) await captureOg(cdp, options.out, homeShot || await readFile(path.join(options.out, shotFile('home'))));
  } finally {
    await browser?.close();
    await vite?.close();
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
