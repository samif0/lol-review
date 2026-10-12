import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Add a game manually: the real page script against a minimal document.
const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');

function element(id, props = {}) {
  return { id, value: '', checked: false, hidden: true, textContent: '', disabled: false, focused: 0,
    focus() { this.focused++; }, addEventListener() {}, setAttribute() {}, querySelectorAll: () => [], ...props };
}

async function page() {
  const source = (await read('manualentry.js')).replace(/^import .*?;\r?\n/gm, '');
  const els = Object.fromEntries(['me-champ', 'me-win', 'me-loss', 'me-k', 'me-d', 'me-a', 'me-mental',
    'me-mental-val', 'me-notes', 'me-obj-list', 'me-obj-card', 'me-error', 'errpanel', 'err-detail']
    .map(id => [id, element(id)]));
  els['me-mental'].value = '7';
  els['me-k'].value = '4'; els['me-d'].value = '-2'; els['me-a'].value = 'x';
  const calls = [];
  const listeners = {};
  const location = { href: '' };
  const context = {
    console, window: { location },
    document: { readyState: 'complete', addEventListener: (type, fn) => (listeners[type] ||= []).push(fn) },
    fetch: async () => ({ ok: false }),
    getInvoke: async () => async (command, args) => {
      if (command === 'get_active_objectives') return { objectives: [] };
      calls.push([command, args]);
      return { ok: true };
    },
    $: id => els[id] ?? null, show: (el, visible) => { if (el) el.hidden = !visible; }, clear() {}, tpl() {},
    objectivePhaseLabel: value => String(value ?? ''),
    registerLeaveGuard: () => () => {}, trackForm: () => ({ isDirty: () => false, mark: () => {} }), changedFromDefaults: () => false,
  };
  vm.createContext(context);
  vm.runInContext(source, context);
  const save = async () => {
    const button = element('me-save', { dataset: { action: 'save' } });
    button.closest = () => button;
    for (const fn of listeners.click) await fn({ target: button, preventDefault() {} });
  };
  return { els, calls, location, save };
}

test('the form has real labels, sentence case and no preselected result', async () => {
  const html = await read('manualentry.html');
  assert.match(html, /<title>Revu: Add a game manually<\/title>/);
  assert.match(html, /<h1>Add a game manually<\/h1>/);
  assert.match(html, /<label class="me-field-lbl" for="me-champ">Champion<\/label>/);
  for (const id of ['me-k', 'me-d', 'me-a', 'me-mental', 'me-notes']) assert.match(html, new RegExp(`<label[^>]*for="${id}"`), id);
  assert.match(html, /<legend class="me-field-lbl">Result<\/legend>/);
  assert.doesNotMatch(html, /name="result"[^>]*checked/, 'Defeat is not recorded by default');
  // The reflection field is the Review page's takeaway question.
  assert.match(await read('review.js'), /'One thing to take into your next game'/,
    'manual entry mirrors the Review takeaway question; change both together');
  assert.match(html, /for="me-notes">One thing to take into your next game</);
  assert.doesNotMatch(html, /bracket|Log a Game|Save Game Entry|Performance Review/);
});

test('saving without a result asks for one instead of recording a defeat', async () => {
  const { els, calls, save } = await page();
  els['me-champ'].value = 'Ahri';
  await save();
  assert.deepEqual(calls, []);
  assert.equal(els['me-error'].hidden, false);
  assert.equal(els['me-error'].textContent, 'Choose Victory or Defeat.');
  assert.equal(els['me-win'].focused, 1);
});

test('saving a complete form sends the explicit result and the takeaway note', async () => {
  const { els, calls, location, save } = await page();
  els['me-champ'].value = '  Ahri ';
  els['me-loss'].checked = true;
  els['me-notes'].value = ' Ward river before 3:15 ';
  await save();
  assert.equal(calls.length, 1);
  const [command, { payload }] = calls[0];
  assert.equal(command, 'save_manual_game');
  assert.equal(payload.championName, 'Ahri');
  assert.equal(payload.win, false);
  assert.deepEqual([payload.kills, payload.deaths, payload.assists], [4, 0, 0]);
  assert.equal(payload.mentalRating, 7);
  assert.equal(payload.notes, 'Ward river before 3:15');
  assert.equal(location.href, 'games.html');
});
