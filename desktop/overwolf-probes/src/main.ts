import type {} from '@overwolf/ow-electron-packages-types';
import { app, BrowserWindow, Menu } from 'electron';
import { readFileSync, writeFileSync, renameSync, statfsSync, unlinkSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { connectGep } from './gep.js';
import { Journal } from './journal.js';
import { recorderController, trackRecorderGame, type RecorderConfig } from './recorder.js';
import type { CaptureController, CaptureState } from './controller.js';
import { storageWatchdog } from './storage-watchdog.js';
import { shutdownRequestPoller } from './shutdown-request.js';

const mode = process.env.REVU_PROBE_MODE;
const root = process.env.REVU_PROBE_ROOT ?? '';
const token = process.env.REVU_PROBE_LAUNCH_TOKEN;
const lockFile = process.env.REVU_PROBE_LOCK_FILE;
const parentPid = Number(process.env.REVU_PROBE_PARENT_PID);
if (!root || !path.isAbsolute(root) || !['readiness', 'gep', 'recorder'].includes(mode ?? '') ||
    !token || !lockFile || !Number.isSafeInteger(parentPid) || parentPid <= 0 ||
    (mode !== 'readiness' && process.env.REVU_PROBE_MANUAL_OPT_IN !== '1')) throw new Error('Use the probe launcher');
type Lock = { token: string; mode: string; runRoot: string; launcherPid: number; childPid: number | null };
const ownsLock = () => {
  try {
    const lock = JSON.parse(readFileSync(lockFile, 'utf8')) as Lock;
    return lock.token === token && lock.mode === mode && path.resolve(lock.runRoot) === path.resolve(root) &&
      (lock.childPid === process.pid || (lock.childPid === null && lock.launcherPid === parentPid));
  } catch { return false; }
};
if (!ownsLock()) throw new Error('Probe launch ownership mismatch');
if (mode === 'readiness' && Object.keys(process.env).some(key => key.startsWith('OW_') && process.env[key])) {
  throw new Error('Readiness must run without gaming credentials');
}
for (const name of ['user-data', 'session-data']) mkdirSync(path.join(root, name), { recursive: true });
app.setPath('userData', path.join(root, 'user-data'));
app.setPath('sessionData', path.join(root, 'session-data'));
app.setAppLogsPath(path.join(root, 'runtime-logs'));
if (!app.requestSingleInstanceLock()) app.exit(1);
app.overwolf.disableAnonymousAnalytics();
app.overwolf.disableAdsOptimization();
const journal = new Journal(root);
let window: BrowserWindow | undefined;
let controller: CaptureController | undefined;
let gep: ReturnType<typeof connectGep> | undefined;
let tracking: { dispose(): void } | undefined;
const sourceLifetime = new AbortController();
let lifecycle = 0;
let pid: number | undefined;
let ready = false;
let blocked = false;
let identityVerified = false;
let packageArrived = false;
let startUtc = 0;
let quitting = false;
let failed = false;
let failureCategory: string | null = null;
let storageHealthy = true;
let message = mode === 'readiness' ? 'Checking startup with gaming disabled' : 'Waiting for package activation';
const packageEvents: string[] = [];
const config = JSON.parse(readFileSync(path.join(root, 'capture-settings.json'), 'utf8')) as RecorderConfig;
const status = (value: string) => {
  message = value;
  if (window && !window.isDestroyed()) window.setTitle(`Revu ${mode} probe - ${value}`);
};
const saveJson = (name: string, value: unknown) => {
  writeFileSync(path.join(root, `${name}.tmp`), JSON.stringify(value, null, 2));
  renameSync(path.join(root, `${name}.tmp`), path.join(root, `${name}.json`));
};
const hasFailure = () => failed || !storageHealthy || controller?.persistenceHealthy === false ||
  controller?.lastFailure != null || (quitting && controller !== undefined && controller.state !== 'finalized');
const saveSummary = () => {
  try {
    saveJson('run-result', { schema: 1, diagnosticOnly: true, mode, utc: new Date().toISOString(),
      identityVerified, packageReady: ready, blocked, failed: hasFailure(), storageHealthy,
      state: controller?.state ?? 'idle', failure: controller?.lastFailure ?? failureCategory,
      persistenceHealthy: controller?.persistenceHealthy ?? storageHealthy,
      captureMayBeActive: controller?.mayBeActive ?? false, journalRows: journal.rows, journalDropped: journal.dropped });
  } catch { storageHealthy = false; }
};
const disposeSources = () => {
  lifecycle++;
  sourceLifetime.abort();
  try { gep?.dispose(); } catch { failed = true; }
  gep = undefined;
  try { tracking?.dispose(); } catch { failed = true; }
  tracking = undefined;
  pid = undefined;
};
const failSafely = (category: string) => {
  blocked = true; ready = false; failed = true;
  failureCategory = category;
  disposeSources(); status(category);
  // Never write to a failed journal from an error handler.
  void controller?.crash().then(saveSummary).catch(() => { storageHealthy = false; });
  saveSummary();
};
const record = (row: Record<string, string | number | boolean | null>) => {
  try { journal.write(row); }
  catch { storageHealthy = false; failSafely('Storage unavailable; capture stopped or requires recovery'); }
};
const persist = (state: CaptureState) => {
  saveJson('session', { schema: 2, diagnosticOnly: true, state, mode, fileIntent: 'capture',
    launchToken: token, processId: process.pid, utc: new Date().toISOString(), monoMs: performance.now() });
  journal.write({ kind: 'capture-state', state }); status(state);
};
const stop = async () => { await controller?.stop(); saveSummary(); };
const packages = app.overwolf.packages;
function activatePackage() {
  if (mode === 'readiness' || !identityVerified || !packageArrived || blocked || ready || quitting) return;
  ready = true;
  if (mode === 'gep') gep = connectGep(packages.gep, journal, status);
  else {
    const generation = lifecycle;
    status('Recorder ready; waiting for League gameplay');
    void trackRecorderGame(packages.recorder, next => {
      if (generation !== lifecycle || !ready || quitting) return;
      if (pid && next && pid !== next && controller) void stop();
      pid = next;
      status(pid ? 'League detected; use Probe > Start game-only capture' : 'League exited; stopping capture');
      if (!pid) void stop();
    }, journal, { signal: sourceLifetime.signal }).then(handle => {
      if (generation !== lifecycle || quitting) handle.dispose();
      else tracking = handle;
    }).catch(() => failSafely('Recorder game tracking failed; restart this diagnostic'));
  }
}
packages.on('ready', (_event, name, version) => {
  packageEvents.push(name === 'gep' || name === 'recorder' ? name : 'unexpected');
  if (name !== mode) {
    record({ kind: 'unexpected-package' });
    failSafely('Unexpected package; inspect dependency requirements before retrying'); return;
  }
  record({ kind: 'package-ready', package: mode, version: /^[\d.a-z-]{1,40}$/i.test(version) ? version : 'unknown' });
  packageArrived = true; activatePackage();
});
packages.on('failed-to-initialize', (_event, name) => {
  if (name === mode || mode === 'readiness') {
    record({ kind: 'package-initialization-failed' });
    failSafely('Package activation failed; restart after checking access and runtime logs');
  }
});
packages.on('crashed', event => {
  event.preventDefault(); // Never silently resume a recording after a package crash.
  record({ kind: 'package-crashed' }); failSafely('Package crashed; partial media preserved');
});
process.on('uncaughtException', () => failSafely('Probe error; inspect fixed-category local metadata'));
process.on('unhandledRejection', () => failSafely('Probe operation failed; inspect fixed-category local metadata'));
process.on('exit', () => {
  saveSummary();
  // JS shutdown cannot prove a timed-out native recorder stopped.
  if (!controller?.mayBeActive && controller?.persistenceHealthy !== false && storageHealthy && ownsLock()) {
    try { unlinkSync(lockFile); } catch { /* Ambiguous ownership stays locked. */ }
  }
});

const storage = storageWatchdog({ root, active: () => Boolean(controller?.mayBeActive) && !quitting,
  elapsedMs: () => Date.now() - startUtc, onStop: kind => { record({ kind }); void stop(); } });
const shutdownRequest = shutdownRequestPoller(path.join(root, 'shutdown-request.json'), token, () => { if (!quitting) app.quit(); });
const watchdog = setInterval(() => {
  if (!quitting) {
    try { process.kill(parentPid, 0); }
    catch (error) { if ((error as NodeJS.ErrnoException).code === 'ESRCH') app.quit(); }
    void shutdownRequest.check();
  }
  void storage.check();
}, 1000);
watchdog.unref();
app.on('before-quit', event => {
  if (quitting) return;
  event.preventDefault(); quitting = true; ready = false;
  clearInterval(watchdog); storage.dispose(); shutdownRequest.dispose(); disposeSources();
  void (async () => {
    let deadline: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([stop().then(() => controller?.drained()), new Promise<void>(resolve => { deadline = setTimeout(resolve, 15_000); })]);
      if (controller && !['finalized', 'partial', 'failed'].includes(controller.state)) void controller.crash();
    } catch { failed = true; }
    finally { if (deadline) clearTimeout(deadline); saveSummary(); app.exit(hasFailure() ? 1 : 0); }
  })();
});
app.on('window-all-closed', () => app.quit());

async function start() {
  await app.whenReady();
  const uid = app.overwolf.uid;
  if ((mode !== 'readiness' && uid !== process.env.REVU_PROBE_EXPECTED_APP_ID) ||
      (process.env.OVERWOLF_APP_UID && process.env.OVERWOLF_APP_UID !== uid)) {
    record({ kind: 'app-identity-mismatch' }); failed = true; app.quit();
  } else {
    identityVerified = mode !== 'readiness';
    activatePackage(); // Subscribe before renderer load; GEP detection has no enumerate API.
    window = new BrowserWindow({ width: 820, height: 540, show: mode !== 'readiness',
      webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true, webSecurity: true } });
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('will-navigate', event => event.preventDefault());
    window.webContents.on('render-process-gone', () => { if (!quitting) { failSafely('Renderer exited; capture interrupted'); app.quit(); } });
    window.webContents.session.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
    await window.loadFile(fileURLToPath(new URL('../probe.html', import.meta.url)));
    status(message);
    Menu.setApplicationMenu(Menu.buildFromTemplate([{ label: 'Probe', submenu: [
      { label: 'Start game-only capture (microphone off)', enabled: mode === 'recorder', click: () => {
        if (!ready || blocked || quitting || !pid || controller) { status('Start requires detected League, healthy storage and a new session'); return; }
        try {
          const disk = statfsSync(root);
          if (disk.bavail * disk.bsize < 2 * 1024 ** 3) { status('Insufficient space: 2 GiB reserve required'); return; }
          startUtc = Date.now();
          controller = recorderController(packages.recorder, pid, config, root, journal, persist);
          void controller.start().then(() => {
            if (controller?.lastFailure) status(`Capture ${controller.state}: ${controller.lastFailure}`);
            saveSummary();
          });
        } catch { failSafely('Capture setup failed; inspect local metadata'); }
      } },
      { label: 'Stop capture', enabled: mode === 'recorder', click: () => { void stop(); } },
      { label: 'Retry game events', enabled: mode === 'gep', click: () => { void gep?.refresh(); } },
      { label: 'Exit', click: () => app.quit() }
    ] }]));
    if (mode === 'readiness') {
      const renderer = await window.webContents.executeJavaScript(`({ loaded: document.readyState === 'complete', title: document.title,
        nodeUnavailable: typeof require === 'undefined' && typeof process === 'undefined', headingPresent: !!document.querySelector('h1') })`);
      const manifest = JSON.parse(readFileSync(path.join(app.getAppPath(), 'package.json'), 'utf8'));
      const gamingPackagesRequested = manifest.overwolf?.packages;
      const passed = !failed && renderer.loaded && renderer.nodeUnavailable && renderer.headingPresent &&
        Array.isArray(gamingPackagesRequested) && gamingPackagesRequested.length === 0 && packageEvents.length === 0 && typeof uid === 'string' && uid.length > 0;
      saveJson('readiness-result', { schema: 1, passed, diagnosticOnly: true, liveAccessVerified: false,
        appReady: app.isReady(), rendererLoaded: renderer.loaded, rendererNodeUnavailable: renderer.nodeUnavailable,
        gamingPackagesRequested, packageEvents, credentialVariablesPresent: false,
        runtime: { electron: process.versions.electron, node: process.versions.node, chrome: process.versions.chrome },
        uid, utc: new Date().toISOString() });
      failed = !passed;
      if (process.env.REVU_PROBE_READINESS_SMOKE === '1' || process.argv.includes('--readiness-smoke')) app.quit();
      else { window.show(); status(passed ? 'Startup passed; gaming disabled' : 'Startup check failed'); }
    }
  }
}
// Electron must finish loading its entry module before emitting ready.
void start().catch(() => { failSafely('Probe startup failed; inspect local metadata'); app.quit(); });
