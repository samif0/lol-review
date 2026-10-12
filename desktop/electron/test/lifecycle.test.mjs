import assert from 'node:assert/strict';
import test from 'node:test';
import { askRecovery, createClosingNotice, createRendererRecovery, RECOVERY_BUTTONS, relaunchArgs, shellPage, shellUrl } from '../lifecycle.mjs';
import { createSplash, SPLASH_TEXT, splashPage } from '../splash.mjs';

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
