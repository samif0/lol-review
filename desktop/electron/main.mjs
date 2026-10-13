import { app, BrowserWindow, clipboard, dialog, ipcMain, nativeTheme, protocol, screen, shell, session, systemPreferences, Tray, Menu } from 'electron';
import { mkdir, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { Sidecar } from './sidecar.mjs';
import { MediaRegistry } from './media.mjs';
import { APP_ORIGIN, commandRequest, isAppUrl, snapshotMedia, validateSender } from './routing.mjs';
import { declaredPackages, isMaintenanceLaunch, launchOptions, prepareConfiguration } from './configuration.mjs';
import { nativeCommands, externalUrl } from './native.mjs';
import { registerProtocols } from './protocols.mjs';
import { createCommandGate, createQuitHandler, shutdownOwnedApp } from './shutdown.mjs';
import { RecordingService } from './recording-service.mjs';
import { createOverwolfRecorder } from './recorder-adapter.mjs';
import { createBackgroundController } from './background.mjs';
import { createAppearanceController, readWindowAppearance, windowPalette } from './appearance.mjs';
import { saveNarration } from './narration.mjs';
import { createRollingLog, logDirectory, runtimeLogDirectory } from './logging.mjs';
import { askRecovery, askSlowStart, backendExitAction, createClosingNotice, createRendererRecovery, readLaunchConfig, relaunchArgs, shellPage, shellUrl,
  watchSessionEnd } from './lifecycle.mjs';
import { createSplash, createStartupProgress } from './splash.mjs';

app.setName('Revu');
let window, backend, eventAbort, configuration, native, afterShutdown, splash, progress;
let recording, background, appearance, recorderReady = false, recorderAttached = false, recordingPrepared = false;
let quitting = false, restart = false, pageGeneration = 0;
// A crashed backend cannot stop cleanly; restartAfterFailure is the user's recovery choice.
// abandonedWork: quit cancelled a long job, so its backend may need to be ended.
let backendCrashed = false, restartAfterFailure = false, recovering = false, abandonedWork = false, framePage = '';
// True while Windows is ending the session (logoff, restart, shutdown).
let sessionEnding = () => false;
const media = new MediaRegistry();
const packageEvents = [];
const pendingCommands = new Set();
const commandNames = new WeakMap();
const commandGate = createCommandGate();
// <data root>/Revu/logs: host lifecycle, plus a capped copy of the sidecar's stderr.
const hostLog = createRollingLog({ name: 'host' });
const sidecarStderr = createRollingLog({ name: 'sidecar-stderr', maxFiles: 2 });
const rendererRecovery = createRendererRecovery();
const closingNotice = createClosingNotice(dialog);
// Ordinary writes time out within 30 s, so this bound only stops waiting on long jobs.
const QUIT_DRAIN_MS = 35_000;
// Controls inside a drag region stay clickable; the title bar buttons never drag.
const DRAG_CSS = '[data-window-drag]{-webkit-app-region:drag}[data-window-drag] :is(a,button,input,select,textarea,[role="button"]),'
  + '.appbar-controls,.appbar-controls *{-webkit-app-region:no-drag}';

process.on('uncaughtException', error => hostLog.error('Uncaught exception in the main process', error));
process.on('unhandledRejection', reason => hostLog.error('Unhandled promise rejection in the main process', reason));

// The ordinary unsigned build requests no gaming packages. The explicit
// credentialed development launcher enables Recorder for the integrated app.
app.overwolf?.disableAnonymousAnalytics();
for (const eventName of ['loading', 'ready', 'failed-to-initialize']) {
  app.overwolf?.packages.on(eventName, (_event, packageName) => {
    packageEvents.push({ event: eventName, packageName });
    if (['gep', 'overlay'].includes(packageName)) { process.exitCode = 1; app.quit(); }
    if (packageName === 'recorder') {
      if (eventName === 'ready') { recorderReady = true; void attachRecorder(); }
      if (eventName === 'failed-to-initialize') { recorderReady = false; recording?.unavailable(); }
    }
  });
}
app.overwolf?.packages.on('crashed', event => {
  event.preventDefault(); // Keep the interrupted session visible; no silent restart.
  recorderReady = false; recording?.unavailable();
});
protocol.registerSchemesAsPrivileged([
  { scheme: 'revu-app', privileges: { standard: true, secure: true, supportFetchAPI: true } },
  { scheme: 'revu-media', privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true } },
]);

async function shutdown() {
  // Submitted writes, including final pagehide drafts, retain their own HTTP
  // timeouts. Closing the window never retries or cancels an accepted write;
  // only long jobs can outlast the drain bound. Those are logged and cancelled, so
  // the backend stops them (ffmpeg, backfill) and saves nothing partial.
  await shutdownOwnedApp({ pendingCommands, drainTimeoutMs: QUIT_DRAIN_MS,
    onAbandoned: remaining => {
      abandonedWork = true;
      hostLog.warn('Closing without waiting for: '
        + remaining.map(operation => commandNames.get(operation) || 'command').join(', ') + '; cancelling them');
      backend?.cancelRequests();
    },
    abortEvents: () => eventAbort?.abort(), releaseMedia: () => media.clear(),
    stopBackend: async () => {
      try { await recording?.shutdown(); }
      finally { background?.dispose(); await backend?.stop(); }
    }, afterShutdown: () => afterShutdown?.() });
}
async function attachRecorder() {
  if (!recordingPrepared || !recording || !recorderReady || recorderAttached || configuration?.isolated || quitting) return;
  recorderAttached = true;
  try {
    const driver = createOverwolfRecorder({ recorder: app.overwolf.packages.recorder,
      shouldDiscover: () => recording.settings.enabled || !!recording.current,
      onGame: game => recording.onGame(game), onUnavailable: () => recording.unavailable() });
    await recording.attachDriver(driver);
  } catch { recording.unavailable(); }
}
const relaunch = () => app.relaunch({ args: relaunchArgs(process.argv) });
const openLogs = async () => {
  const folder = logDirectory(configuration.dataRoot);
  await mkdir(folder, { recursive: true });
  const error = await shell.openPath(folder);
  if (error) throw new Error(error);
};
app.on('before-quit', createQuitHandler({
  begin: () => { quitting = true; hostLog.info('Revu is closing'); },
  shutdown: async () => { await shutdown(); if (restart || restartAfterFailure) relaunch(); },
  failed: error => {
    process.exitCode = 1;
    hostLog.error('Revu could not finish closing', error);
    // After a backend crash its unclean stop is expected; the user already chose.
    // Ending a backend that was still stopping a cancelled long job is logged above.
    if (restartAfterFailure) relaunch();
    else if (!backendCrashed && !(abandonedWork && error?.forced) && !configuration?.smoke && !sessionEnding())
      dialog.showErrorBox('Revu could not finish closing', String(error?.message || error));
  },
  exit: () => { hostLog.info(`Revu exited (code ${process.exitCode || 0})`); app.exit(process.exitCode || 0); },
}));
app.on('window-all-closed', () => app.quit());
app.on('child-process-gone', (_event, details) => hostLog.warn(`A helper process stopped (${details.type}, ${details.reason})`));

// Ask once how to recover from a crash: restart Revu, open the logs, or quit.
async function recover(kind) {
  if (recovering || quitting) return;
  recovering = true;
  let choice = 'quit';
  if (!configuration?.smoke) {
    const parent = window && !window.isDestroyed() && window.isVisible() ? window : undefined;
    try { choice = await askRecovery({ dialog, parent, kind, openLogs }); }
    catch (error) { hostLog.error('The recovery dialog failed', error); }
  }
  hostLog.info(`Recovery after a ${kind} failure: ${choice}`);
  if (choice === 'restart') restartAfterFailure = true;
  process.exitCode = 1;
  app.quit();
}

async function executeCommand(command, args) {
  if (quitting || restart) throw new Error('The app is closing');
  const request = commandRequest(command, args);
  const generation = pageGeneration;
  if (configuration.isolated && (['check_update', 'download_update', 'apply_update'].includes(command)
    || request.definition.sideEffect === 'restart-after-write'))
    throw new Error('Updates and database replacement are disabled in isolated mode');
  return commandGate({ terminal: command === 'apply_update'
    || request.definition.sideEffect === 'restart-after-write', pendingCommands }, async () => {
    if ((command === 'apply_update' || request.definition.sideEffect === 'restart-after-write') && recording?.current)
      throw new Error('Finish the current recording before updating or replacing application data.');
    if (request.method === 'SSE') {
      if (!eventAbort) {
        eventAbort = new AbortController();
        void backend.events(eventAbort.signal, payload => {
          if (!window.isDestroyed()) window.webContents.send('revu:lcu-event', payload);
        });
      }
      return { value: null };
    }
    let value;
    if (command === 'get_recording_status') value = recording.getStatus();
    else if (command === 'save_recording_settings') value = await recording.saveSettings(args.payload);
    else if (command === 'get_background_settings') value = background.getSettings();
    else if (command === 'save_background_settings') value = await background.saveSettings(args.payload);
    else if (command === 'set_window_appearance') value = await appearance.apply(args.payload);
    else if (command === 'open_recordings_folder') {
      const error = await shell.openPath(recording.root);
      if (error) throw new Error('The recordings folder could not be opened');
      value = { ok: true };
    } else value = request.method === 'NATIVE' ? await native(command, args) : await backend.request(request.route, request);
    if (request.definition.sideEffect === 'restart-after-write' && value?.ok !== false) {
      restart = true;
      setImmediate(() => app.quit());
    }
    const paths = snapshotMedia(command, value);
    if (generation !== pageGeneration && paths) throw new Error('Review changed during media resolution');
    const grants = paths ? await media.replace(paths) : null;
    if (generation !== pageGeneration && paths) throw new Error('Review changed during media resolution');
    return { value, grants };
  });
}

function registerIpc() {
  ipcMain.handle('revu:window', (event, action) => {
    validateSender(event, window.webContents);
    switch (action) {
      case 'minimize': if (window.isVisible()) window.minimize(); break; // never re-show a tray-hidden window
      case 'toggleMaximize': window.isMaximized() ? window.unmaximize() : window.maximize(); break;
      case 'close': window.close(); break;
      case 'unminimize': window.restore(); break;
      case 'show': window.show(); break;
      case 'setFocus': window.focus(); break;
      default: throw new Error('Unsupported window action');
    }
  });
  ipcMain.handle('revu:open-external', (event, value) => {
    validateSender(event, window.webContents);
    if (configuration.isolated || quitting) throw new Error('External links are disabled');
    return shell.openExternal(externalUrl(value));
  });
  ipcMain.handle('revu:command', (event, command, args = {}) => {
    validateSender(event, window.webContents);
    return track(executeCommand(command, args), command);
  });
  // Narration audio has its own channel: bytes never travel through the JSON
  // command envelope, and the renderer never chooses where the file is written.
  // Tracked like a command so shutdown waits for it; the sidecar request is bounded at 280 s.
  // It shares command admission, so it cannot race an update or database replacement.
  ipcMain.handle('revu:narration-save', (event, bytes, meta) => {
    validateSender(event, window.webContents);
    if (configuration.isolated || quitting || restart) throw new Error('Narration is unavailable while Revu is closing or in isolated mode');
    return track(commandGate({ terminal: false, pendingCommands }, () =>
      saveNarration({ root: path.join(configuration.dataRoot, 'Revu', 'Narration'), backend, bytes, meta })), 'narration-save');
  });
}

function track(operation, name) {
  pendingCommands.add(operation);
  commandNames.set(operation, name);
  operation.then(() => pendingCommands.delete(operation), () => pendingCommands.delete(operation));
  return operation;
}

async function start() {
  // Velopack maintenance hooks must never open a profile, database or window.
  if (isMaintenanceLaunch(process.argv)) { app.exit(0); return; }
  configuration = prepareConfiguration({ options: launchOptions(process.argv),
    desktopRoot: app.getAppPath(), localAppData: process.env.LOCALAPPDATA,
    packaged: app.isPackaged, executablePath: process.execPath });
  const { dataRoot, profile, windowsAppId, iconPath, uiDirectory, executable, isolated, smoke } = configuration;
  if (process.platform === 'win32') app.setAppUserModelId(windowsAppId);
  app.setPath('userData', profile);
  app.setPath('sessionData', path.join(profile, 'session'));
  // The runtime's own logs sit with Revu's, so "Open logs" shows everything support needs.
  app.setAppLogsPath(runtimeLogDirectory(dataRoot));
  if (!app.requestSingleInstanceLock()) { app.quit(); return; }
  hostLog.open(logDirectory(dataRoot));
  sidecarStderr.open(logDirectory(dataRoot));
  // A login launch starts hidden in the tray.
  const login = process.argv.includes('--revu-startup');
  hostLog.info(`Revu ${app.getVersion()} starting (${app.isPackaged ? 'packaged' : 'development'}`
    + `${isolated ? ', isolated' : ''}${smoke ? ', smoke' : ''}${login ? ', login' : ''})`);
  app.on('second-instance', () => {
    // The window is destroyed (or hidden for good) while a quit drains pending work.
    if (quitting) closingNotice();
    else if (window && !window.isDestroyed()) { window.restore(); window.show(); window.focus(); }
    else if (!smoke) splash?.show(); // Still starting: show progress instead of nothing.
  });
  await app.whenReady();
  // Packaged builds have no menu, so no reload, zoom or DevTools accelerators.
  if (app.isPackaged) Menu.setApplicationMenu(null);
  // The colors the shell last showed, so the splash and window open in the user's theme.
  // nativeTheme is only read: themeSource stays 'system' so pages follow Windows.
  const savedAppearance = await readWindowAppearance(profile);
  splash = createSplash({ BrowserWindow, iconPath, appearance: savedAppearance, dark: nativeTheme.shouldUseDarkColors });
  if (!smoke && !login) splash.show();
  progress = createStartupProgress({ splash, login, askSlow: async signal => {
    hostLog.warn('The data update is taking longer than expected');
    const choice = await askSlowStart({ dialog, signal, openLogs });
    hostLog.info(`Slow data update: ${choice}`);
    if (choice === 'quit') app.quit();
    return choice;
  } });
  backend = new Sidecar({ dataRoot, executable, isolated, log: hostLog, stderrLog: sidecarStderr,
    onPreparing: () => { if (!smoke) progress.preparing(); },
    onExit: () => {
      const action = backendExitAction({ quitting, sessionEnding: sessionEnding() });
      if (action === 'ignore') return;
      backendCrashed = true;
      // Windows ending the session stops the backend too. That is not a crash: close quietly.
      if (action === 'quit') { hostLog.info('The backend stopped while Windows is ending the session; closing'); app.quit(); }
      else void recover('sidecar');
    } });
  await backend.start();
  progress.finish();
  if (quitting) return;
  recording = new RecordingService({ root: path.join(dataRoot, 'Revu', 'Recordings'), profile, backend, isolated,
    onChange: value => {
      if (window && !window.isDestroyed()) window.webContents.send('revu:lcu-event', { type: 'recordingStatus', payload: value });
    } });
  // The ordinary build declares no gaming packages: the dormant recorder does no
  // filesystem work and get_recording_status keeps answering unavailable.
  if (declaredPackages(app.getAppPath()).includes('recorder')) {
    try { await recording.initialize(); recordingPrepared = true; }
    catch { recording.publish('failed', 'The recordings folder is unavailable. Your other Revu features are still available.'); }
    void attachRecorder();
  } else recording.publish('unavailable', 'Recording is not available in this build yet.');
  // Revu's network policy belongs to its own session; it must not intercept
  // runtime-owned consent or package-manager windows in the default session.
  const appSession = session.fromPartition('persist:revu-ui');
  registerProtocols({ protocol: appSession.protocol, session: appSession, uiDirectory, media });
  window = new BrowserWindow({ width: 1600, height: 1000, minWidth: 980, minHeight: 640,
    frame: false, show: false, title: isolated ? 'Revu (isolated preview)' : 'Revu', icon: iconPath,
    backgroundColor: windowPalette(savedAppearance, { dark: nativeTheme.shouldUseDarkColors }).background,
    webPreferences: { session: appSession, preload: path.join(app.getAppPath(), 'electron/preload.cjs'), contextIsolation: true,
      additionalArguments: isolated ? ['--revu-isolated'] : [], sandbox: true, devTools: !app.isPackaged,
      nodeIntegration: false, nodeIntegrationInSubFrames: false, webviewTag: false, webSecurity: true } });
  if (process.platform === 'win32') window.setAppDetails({ appId: windowsAppId, appIconPath: iconPath, appIconIndex: 0 });
  appearance = createAppearanceController({ window, profile, saved: savedAppearance });
  background = await createBackgroundController({ app, window, Tray, Menu, iconPath, profile, isolated, smoke });
  window.on('close', background.onClose);
  sessionEnding = watchSessionEnd({ window });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-frame-navigate', event => { if (!isAppUrl(event.url)) event.preventDefault(); });
  window.webContents.on('will-attach-webview', event => event.preventDefault());
  window.webContents.on('did-start-navigation', details => {
    if (!details.isSameDocument) { pageGeneration++; media.clear(); window.webContents.send('revu:media-clear'); }
  });
  // Frameless dragging is page CSS; re-apply it on every top-level load (recovery, reloads).
  window.webContents.on('dom-ready', () => { window.webContents.insertCSS(DRAG_CSS).catch(() => {}); });
  window.webContents.on('did-frame-navigate', (_event, url, _code, _status, isMainFrame) => {
    if (!isMainFrame) framePage = shellPage(url);
  });
  window.webContents.on('render-process-gone', (_event, details) => {
    hostLog.error(`The window stopped (${details.reason}, exit code ${details.exitCode})`);
    if (quitting || recovering) return;
    if (smoke || rendererRecovery() === 'ask') { void recover('renderer'); return; }
    hostLog.info('Reloading the window once after a renderer failure');
    window.loadURL(shellUrl(APP_ORIGIN, framePage)).catch(error => {
      hostLog.error('Reloading the window failed', error);
      void recover('renderer');
    });
  });
  native = nativeCommands({ app, clipboard, dialog, screen, shell, systemPreferences, window, dataRoot, isolated,
    quit: action => { afterShutdown = action; app.quit(); } });
  registerIpc();
  const config = await readLaunchConfig(route => backend.request(route), { log: hostLog });
  if (quitting || recovering) return; // A backend that stopped meanwhile already asked how to recover.
  await native('set_window_resolution', { resolution: config.windowResolution || 'default' });
  await window.loadURL(`${APP_ORIGIN}/index.html`);
  if (quitting) return;
  if (smoke) {
    window.showInactive(); // Exercise actual painting without stealing focus.
    const { runSmoke } = await import('./smoke.mjs');
    const result = await runSmoke(window, { dataRoot, packageEvents, media, backend, executable });
    await shutdown();
    await unlink(path.join(dataRoot, 'electron-smoke-error.txt')).catch(() => {});
    await writeFile(path.join(dataRoot, 'electron-smoke.json'), JSON.stringify({ ...result, ownedSidecarStopped: true }, null, 2));
    app.quit();
  } else if (!background.shouldStartHidden()) window.show();
  else window.hide();
  if (smoke) return;
  splash.close();
  hostLog.info('Revu is ready');
}

start().catch(async error => {
  process.exitCode = 1;
  hostLog.error('Revu could not start', error);
  progress?.finish();
  if (configuration?.smoke) await writeFile(path.join(configuration.dataRoot, 'electron-smoke-error.txt'), String(error.message)).catch(() => {});
  // Closed during startup, or a backend crash already asked: that choice owns the exit.
  else if (quitting || recovering) return;
  else if (!app.isReady() || !hostLog.file) dialog.showErrorBox('Revu could not start', String(error?.message || error));
  else {
    // A verified backend that cannot open its database explains why here.
    const choice = await askRecovery({ dialog, kind: 'startup', detail: String(error?.message || error), openLogs })
      .catch(() => 'quit');
    if (choice === 'restart') restartAfterFailure = true;
  }
  app.quit();
});
