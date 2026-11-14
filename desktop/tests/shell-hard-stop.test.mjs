import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// The hard-stop card on the shell. A cancelled queue search takes the screen. A match
// that was already found is the player's to answer: the card shows inside Revu and the
// window stays where it is, so it never covers the League client's own prompt.
const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
const markup = await readFile(new URL('../ui/index.html', import.meta.url), 'utf8');
const code = outer.slice(outer.indexOf('const HARDSTOP_HOLD_S'), outer.indexOf('\nwireHardStop();'));
const IDS = ['hardstop', 'hardbar', 'hardstop-h', 'hardstop-foot', 'hardstop-rule', 'hardstop-reason', 'hardstop-plan',
  'hardstop-plan-t', 'hardstop-count', 'hardbar-txt', 'hardstop-override', 'hardstop-ok', 'hardbar-btn'];

function shell() {
  const elements = Object.fromEntries(IDS.map(id => [id, { id, hidden: true, textContent: '', disabled: false, focus() {}, addEventListener() {} }]));
  const windowCalls = [];
  const win = Object.fromEntries(['unminimize', 'show', 'setFocus'].map(name => [name, async () => { windowCalls.push(name); }]));
  const context = vm.createContext({
    document: { getElementById: id => elements[id] ?? null, addEventListener() {} },
    getWindow: async () => win,
    getInvoke: async () => null,
    setInterval: () => 1, clearInterval() {}, console, Date, Math, Number, String,
  });
  vm.runInContext(`${code}\nglobalThis.api = { showHardStop, clearHardStop, hardStop };`, context);
  return { elements, windowCalls, ...context.api };
}

const snapshot = (action, extra = {}) => ({
  ruleId: 7, ruleName: 'Three games a day', reason: 'Already played 3/3 games today', conditionCue: 'Max 3 games per day',
  replacementPlan: 'walk it off', hasPlan: true, action, unlockAt: null, at: 1000, ...extra,
});
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test('every element the card fills exists in the shell markup', () => {
  for (const id of IDS) assert.ok(markup.includes(`id="${id}"`), id);
});

test('a cancelled queue search opens the card and brings Revu forward', async () => {
  const s = shell();
  s.showHardStop(snapshot('cancelled_queue'), true);
  await settle();
  assert.equal(s.elements['hardstop-h'].textContent, 'Revu cancelled your queue.');
  assert.match(s.elements['hardstop-foot'].textContent, /keeps cancelling the queue/);
  assert.equal(s.elements.hardstop.hidden, false);
  assert.deepEqual(s.windowCalls, ['unminimize', 'show', 'setFocus']);
});

test('a found match shows the rule and the plan and never touches the window', async () => {
  const s = shell();
  s.showHardStop(snapshot('match_found'), true);
  await settle();
  assert.equal(s.elements['hardstop-h'].textContent, 'A match was found. Revu left it for you to answer.');
  assert.match(s.elements['hardstop-foot'].textContent, /never accepts or declines a match for you/);
  assert.equal(s.elements['hardstop-rule'].textContent, 'Three games a day');
  assert.equal(s.elements['hardstop-plan-t'].textContent, 'walk it off');
  assert.equal(s.elements['hardstop-plan'].hidden, false);
  assert.equal(s.elements.hardstop.hidden, false);
  assert.deepEqual(s.windowCalls, [], 'the League client keeps the screen while its prompt is up');
});

test('a replayed found-match notice is the slim bar, and clearing it hides everything', async () => {
  const s = shell();
  s.showHardStop(snapshot('match_found'), false);
  await settle();
  assert.equal(s.elements.hardstop.hidden, true);
  assert.equal(s.elements.hardbar.hidden, false);
  assert.deepEqual(s.windowCalls, []);
  s.clearHardStop();
  assert.equal(s.elements.hardbar.hidden, true);
  assert.equal(s.hardStop.snap, null);
});

test('the search being cancelled after a found match replaces the notice with the lock', async () => {
  const s = shell();
  s.showHardStop(snapshot('match_found'), true);
  s.showHardStop(snapshot('cancelled_queue', { at: 1010 }), true);
  await settle();
  assert.equal(s.elements['hardstop-h'].textContent, 'Revu cancelled your queue.');
  assert.deepEqual(s.windowCalls, ['unminimize', 'show', 'setFocus']);
});

test('the card copy has no dashes and no claim that Revu answers a match', () => {
  const copy = code.slice(code.indexOf('const HARDSTOP_COPY'), code.indexOf('const isMatchFound'));
  assert.doesNotMatch(copy, new RegExp(`[${String.fromCharCode(0x2013, 0x2014)}]`));
  assert.doesNotMatch(outer, /declined the match/i);
});
