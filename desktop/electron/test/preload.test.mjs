import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { validateSender, isAppUrl } from '../routing.mjs';

const source = await readFile(new URL('../preload.cjs', import.meta.url), 'utf8');

function loadPreload({ mainFrame = true, isolated = true, protocol = 'revu-app:', host = 'ui', invoke = async () => ({ value: null }) } = {}) {
  const ipc = new EventEmitter();
  const exposed = {};
  const calls = [];
  ipc.invoke = async (...args) => { calls.push(args); return invoke(...args); };
  const electron = { ipcRenderer: ipc, contextBridge: { exposeInMainWorld(name, value) { exposed[name] = value; } } };
  vm.runInNewContext(source, {
    require(name) { assert.equal(name, 'electron'); return electron; },
    process: { isMainFrame: mainFrame, argv: isolated ? ['--revu-isolated'] : [] }, location: { protocol, host },
  });
  return { bridge: exposed.revuDesktop, ipc, calls };
}

test('preload installs only in the trusted top document and exposes no raw IPC methods', () => {
  for (const context of [{ mainFrame: false }, { protocol: 'https:' }, { host: 'other' }]) {
    const { bridge, ipc } = loadPreload(context);
    assert.equal(bridge, undefined);
    assert.equal(ipc.eventNames().length, 0);
  }
  const { bridge } = loadPreload();
  assert.equal(bridge.kind, 'electron');
  assert.equal(bridge.version, 1);
  assert.equal(bridge.capabilities.recorder, false);
  assert.equal(bridge.capabilities.updates, false);
  assert.equal(bridge.send, undefined);
  assert.equal(bridge.ipcRenderer, undefined);
  assert.equal(bridge.readFile, undefined);
  assert.ok(Object.isFrozen(bridge));
  assert.ok(Object.isFrozen(bridge.window));
});

test('preload forwards application results and never exposes native event/sender objects', async () => {
  const payload = { type: 'gameEnded', payload: { gameId: 101 } };
  const nativeEvent = { sender: 'privileged-native-object', reply() {} };
  const { bridge, ipc, calls } = loadPreload({ invoke: async () => ({ value: { ok: true } }) });
  const result = await bridge.invoke('save_review', { payload: { gameId: 101 } });
  assert.equal(result.ok, true);
  assert.deepEqual(calls, [['revu:command', 'save_review', { payload: { gameId: 101 } }]]);
  let delivered;
  const unsubscribe = await bridge.listen('lcu-event', (event) => { delivered = event; });
  ipc.emit('revu:lcu-event', nativeEvent, payload);
  assert.equal(delivered.payload, payload);
  assert.equal(delivered.sender, undefined);
  assert.equal(delivered.reply, undefined);
  assert.equal(Object.keys(delivered).join(','), 'payload');
  unsubscribe();
  assert.equal(ipc.listenerCount('revu:lcu-event'), 0);
  await assert.rejects(bridge.listen('arbitrary-channel', () => {}), /Invalid event subscription/);
  await assert.rejects(bridge.listen('lcu-event', null), /Invalid event subscription/);
});

test('media resolver exposes host grants only and clears them on navigation', async () => {
  const allowed = 'C:\\scratch\\synthetic.mp4';
  const denied = 'C:\\private\\ungranted.mp4';
  const url = 'revu-media://file/synthetic-token';
  const { bridge, ipc } = loadPreload({ invoke: async () => ({ value: { filePath: allowed }, grants: [[allowed, url]] }) });
  assert.equal(bridge.resolveMedia(allowed), null);
  await bridge.invoke('get_vod', { gameId: 101 });
  assert.equal(bridge.resolveMedia(allowed), url);
  assert.equal(bridge.resolveMedia(denied), null);
  ipc.emit('revu:media-clear');
  assert.equal(bridge.resolveMedia(allowed), null);
});

test('window bridge maps only fixed actions to its native channel', async () => {
  const { bridge, calls } = loadPreload();
  await bridge.window.minimize('ignored caller argument');
  await bridge.window.setFocus();
  assert.deepEqual(calls, [['revu:window', 'minimize'], ['revu:window', 'setFocus']]);
  assert.equal(bridge.window.execute, undefined);
});

test('normal host advertises migrated capabilities and external links use a dedicated channel', async () => {
  const { bridge, calls } = loadPreload({ isolated: false });
  assert.equal(bridge.capabilities.updates, true);
  assert.equal(bridge.capabilities.externalLinks, true);
  assert.equal(bridge.capabilities.recorder, true); // API support; native readiness is get_recording_status.
  await bridge.openExternal('https://revu.gg');
  assert.deepEqual(calls, [['revu:open-external', 'https://revu.gg']]);
});

test('sender validation rejects frame replacement, other windows, credentials and lookalike origins', () => {
  const mainFrame = { url: 'revu-app://ui/index.html' };
  const contents = { mainFrame };
  validateSender({ sender: contents, senderFrame: mainFrame }, contents);
  assert.throws(() => validateSender({ sender: { mainFrame }, senderFrame: mainFrame }, contents));
  assert.throws(() => validateSender({ sender: contents, senderFrame: { url: mainFrame.url } }, contents));
  assert.throws(() => validateSender({ sender: contents, senderFrame: null }, contents));
  for (const url of ['https://ui/index.html', 'revu-app://user@ui/index.html',
    'revu-app://ui.evil/index.html', 'revu-app://ui:80/index.html', 'file:///index.html']) {
    assert.equal(isAppUrl(url), false, url);
    mainFrame.url = url;
    assert.throws(() => validateSender({ sender: contents, senderFrame: mainFrame }, contents), url);
  }
});
