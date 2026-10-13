import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Settings "Minimize when a game starts" is wired to the persistent shell's real
// live-event handler and the shared native window command.
const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');

async function shellFor(config, { windowApi = true, page = 'dashboard.html', visibility = 'visible' } = {}) {
  const code = outer.slice(outer.indexOf('async function wireLiveAutoShow()'), outer.indexOf('\nwireLiveAutoShow();'));
  const calls = [], navigations = [];
  let callback;
  const win = { minimize: async () => { calls.push('minimize'); } };
  const context = vm.createContext({
    document: { visibilityState: visibility },
    frame: { contentWindow: {} }, frameHas: name => name === page, frameGoto: target => navigations.push(target),
    getInvoke: async () => async command => { calls.push(command); if (config instanceof Error) throw config; return config; },
    getListen: async () => async (_name, listener) => { callback = listener; },
    getWindow: async () => windowApi ? win : null,
    setLcuIndicator() {}, hardStop: { snap: null }, hardStopHolds: () => false, showHardStop() {}, clearHardStop() {},
    console: { warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.ready = wireLiveAutoShow();`, context);
  await context.ready;
  return { calls, navigations,
    emit: async (type, payload = {}) => { callback({ payload: { type, payload } }); await new Promise(resolve => setImmediate(resolve)); } };
}

test('a game starting minimizes the window when the setting is on and still opens the in-game page', async () => {
  const shell = await shellFor({ minimizeDuringGame: true });
  await shell.emit('gameInProgress');
  assert.deepEqual(shell.navigations, ['ingame.html']);
  assert.deepEqual(shell.calls.filter(call => call !== 'start_lcu_events'), ['get_config', 'minimize']);
});

test('the window stays put when the setting is off, unreadable, or there is no native window', async () => {
  for (const [config, options] of [[{ minimizeDuringGame: false }, {}], [{}, {}], [new Error('sidecar restarting'), {}],
    [{ minimizeDuringGame: true }, { windowApi: false }]]) {
    const shell = await shellFor(config, options);
    await shell.emit('gameInProgress');
    assert.ok(!shell.calls.includes('minimize'), JSON.stringify(config));
    assert.deepEqual(shell.navigations, ['ingame.html']);
  }
});

test('only the game-start transition minimizes: champ select and the reconnect replay never do', async () => {
  const shell = await shellFor({ minimizeDuringGame: true });
  await shell.emit('champSelectStarted');
  await shell.emit('liveState', { isGameInProgress: true });
  assert.ok(!shell.calls.includes('minimize'));
  assert.ok(!shell.calls.includes('get_config'));
});

test('a window closed to the tray or started hidden is never minimized, which would show it on the taskbar', async () => {
  const shell = await shellFor({ minimizeDuringGame: true }, { visibility: 'hidden' });
  await shell.emit('gameInProgress');
  assert.ok(!shell.calls.includes('minimize'));
  assert.deepEqual(shell.navigations, ['ingame.html'], 'the in-game page still opens for when the player brings Revu back');
});

test('the host minimizes only a visible window', async () => {
  const main = await readFile(new URL('../electron/main.mjs', import.meta.url), 'utf8');
  assert.match(main, /case 'minimize': if \(window\.isVisible\(\)\) window\.minimize\(\);/);
});
