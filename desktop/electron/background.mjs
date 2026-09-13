import { access, readFile, rename, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';

const DEFAULTS = Object.freeze({ closeToTray: false, startWithWindows: false });
const LOGIN_ARGS = Object.freeze(['--revu-startup']);

export function validateBackgroundSettings(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).some(key => !Object.hasOwn(DEFAULTS, key))
    || Object.keys(DEFAULTS).some(key => typeof value[key] !== 'boolean'))
    throw new TypeError('Background settings must contain closeToTray and startWithWindows booleans');
  return { closeToTray: value.closeToTray, startWithWindows: value.startWithWindows };
}

// Velopack keeps this execution stub at the installation root across updates.
// https://docs.velopack.io/packaging/operating-systems/windows
export function startupLauncher(executable, packaged, isolated, smoke, platform) {
  const current = path.dirname(executable);
  if (platform !== 'win32' || !packaged || isolated || smoke
    || path.basename(current).toLowerCase() !== 'current'
    || path.basename(executable).toLowerCase() !== 'revu-desktop.exe') return null;
  return path.join(path.dirname(current), 'revu-desktop.exe');
}

export async function createBackgroundController({ app, window, Tray, Menu, iconPath, profile,
  isolated = false, smoke = false, platform = process.platform, executablePath = process.execPath,
  argv = process.argv, localAppData = process.env.LOCALAPPDATA }) {
  const file = path.join(profile, 'background-settings.json');
  let settings = { ...DEFAULTS }, message = '', tray = null, quitting = false, disposed = false;
  let saving = Promise.resolve();
  let launcher = startupLauncher(executablePath, app.isPackaged, isolated, smoke, platform);
  if (!localAppData || path.resolve(profile).toLowerCase() !== path.resolve(localAppData, 'Revu/ElectronProfile').toLowerCase())
    launcher = null;
  if (launcher) {
    try { await access(launcher); } catch { launcher = null; }
  }
  if (typeof app.setLoginItemSettings !== 'function' || typeof app.getLoginItemSettings !== 'function') launcher = null;
  const loginOptions = launcher ? { path: launcher, args: [...LOGIN_ARGS], name: 'Revu' } : null;
  const readStartup = () => {
    if (!loginOptions) return false;
    const actual = app.getLoginItemSettings(loginOptions);
    return actual.openAtLogin === true && actual.executableWillLaunchAtLogin !== false;
  };
  try {
    settings = validateBackgroundSettings(JSON.parse(await readFile(file, 'utf8')));
  } catch (error) {
    if (error.code !== 'ENOENT') message = 'Background preferences could not be read. Revu will open normally.';
  }
  // Read the Windows setting without recreating an entry disabled outside Revu.
  try { settings.startWithWindows = readStartup(); }
  catch { message = 'Windows startup settings could not be read.'; settings.startWithWindows = false; launcher = null; }

  const restore = () => {
    if (disposed || window.isDestroyed()) return;
    if (window.isMinimized()) window.restore();
    window.show(); window.focus();
  };
  const dropTray = () => {
    tray?.destroy(); tray = null;
  };
  const ensureTray = () => {
    if (tray && !tray.isDestroyed()) return;
    if (smoke || typeof Tray !== 'function' || !Menu?.buildFromTemplate)
      throw new Error('The system tray is unavailable. Revu will keep opening normally.');
    const created = new Tray(iconPath);
    try {
      created.setToolTip('Revu');
      created.setContextMenu(Menu.buildFromTemplate([
        { label: 'Open Revu', click: restore },
        { type: 'separator' },
        { label: 'Quit Revu', click: () => { quitting = true; app.quit(); } },
      ]));
      created.on('click', restore);
      created.on('double-click', restore);
      tray = created;
    } catch (error) { created.destroy(); throw error; }
  };
  if (settings.closeToTray) {
    try { ensureTray(); }
    catch { settings.closeToTray = false; message = 'The system tray is unavailable. Closing Revu will quit the app.'; }
  }

  const beginQuit = () => { quitting = true; };
  app.on('before-quit', beginQuit);
  // Windows does not reliably emit before-quit at logoff/shutdown. Never turn
  // these closes into a tray hide or prevent the operating system from exiting.
  window.on('query-session-end', beginQuit);
  window.on('session-end', beginQuit);
  const getSettings = () => ({ ...settings, startupAvailable: !!launcher,
    trayAvailable: !smoke && typeof Tray === 'function' && !!Menu?.buildFromTemplate, message });
  const saveSettings = payload => {
    const requested = validateBackgroundSettings(payload);
    const operation = saving.then(async () => {
      if (disposed || quitting) throw new Error('Revu is closing');
      if (requested.startWithWindows && !launcher)
        throw new Error('Start with Windows is available after installing Revu.');
      const previousStartup = launcher ? readStartup() : false;
      const createdTray = requested.closeToTray && !tray;
      if (requested.closeToTray) ensureTray();
      const temp = file + '.tmp';
      let changedLogin = false;
      try {
        await writeFile(temp, JSON.stringify(requested, null, 2) + '\n', { encoding: 'utf8', flush: true });
        if (launcher && requested.startWithWindows !== previousStartup) {
          app.setLoginItemSettings({ ...loginOptions, openAtLogin: requested.startWithWindows, enabled: requested.startWithWindows });
          changedLogin = true;
          if (readStartup() !== requested.startWithWindows)
            throw new Error('Windows did not apply the startup setting. Check Startup apps in Windows Settings.');
        }
        await rename(temp, file);
      } catch (error) {
        await unlink(temp).catch(() => {});
        if (changedLogin) {
          try { app.setLoginItemSettings({ ...loginOptions, openAtLogin: previousStartup, enabled: previousStartup }); }
          catch { message = 'Windows startup settings may have changed. Check Startup apps in Windows Settings.'; }
        }
        if (createdTray) dropTray();
        throw error;
      }
      settings = requested; message = '';
      if (!settings.closeToTray && tray) { restore(); dropTray(); }
      return getSettings();
    });
    saving = operation.catch(() => {});
    return operation;
  };
  return {
    getSettings, saveSettings, restore,
    onClose: event => {
      if (quitting || disposed || !settings.closeToTray || !tray || tray.isDestroyed()) return;
      event.preventDefault(); window.hide();
    },
    shouldStartHidden: () => !quitting && !disposed && !isolated && !smoke && !!launcher
      && argv.includes('--revu-startup') && settings.startWithWindows && settings.closeToTray && !!tray && !tray.isDestroyed(),
    dispose: () => {
      disposed = true; dropTray();
      app.removeListener('before-quit', beginQuit);
      window.removeListener('query-session-end', beginQuit);
      window.removeListener('session-end', beginQuit);
    },
  };
}
