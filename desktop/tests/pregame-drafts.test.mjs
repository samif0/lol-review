import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// The real debounce/flush code of the pre-game prompt boxes.
const source = await readFile(new URL('../ui/pregame.js', import.meta.url), 'utf8');
const start = source.indexOf('function scheduleDraftSave');
const end = source.indexOf('registerLeaveGuard({ autosave: flushDrafts });');
assert.ok(start >= 0 && end > start, 'pregame draft section must remain identifiable');

function drafts() {
  const timers = new Map(), saves = [];
  let id = 0;
  const context = vm.createContext({
    _draftTimers: new Map(),
    setTimeout: fn => { timers.set(++id, fn); return id; }, clearTimeout: timer => timers.delete(timer),
    getInvoke: async () => async (command, { payload }) => { saves.push([command, JSON.parse(JSON.stringify(payload))]); },
    console: { info() {}, error() {} },
  });
  vm.runInContext(`${source.slice(start, end)}\nglobalThis.api = { scheduleDraftSave, flushDrafts };`, context);
  return { ...context.api, saves, fire: () => { for (const [key, fn] of [...timers]) { timers.delete(key); fn(); } }, timers };
}

test('the game starting flushes the last keystrokes of every prompt answer at once, without a duplicate later save', async () => {
  const d = drafts();
  d.scheduleDraftSave(7, 'Ward');
  d.scheduleDraftSave(7, 'Ward river');
  d.scheduleDraftSave(9, 'Track jungler');
  assert.equal(await d.flushDrafts(), true);
  assert.deepEqual(d.saves, [['save_pregame_draft', { promptId: 7, text: 'Ward river' }],
    ['save_pregame_draft', { promptId: 9, text: 'Track jungler' }]]);
  assert.equal(d.timers.size, 0, 'the debounce timers are cancelled');
  d.fire(); await Promise.resolve();
  assert.equal(d.saves.length, 2);
});

test('the ordinary debounce still saves once, and leaving afterwards has nothing left to send', async () => {
  const d = drafts();
  d.scheduleDraftSave(3, 'First');
  d.scheduleDraftSave(3, 'Final');
  d.fire(); await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(d.saves, [['save_pregame_draft', { promptId: 3, text: 'Final' }]]);
  await d.flushDrafts();
  assert.equal(d.saves.length, 1);
});

test('both ways off the pre-game page flush drafts first', () => {
  assert.match(source, /registerLeaveGuard\(\{ autosave: flushDrafts \}\)/, 'the shell auto-show awaits the flush');
  assert.match(source, /case 'gameInProgress':[\s\S]*?flushDrafts\(\)\.then\(\(\) => \{ window\.location\.href = 'ingame\.html'; \}\)/);
  assert.match(source, /addEventListener\('pagehide', \(\) => \{ void flushDrafts\(\); \}\)/);
});
