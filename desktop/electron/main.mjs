import { app, BrowserWindow, clipboard, dialog, ipcMain, protocol, screen, shell, session, Tray, Menu } from 'electron';
import { unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { Sidecar } from './sidecar.mjs';
import { MediaRegistry } from './media.mjs';
import { APP_ORIGIN, commandRequest, isAppUrl, snapshotMedia, validateSender } from './routing.mjs';
import { isMaintenanceLaunch, launchOptions, prepareConfiguration } from './configuration.mjs';
import { nativeCommands, externalUrl } from './native.mjs';
import { registerProtocols } from './protocols.mjs';
import { createCommandGate, createQuitHandler, shutdownOwnedApp } from './shutdown.mjs';
import { RecordingService } from './recording-service.mjs';
import { createOverwolfRecorder } from './recorder-adapter.mjs';
import { createBackgroundController } from './background.mjs';

app.setName('Revu');
let window, backend, eventAbort, configuration, native, afterShutdown;
let recording, background, recorderReady = false, recorderAttached = false, recordingPrepared = false;
let quitting = false, restart = false, pageGeneration = 0;
const media = new MediaRegistry();
const packageEvents = [];
const pendingCommands = new Set();
const commandGate = createCommandGate();

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
  // timeouts. Closing the window never retries or cancels an accepted write.
  await shutdownOwnedApp({ pendingCommands,
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
app.on('before-quit', createQuitHandler({
  begin: () => { quitting = true; },
  shutdown: async () => { await shutdown(); if (restart) app.relaunch(); },
  failed: () => { process.exitCode = 1; },
  exit: () => app.exit(process.exitCode || 0),
}));
app.on('window-all-closed', () => app.quit());

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
      case 'minimize': window.minimize(); break;
      case 'toggleMaximize': window.isMaximized() ? window.unmaximize() : window.maximize(); break;
      case 'close': window.close(); break;
      case 'unminimize': window.restore(); break;
      case 'show': window.show(); break;
      case 'setFocus': window.focus(); break;
      case 'startDragging': break; // Chromium app-region CSS owns frameless dragging.
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
    const operation = executeCommand(command, args);
    pendingCommands.add(operation);
    operation.then(() => pendingCommands.delete(operation), () => pendingCommands.delete(operation));
    return operation;
  });
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
  app.setAppLogsPath(path.join(dataRoot, 'Revu', 'RuntimeLogs'));
  if (!app.requestSingleInstanceLock()) { app.quit(); return; }
  app.on('second-instance', () => { if (window) { window.restore(); window.show(); window.focus(); } });
  await app.whenReady();
  backend = new Sidecar({ dataRoot, executable, isolated, onExit: () => {
    if (!quitting) { process.exitCode = 1; app.quit(); }
  } });
  await backend.start();
  recording = new RecordingService({ root: path.join(dataRoot, 'Revu', 'Recordings'), profile, backend, isolated,
    onChange: value => {
      if (window && !window.isDestroyed()) window.webContents.send('revu:lcu-event', { type: 'recordingStatus', payload: value });
    } });
  try { await recording.initialize(); recordingPrepared = true; }
  catch { recording.publish('failed', 'The recordings folder is unavailable. Your other Revu features are still available.'); }
  void attachRecorder();
  // Revu's network policy belongs to its own session; it must not intercept
  // runtime-owned consent or package-manager windows in the default session.
  const appSession = session.fromPartition('persist:revu-ui');
  registerProtocols({ protocol: appSession.protocol, session: appSession, uiDirectory, media });
  window = new BrowserWindow({ width: 1600, height: 1000, minWidth: 980, minHeight: 640,
    frame: false, show: false, title: isolated ? 'Revu — isolated preview' : 'Revu', icon: iconPath, backgroundColor: '#0d101b',
    webPreferences: { session: appSession, preload: path.join(app.getAppPath(), 'electron/preload.cjs'), contextIsolation: true,
      additionalArguments: isolated ? ['--revu-isolated'] : [], sandbox: true,
      nodeIntegration: false, nodeIntegrationInSubFrames: false, webviewTag: false, webSecurity: true } });
  if (process.platform === 'win32') window.setAppDetails({ appId: windowsAppId, appIconPath: iconPath, appIconIndex: 0 });
  background = await createBackgroundController({ app, window, Tray, Menu, iconPath, profile, isolated, smoke });
  window.on('close', background.onClose);
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-frame-navigate', event => { if (!isAppUrl(event.url)) event.preventDefault(); });
  window.webContents.on('will-attach-webview', event => event.preventDefault());
  window.webContents.on('did-start-navigation', details => {
    if (!details.isSameDocument) { pageGeneration++; media.clear(); window.webContents.send('revu:media-clear'); }
  });
  window.webContents.on('render-process-gone', () => { process.exitCode = 1; app.quit(); });
  native = nativeCommands({ app, clipboard, dialog, screen, shell, window, dataRoot, isolated,
    quit: action => { afterShutdown = action; app.quit(); } });
  registerIpc();
  const config = await backend.request('/api/config');
  await native('set_window_resolution', { resolution: config.windowResolution || 'default' });
  await window.loadURL(`${APP_ORIGIN}/index.html`);
  await window.webContents.insertCSS('[data-window-drag]{-webkit-app-region:drag}.appbar-controls,.appbar-controls *{-webkit-app-region:no-drag}');
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
}

start().catch(async error => {
  process.exitCode = 1;
  if (configuration?.smoke) await writeFile(path.join(configuration.dataRoot, 'electron-smoke-error.txt'), String(error.message)).catch(() => {});
  else if (app.isReady()) dialog.showErrorBox('Revu could not start', error.message);
  app.quit();
});
