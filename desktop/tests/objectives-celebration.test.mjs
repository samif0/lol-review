import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// The objective-complete celebration is a retention moment: it must be announced,
// take focus, and wait for the player instead of vanishing on a timer.
const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');

function element(id, props = {}) {
  return { id, hidden: false, textContent: '', focused: 0, isConnected: true, focus() { this.focused++; }, ...props };
}

async function page() {
  const source = (await read('objectives.js')).replace(/^import .*?;\r?\n/gm, '');
  const els = Object.fromEntries([
    element('celebrate', { hidden: true }), element('celebrate-title'), element('celebrate-stats'),
    element('celebrate-continue'), element('new-objective'),
  ].map(el => [el.id, el]));
  const listeners = {};
  const timers = [];
  const document = { readyState: 'complete', activeElement: null,
    addEventListener: (type, fn) => (listeners[type] ||= []).push(fn), querySelector: () => null };
  const context = {
    document, console, window: { addEventListener() {}, location: {} },
    setTimeout: (fn, ms) => { timers.push(ms); return timers.length; }, clearTimeout() {},
    readSnapshot: () => new Promise(() => {}), getInvoke: async () => null,
    $: id => els[id] ?? null, show: (el, visible) => { if (el) el.hidden = !visible; }, clear() {}, tpl() {},
    objectiveDisplayText: v => String(v ?? ''), objectiveTypeLabel: () => '', objectivePhaseLabel: v => String(v ?? ''),
    registerLeaveGuard: () => () => {}, trackForm: () => ({ isDirty: () => false, mark: () => {} }), changedFromDefaults: () => false,
    renderObjectiveProgress: () => ({ score: 0, ready: false }), TRACKABLE_EVENT_TOKENS: [],
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  const key = (keyName) => {
    const ev = { key: keyName, target: {}, prevented: false, preventDefault() { this.prevented = true; } };
    listeners.keydown.forEach(fn => fn(ev));
    return ev;
  };
  return { context, els, document, timers, key };
}

test('the celebration is an announced modal dialog without HUD brackets', async () => {
  const html = await read('objectives.html');
  const overlay = html.match(/<div class="obj-celebrate"[\s\S]*?>/)[0];
  assert.match(overlay, /role="alertdialog"/);
  assert.match(overlay, /aria-modal="true"/);
  assert.match(overlay, /aria-labelledby="celebrate-head"/);
  assert.match(html, /id="celebrate-head"/);
  assert.doesNotMatch(html, /obj-celebrate-bracket/);
});

test('completing an objective moves focus to Continue and waits for the player', async () => {
  const { context, els, document, timers, key } = await page();
  // The Complete button that opened it has been re-rendered away.
  document.activeElement = element('old-complete', { isConnected: false });
  context.showCelebration({ title: 'Ward before pushing', score: 52, gameCount: 1 });
  assert.equal(els.celebrate.hidden, false);
  assert.equal(els['celebrate-continue'].focused, 1);
  assert.equal(els['celebrate-stats'].textContent, '52 points · 1 game played');
  assert.deepEqual(timers, [], 'no auto-dismiss timer');

  assert.equal(key('Tab').prevented, true, 'Tab stays inside the modal');
  assert.equal(els['celebrate-continue'].focused, 2);
  assert.equal(els.celebrate.hidden, false);

  assert.equal(key('Escape').prevented, true);
  assert.equal(els.celebrate.hidden, true);
  assert.equal(els['new-objective'].focused, 1, 'focus returns to a control that still exists');
});
