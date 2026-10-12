import assert from 'node:assert/strict';
import test from 'node:test';
import { askRecovery, askSlowStart, createClosingNotice, createRendererRecovery, RECOVERY_BUTTONS, relaunchArgs,
  shellPage, shellUrl, SLOW_START_BUTTONS } from '../lifecycle.mjs';
import { createSplash, createStartupProgress, SPLASH_TEXT, splashPage } from '../splash.mjs';

test('reset, restore and crash relaunches never inherit the hidden login launch flag', () => {
  assert.deepEqual(relaunchArgs(['C:\\Revu\\current\\revu-desktop.exe', '--revu-startup']), []);
  assert.deepEqual(relaunchArgs(['electron.exe', 'C:\\app', '--revu-startup', '--data-root', 'D:\\scratch']),
    ['C:\\app', '--data-root', 'D:\\scratch']);
});

test('the first renderer crash reloads; another soon after asks the user', () => {
  let time = 0;
  const decide = createRendererRecovery({ now: () => time, windowMs: 1000 });
  assert.equal(decide(), 'reload');
  time = 500;
  assert.equal(decide(), 'ask');
  time = 5000;
  assert.equal(decide(), 'reload');
});

test('recovery reopens only an app page through the shell hash', () => {
  assert.equal(shellPage('revu-app://ui/review.html?gameId=5'), 'review.html?gameId=5');
  for (const url of ['revu-app://ui/index.html', 'https://ui/review.html', 'revu-app://other/review.html',
    'revu-app://ui/nested/review.html', 'revu-app://ui/fonts/x.woff2', 'not a url', 'revu-app://ui/'])
    assert.equal(shellPage(url), '', url);
  assert.equal(shellUrl('revu-app://ui', 'vodplayer.html?gameId=9'), 'revu-app://ui/index.html#vodplayer.html?gameId=9');
  assert.equal(shellUrl('revu-app://ui', ''), 'revu-app://ui/index.html');
});

function fakeDialog(responses) {
  const calls = [];
  return { calls, showMessageBox: async (...args) => { calls.push(args); return { response: responses.shift() }; } };
}

test('a crash dialog offers restart, logs and quit; opening logs keeps the choice open', async () => {
  const dialog = fakeDialog([1, 1, 0]);
  let opened = 0;
  assert.equal(await askRecovery({ dialog, kind: 'sidecar', openLogs: async () => { opened++; if (opened === 2) throw new Error('gone'); } }), 'restart');
  assert.equal(opened, 2);
  assert.equal(dialog.calls.length, 3);
  const [options] = dialog.calls[0];
  assert.deepEqual(options.buttons, [...RECOVERY_BUTTONS]);
  assert.deepEqual(options.buttons, ['Restart Revu', 'Open logs', 'Quit']);
  assert.equal(options.cancelId, 2);
  for (const text of [options.message, options.detail]) assert.doesNotMatch(text, /\u2014/);

  const parent = { id: 'window' };
  const quit = fakeDialog([2]);
  assert.equal(await askRecovery({ dialog: quit, parent, kind: 'renderer', openLogs: async () => {} }), 'quit');
  assert.equal(quit.calls[0][0], parent);
  const closed = fakeDialog([undefined]);
  assert.equal(await askRecovery({ dialog: closed, kind: 'sidecar', openLogs: async () => {} }), 'quit');
});

test('a startup failure shows the backend diagnosis itself', async () => {
  const dialog = fakeDialog([2]);
  const diagnosis = "Revu couldn't open its database: the file revu.db is marked read-only.";
  await askRecovery({ dialog, kind: 'startup', detail: diagnosis, openLogs: async () => {} });
  const [options] = dialog.calls[0];
  assert.equal(options.message, 'Revu could not start');
  assert.ok(options.detail.startsWith(diagnosis));
  assert.doesNotMatch(options.detail, /within 20 seconds/);
});

test('a relaunch while closing gets one notice, not a crash or a stack of dialogs', async () => {
  const dialog = fakeDialog([0, 0]);
  const notice = createClosingNotice(dialog);
  notice(); notice();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(dialog.calls.length, 1);
  assert.match(dialog.calls[0][0].detail, /will close shortly/);
});

test('the splash is static, escaped markup and cannot be closed while data is prepared', () => {
  const page = decodeURIComponent(splashPage('<b>Starting</b>').replace(/^data:text\/html;charset=utf-8,/, ''));
  assert.match(page, /default-src 'none'/);
  assert.match(page, /&#60;b&#62;Starting&#60;\/b&#62;/);
  assert.doesNotMatch(page, /<script/i);
  assert.doesNotMatch(SPLASH_TEXT.preparing + SPLASH_TEXT.starting, /\u2014/);

  const windows = [];
  class FakeWindow {
    constructor(options) { this.options = options; this.handlers = {}; this.loaded = []; this.destroyed = false; windows.push(this);
      this.webContents = { setWindowOpenHandler() {}, on() {} }; }
    on(name, handler) { this.handlers[name] = handler; }
    once(name, handler) { this.handlers[name] = handler; }
    loadURL(url) { this.loaded.push(url); return Promise.resolve(); }
    isDestroyed() { return this.destroyed; }
    isMinimized() { return false; }
    show() { this.shown = true; }
    focus() {}
    destroy() { this.destroyed = true; }
  }
  const splash = createSplash({ BrowserWindow: FakeWindow, iconPath: 'revu.ico' });
  splash.show();
  splash.show();
  assert.equal(windows.length, 1);
  assert.equal(windows[0].options.webPreferences.javascript, false);
  assert.equal(windows[0].options.webPreferences.devTools, false);
  splash.preparing();
  assert.match(decodeURIComponent(windows[0].loaded.at(-1)), /Updating your Revu data/);
  let prevented = false;
  windows[0].handlers.close({ preventDefault: () => { prevented = true; } });
  assert.equal(prevented, true);
  splash.close();
  assert.equal(windows[0].destroyed, true);
  splash.close();
});

function fakeSplash() {
  const calls = [];
  return { calls, show: () => calls.push('show'), preparing: () => calls.push('preparing') };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('an upgrade on a login launch stays hidden in the tray until it runs long', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const splash = fakeSplash();
  const asked = [];
  const progress = createStartupProgress({ splash, login: true, slowAfterMs: 1000,
    askSlow: async signal => { asked.push(signal); return 'wait'; } });
  progress.preparing();
  assert.deepEqual(splash.calls, ['preparing']);
  t.mock.timers.tick(999);
  assert.deepEqual(splash.calls, ['preparing']);
  assert.equal(asked.length, 0);
  // Still upgrading after the threshold: surface with a way out, then ask again later.
  t.mock.timers.tick(1);
  await settle();
  assert.deepEqual(splash.calls, ['preparing', 'show']);
  assert.equal(asked.length, 1);
  t.mock.timers.tick(1000);
  await settle();
  assert.equal(asked.length, 2);
  assert.deepEqual(splash.calls, ['preparing', 'show', 'show']);
  // Startup ended: an open question closes and nothing is shown or asked again.
  progress.finish();
  assert.equal(asked[1].aborted, true);
  t.mock.timers.tick(5000);
  await settle();
  progress.preparing();
  assert.equal(asked.length, 2);
  assert.deepEqual(splash.calls, ['preparing', 'show', 'show']);
});

test('an ordinary launch shows the upgrade at once, and quitting stops the questions', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const splash = fakeSplash();
  let asked = 0;
  const progress = createStartupProgress({ splash, slowAfterMs: 1000, askSlow: async () => { asked++; return 'quit'; } });
  progress.preparing();
  assert.deepEqual(splash.calls, ['preparing', 'show']);
  t.mock.timers.tick(1000);
  await settle();
  t.mock.timers.tick(5000);
  await settle();
  assert.equal(asked, 1);
  progress.finish();

  // Startup that finishes before the threshold never asks.
  const quick = createStartupProgress({ splash: fakeSplash(), slowAfterMs: 1000, askSlow: async () => assert.fail('asked') });
  quick.preparing();
  quick.finish();
  t.mock.timers.tick(5000);
  await settle();
});

test('a long data update offers keep waiting, the logs, or quit', async () => {
  const dialog = fakeDialog([1, 2]);
  let opened = 0;
  assert.equal(await askSlowStart({ dialog, openLogs: async () => { opened++; } }), 'quit');
  assert.equal(opened, 1);
  const [options] = dialog.calls[0];
  assert.deepEqual(options.buttons, [...SLOW_START_BUTTONS]);
  assert.deepEqual(options.buttons, ['Keep waiting', 'Open logs', 'Quit']);
  assert.equal(options.cancelId, 0);
  assert.match(options.detail, /finishes the update the next time it opens/);
  for (const text of [options.message, options.detail]) assert.doesNotMatch(text, /\u2014/);

  assert.equal(await askSlowStart({ dialog: fakeDialog([0]), openLogs: async () => {} }), 'wait');
  // Startup ending closes the box as cancelled; even a stray Quit then means nothing.
  const controller = new AbortController();
  const closing = { showMessageBox: async options => { assert.equal(options.signal, controller.signal); controller.abort(); return { response: 2 }; } };
  assert.equal(await askSlowStart({ dialog: closing, signal: controller.signal, openLogs: async () => {} }), 'wait');
});
