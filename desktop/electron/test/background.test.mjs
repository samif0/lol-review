import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createBackgroundController, startupLauncher } from '../background.mjs';

async function fixture(t, options = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.Background.'));
  const profile = path.join(root, 'Revu/ElectronProfile'); await mkdir(profile, { recursive: true });
  await mkdir(path.join(root, 'current')); await writeFile(path.join(root, 'revu-desktop.exe'), 'test-stub');
  const app = new EventEmitter(); const window = new EventEmitter(); const calls = []; const trays = [];
  let enabled = options.enabled === true;
  Object.assign(app, { isPackaged: true,
    getLoginItemSettings: () => ({ openAtLogin: enabled, executableWillLaunchAtLogin: enabled }),
    setLoginItemSettings: value => { calls.push(value); enabled = value.openAtLogin; },
    quit: () => { calls.push('quit'); app.emit('before-quit'); },
  });
  Object.assign(window, { isDestroyed: () => false, isMinimized: () => true,
    hide: () => calls.push('hide'), show: () => calls.push('show'),
    restore: () => calls.push('restore'), focus: () => calls.push('focus'),
  });
  class Tray extends EventEmitter {
    constructor() { super(); trays.push(this); }
    setToolTip() {}
    setContextMenu(menu) { this.menu = menu; }
    destroy() { this.destroyed = true; }
    isDestroyed() { return !!this.destroyed; }
  }
  if (options.saved !== undefined) await writeFile(path.join(profile, 'background-settings.json'),
    typeof options.saved === 'string' ? options.saved : JSON.stringify(options.saved));
  const controller = await createBackgroundController({ app, window, Tray, Menu: { buildFromTemplate: x => x },
    iconPath: 'fixture.ico', profile, localAppData: root, platform: 'win32', executablePath: path.join(root, 'current/revu-desktop.exe'),
    argv: [], ...options });
  t.after(async () => { controller.dispose(); await rm(root, { recursive: true, force: true }); });
  return { controller, app, window, calls, trays, profile };
}

test('default startup only reads Windows settings and normal close is not intercepted', async t => {
  const { controller, calls } = await fixture(t);
  assert.equal(controller.getSettings().closeToTray, false);
  assert.equal(controller.getSettings().startWithWindows, false);
  controller.onClose({ preventDefault: () => assert.fail('default close was trapped') });
  assert.deepEqual(calls, []);
  assert.equal(controller.shouldStartHidden(), false);
});

test('tray close, reopen, and explicit Quit use the normal graceful app lifetime', async t => {
  const { controller, calls, trays } = await fixture(t);
  await controller.saveSettings({ closeToTray: true, startWithWindows: false });
  controller.onClose({ preventDefault: () => calls.push('prevented') });
  assert.deepEqual(calls, ['prevented', 'hide']);
  trays[0].emit('click');
  assert.deepEqual(calls.slice(-3), ['restore', 'show', 'focus']);
  trays[0].menu.find(item => item.label === 'Quit Revu').click();
  assert.equal(calls.at(-1), 'quit');
  controller.onClose({ preventDefault: () => assert.fail('Quit was trapped') });
});

test('disabling tray while hidden restores a visible window before destroying its icon', async t => {
  const { controller, calls, trays } = await fixture(t);
  await controller.saveSettings({ closeToTray: true, startWithWindows: false });
  controller.onClose({ preventDefault() {} });
  await controller.saveSettings({ closeToTray: false, startWithWindows: false });
  assert.deepEqual(calls, ['hide', 'restore', 'show', 'focus']);
  assert.equal(trays[0].destroyed, true);
});

test('Windows logoff and shutdown closes are never redirected to the tray', async t => {
  for (const event of ['query-session-end', 'session-end']) {
    const { controller, window } = await fixture(t, { saved: { closeToTray: true, startWithWindows: false } });
    window.emit(event, { preventDefault: () => assert.fail('system shutdown blocked') });
    controller.onClose({ preventDefault: () => assert.fail('system close trapped') });
  }
});

test('startup registration points at the stable installed stub, persists, and can be removed', async t => {
  const { controller, calls, profile } = await fixture(t);
  await controller.saveSettings({ closeToTray: true, startWithWindows: true });
  assert.deepEqual(calls[0], { path: path.join(profile, '../../revu-desktop.exe'),
    args: ['--revu-startup'], name: 'Revu', openAtLogin: true, enabled: true });
  assert.deepEqual(JSON.parse(await readFile(path.join(profile, 'background-settings.json'), 'utf8')),
    { closeToTray: true, startWithWindows: true });
  await controller.saveSettings({ closeToTray: true, startWithWindows: false });
  assert.equal(calls.at(-1).openAtLogin, false);
});

test('startup hides only when the setting, real tray, installed app, and explicit startup argument agree', async t => {
  const saved = { closeToTray: true, startWithWindows: true };
  const valid = await fixture(t, { saved, enabled: true, argv: ['--revu-startup'] });
  assert.equal(valid.controller.shouldStartHidden(), true);
  for (const override of [{ saved: { ...saved, closeToTray: false } }, { enabled: false }, { argv: [] }, { isolated: true }, { smoke: true }]) {
    const { controller, calls } = await fixture(t, { saved, enabled: true, argv: ['--revu-startup'], ...override });
    assert.equal(controller.shouldStartHidden(), false);
    assert.deepEqual(calls, []); // A launch must never silently recreate a registry entry.
  }
});

test('isolated, smoke, dev, portable, and non-Windows launches cannot set startup registration', async t => {
  for (const options of [{ isolated: true }, { smoke: true }, { platform: 'linux' }, { localAppData: path.resolve('different-profile') },
    { executablePath: path.resolve('portable/revu-desktop.exe') }]) {
    const { controller, calls } = await fixture(t, options);
    assert.equal(controller.getSettings().startupAvailable, false);
    await assert.rejects(controller.saveSettings({ closeToTray: false, startWithWindows: true }), /after installing/);
    assert.deepEqual(calls, []);
  }
  assert.equal(startupLauncher(path.resolve('current/revu-desktop.exe'), false, false, false, 'win32'), null);
});

test('failed tray creation keeps a startup launch visible and refuses hide preferences', async t => {
  const { controller } = await fixture(t, { saved: { closeToTray: true, startWithWindows: true }, enabled: true,
    argv: ['--revu-startup'], Tray: class { constructor() { throw new Error('missing icon'); } } });
  assert.equal(controller.shouldStartHidden(), false);
  assert.match(controller.getSettings().message, /tray is unavailable/);
  controller.onClose({ preventDefault: () => assert.fail('unrecoverable hidden app') });
  await assert.rejects(controller.saveSettings({ closeToTray: true, startWithWindows: true }), /missing icon/);
});

test('bad persisted settings are safe, invalid IPC payloads do not write, and concurrent writes are serialized', async t => {
  const { controller, profile, calls } = await fixture(t, { saved: '{bad json' });
  assert.match(controller.getSettings().message, /could not be read/);
  for (const bad of [null, [], {}, { closeToTray: 'true', startWithWindows: false },
    { closeToTray: true, startWithWindows: false, path: 'untrusted.exe' }])
    assert.throws(() => controller.saveSettings(bad), /Background settings/);
  assert.deepEqual(calls, []);
  await Promise.all([
    controller.saveSettings({ closeToTray: false, startWithWindows: true }),
    controller.saveSettings({ closeToTray: true, startWithWindows: false }),
  ]);
  assert.deepEqual(JSON.parse(await readFile(path.join(profile, 'background-settings.json'), 'utf8')),
    { closeToTray: true, startWithWindows: false });
});

test('refused Windows startup changes roll back and do not claim successful persistence', async t => {
  const { controller, app, profile } = await fixture(t);
  app.setLoginItemSettings = () => {};
  await assert.rejects(controller.saveSettings({ closeToTray: true, startWithWindows: true }), /did not apply/);
  assert.equal(controller.getSettings().startWithWindows, false);
  await assert.rejects(readFile(path.join(profile, 'background-settings.json')), { code: 'ENOENT' });
  controller.onClose({ preventDefault: () => assert.fail('unsaved tray preference applied') });
});
