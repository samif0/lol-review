import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// 3.14.1: remake verdict changes reach the pages that show counts and labels, and a
// hard stop that no longer holds is cleared without any navigation.
const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
class CustomEvent { constructor(type, options) { this.type = type; this.detail = options.detail; } }

async function shellFor(page) {
  const code = outer.slice(outer.indexOf('async function wireLiveAutoShow()'), outer.indexOf('\nwireLiveAutoShow();'));
  const events = [], navigations = [], commands = [];
  let callback, cleared = 0;
  const scope = { location: { pathname: `/${page}`, href: page }, dispatchEvent: event => events.push(event) };
  const context = vm.createContext({
    window: scope, frame: { contentWindow: scope },
    frameHas: name => name === page, frameGoto: target => navigations.push(target),
    getInvoke: async () => async (command) => { commands.push(command); return { firstReviewTutorialStep: 'wait_game' }; },
    getListen: async () => async (_name, listener) => { callback = listener; },
    clearHardStop: () => { cleared++; },
    CustomEvent, console: { warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.ready = wireLiveAutoShow();`, context);
  await context.ready;
  return {
    events, navigations, commands, get cleared() { return cleared; },
    emit: (type, payload) => callback({ payload: { type, payload } }),
  };
}

test('remakeUpdated reaches review, games and dashboard frames unchanged, never navigates', async () => {
  const payload = { gameId: 42, isRemake: true };
  for (const page of ['review.html', 'games.html', 'dashboard.html', 'vodplayer.html', 'patterns.html']) {
    const shell = await shellFor(page);
    shell.emit('remakeUpdated', payload);
    assert.deepEqual(shell.navigations, [], page);
    if (['vodplayer.html', 'patterns.html'].includes(page)) { assert.equal(shell.events.length, 0, page); continue; }
    assert.deepEqual(shell.events.map(event => event.type), ['revu:remake-updated'], page);
    assert.deepEqual(shell.events[0].detail, payload, page);
  }
});

test('hardStopCleared clears the lock bar', async () => {
  const shell = await shellFor('dashboard.html');
  shell.emit('hardStopCleared', { ruleId: 7 });
  assert.equal(shell.cleared, 1);
  assert.deepEqual(shell.navigations, []);
});

test('a remake game end skips the first-review handoff but still leaves the live surface', async () => {
  const shell = await shellFor('ingame.html');
  shell.emit('gameEnded', { gameId: 42, saved: true, isRemake: true });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.ok(!shell.commands.includes('get_config'), 'the tutorial config is never read for a remake');
  assert.ok(!shell.commands.includes('save_config'));
  assert.deepEqual(shell.navigations, ['dashboard.html']);
});

test('a saved game end records the first-review handoff and then leaves the live surface', async () => {
  const shell = await shellFor('ingame.html');
  shell.emit('gameEnded', { gameId: 42, saved: true });
  for (let i = 0; i < 4; i++) await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(shell.commands.filter(command => command.endsWith('_config')), ['get_config', 'save_config']);
  assert.deepEqual(shell.navigations, ['dashboard.html']);
});
