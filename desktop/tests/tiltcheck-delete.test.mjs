import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// P-042: the real Tilt check renderer and its Delete write, on a minimal DOM. The
// production template's markup is checked separately below.
const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
const source = (await read('tiltcheck.js')).replace(/^import .*?;\r?\n/gm, '');
const html = await read('tiltcheck.html');
const plain = value => JSON.parse(JSON.stringify(value));
const tick = () => new Promise(resolve => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 20; i++) await tick(); };
const deferred = () => {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
};

const STRAY_PLAN = 'If I lose, then I queue again right away.';
const CALM_PLAN = 'If I fall two levels behind, then I farm for 60 seconds.';
const PREGAME_PLAN = 'If I start to flame, then I mute all.';
const entry = (id, emotion, extra = {}) => ({
  id, emotion, intensityBefore: 8, intensityAfter: 4, intensityReduction: 4, reframeThought: '',
  reframeResponse: 'One game is one data point.', thoughtType: '', cueWord: '', focusIntention: '', gameId: null,
  ifThenPlan: '', hasPlan: false, createdAt: 1_760_000_000, createdAtText: 'Oct 19, 22:40', emotionColorHex: '#f3a3a8', ...extra,
});
const snapshot = (recent, stats, latestPlan = null) => ({
  generatedAt: '2026-10-20T10:00:00', recent, hasRecent: recent.length > 0,
  stats: { total: 0, avgBefore: 0, avgAfter: 0, avgReduction: 0, avgReductionText: '', topEmotions: [], ...stats },
  latestPlan, hasLatestPlan: !!latestPlan,
});
const stray = entry(12, 'frustrated', { intensityBefore: 10, intensityAfter: 2, intensityReduction: 8,
  ifThenPlan: STRAY_PLAN, hasPlan: true });
const calm = entry(11, 'anxious', { intensityBefore: 6, intensityAfter: 3, intensityReduction: 3,
  ifThenPlan: CALM_PLAN, hasPlan: true, createdAtText: 'Oct 18, 21:05' });
const pregame = entry(10, 'pregame_plan', { intensityBefore: 0, intensityAfter: null, intensityReduction: null,
  reframeResponse: '', ifThenPlan: PREGAME_PLAN, hasPlan: true, createdAtText: 'Oct 17, 19:30' });
const BEFORE = snapshot([stray, calm, pregame], { total: 2, avgBefore: 8, avgAfter: 2.5, avgReduction: 5.5,
  topEmotions: [{ emotion: 'frustrated', count: 1 }, { emotion: 'anxious', count: 1 }, { emotion: 'pregame_plan', count: 1 }] },
STRAY_PLAN);
const AFTER = snapshot([calm, pregame], { total: 1, avgBefore: 6, avgAfter: 3, avgReduction: 3,
  topEmotions: [{ emotion: 'anxious', count: 1 }, { emotion: 'pregame_plan', count: 1 }] }, CALM_PLAN);
const EMPTY = snapshot([], {});

function fixture({ snapshots, invoke, confirm = () => true }) {
  const ids = new Map(), listeners = new Map(), reads = [], writes = [], asked = [];
  function element() {
    const classes = new Set(), attributes = new Map();
    return {
      children: [], dataset: {}, style: {}, textContent: '', hidden: false, disabled: false, parts: new Map(), parent: null,
      set innerHTML(_) { throw new Error('Tilt check text must never be parsed as HTML'); },
      classList: {
        add: (...names) => names.forEach(name => classes.add(name)),
        remove: (...names) => names.forEach(name => classes.delete(name)),
        contains: name => classes.has(name),
        toggle(name, force) { const on = force ?? !classes.has(name); if (on) classes.add(name); else classes.delete(name); return on; },
      },
      get firstChild() { return this.children[0] || null; },
      appendChild(child) { this.children.push(child); child.parent = this; return child; },
      querySelector(selector) { return this.parts.get(selector) || null; },
      querySelectorAll() { return []; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      closest(selector) { return selector === '[data-action]' && this.dataset.action ? this : this.parent?.closest(selector) ?? null; },
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  const status = element();
  const document = {
    readyState: 'loading',
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
    removeEventListener() {},
    querySelector: selector => (selector === '#statusline b' ? status : null),
  };
  const PARTS = {
    'tpl-recent': ['.tc-row-emo', '.tc-row-when', '.tc-row-plan', '.tc-row-text', '.tc-row-before', '.tc-row-arr', '.tc-row-after', '.tc-row-drop'],
    'tpl-stat': ['.k', '.v', '.s'],
    'tpl-emotion': ['.tc-emo-name', '.tc-emo-n'],
  };
  const context = vm.createContext({
    document, window: { confirm(message) { asked.push(message); return confirm(message); } },
    console: { error() {}, info() {} }, $,
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    tpl(name) {
      const node = element();
      for (const selector of PARTS[name] || []) { const part = element(); node.parts.set(selector, part); node.appendChild(part); }
      if (name === 'tpl-recent') {
        // The template's Delete button (markup pinned by the last test).
        const del = element(); del.dataset.action = 'delete_entry';
        node.parts.set('.tc-row-del', del); node.appendChild(del);
      }
      return node;
    },
    readSnapshot: async command => { reads.push(command); return snapshots(reads.length); },
    getInvoke: async () => (invoke ? async (command, args) => { writes.push({ command, args: plain(args) }); return invoke(command, args); } : null),
  });
  vm.runInContext(`${source}\n globalThis.hooks = { loadTiltcheck, deleteEntry, deleteMessage };`, context);
  const rows = () => $('tc-recent').children;
  return {
    $, hooks: context.hooks, reads, writes, asked, rows,
    load: () => context.hooks.loadTiltcheck(),
    del: row => row.querySelector('.tc-row-del'),
    ids: () => rows().map(row => row.querySelector('.tc-row-del').dataset.entryId),
    strip: () => $('tc-strip').children.map(cell => [cell.querySelector('.k').textContent, cell.querySelector('.v').textContent]),
    status: () => status.textContent,
    async click(target) {
      const event = { target, preventDefault() {} };
      for (const handler of listeners.get('click') || []) handler(event);
      await settle();
    },
  };
}

test('every recent row carries its id on its Delete, named for screen readers, and row text stays text', async () => {
  const hostile = entry(13, '<img src=x onerror=alert(1)>', { createdAtText: '<b>Oct 20</b>' });
  const f = fixture({ snapshots: () => snapshot([...BEFORE.recent, hostile], BEFORE.stats, STRAY_PLAN) });
  await f.load();

  assert.deepEqual(f.ids(), ['12', '11', '10', '13']);
  assert.deepEqual(f.rows().map(row => f.del(row).getAttribute('aria-label')), [
    'Delete the reset from Oct 19, 22:40',
    'Delete the reset from Oct 18, 21:05',
    'Delete the plan from Oct 17, 19:30',
    'Delete the reset from <b>Oct 20</b>',
  ]);
  assert.equal(f.rows()[3].querySelector('.tc-row-emo').textContent, '<img src=x onerror=alert(1)>');
});

test('Delete asks first, says what goes, and writes nothing when the player declines', async () => {
  const f = fixture({ snapshots: () => BEFORE, invoke: async () => ({ ok: true }), confirm: () => false });
  await f.load();

  for (const row of f.rows()) await f.click(f.del(row));

  const withPlan = 'Delete this reset? It comes out of your stats, and its plan stops showing before games. This cannot be undone.';
  assert.deepEqual(f.asked, [withPlan, withPlan, 'Delete this plan? It stops showing before games. This cannot be undone.']);
  assert.equal(f.hooks.deleteMessage({ emotion: 'tense', hasPlan: false }),
    'Delete this reset? It comes out of your stats. This cannot be undone.');
  for (const message of f.asked) assert.doesNotMatch(message, /[–—]/);
  assert.deepEqual(f.writes, []);
  assert.equal(f.reads.length, 1, 'no reload without a delete');
});

test('a confirmed Delete sends the id, then the reload shows the new stats, list and latest plan', async () => {
  const f = fixture({ snapshots: n => (n === 1 ? BEFORE : AFTER), invoke: async () => ({ ok: true }) });
  await f.load();
  assert.equal(f.$('tc-plan-v').textContent, STRAY_PLAN);

  await f.click(f.del(f.rows()[0]));

  assert.deepEqual(f.writes, [{ command: 'delete_tiltcheck', args: { payload: { id: 12 } } }]);
  assert.deepEqual(f.reads, ['get_tiltcheck', 'get_tiltcheck']);
  assert.deepEqual(f.ids(), ['11', '10']);
  assert.deepEqual(f.strip().slice(0, 4), [['Resets', '1'], ['Avg before', '6'], ['Avg after', '3'], ['Avg drop', '3']]);
  assert.equal(f.status(), '1 reset logged · 6 → 3 typical');
  assert.equal(f.$('tc-plan-v').textContent, CALM_PLAN);
  assert.equal(f.$('tc-plan').hidden, false);
});

test('deleting the last entry empties the list and hides the plan', async () => {
  const f = fixture({ snapshots: n => (n === 1 ? snapshot([pregame], {}, PREGAME_PLAN) : EMPTY), invoke: async () => ({ ok: true }) });
  await f.load();

  await f.click(f.del(f.rows()[0]));

  assert.deepEqual(f.writes.map(w => w.args.payload.id), [10]);
  assert.deepEqual(f.rows(), []);
  assert.equal(f.$('tc-empty').hidden, false);
  assert.equal(f.$('tc-plan').hidden, true);
  assert.equal(f.status(), 'No resets yet.');
});

test('a failed delete shows the error, keeps the row and lets the player try again', async () => {
  const f = fixture({ snapshots: () => BEFORE, invoke: async () => { throw new Error('The database is busy.'); } });
  await f.load();
  const del = f.del(f.rows()[1]);

  await f.click(del);

  assert.equal(f.$('errpanel').hidden, false);
  assert.equal(f.$('err-detail').textContent, 'The database is busy.');
  assert.equal(del.disabled, false);
  assert.equal(f.reads.length, 1, 'nothing changed, so nothing is reloaded');
  assert.deepEqual(f.ids(), ['12', '11', '10']);
});

test('a delete that lands while a reload is in flight still gets a reload of its own', async () => {
  const held = deferred();
  const BOTH = snapshot([pregame], {}, PREGAME_PLAN);
  const f = fixture({ snapshots: n => (n === 1 ? BEFORE : n === 2 ? held.promise : BOTH), invoke: async () => ({ ok: true }) });
  await f.load();
  const [first, second] = f.rows();

  const one = f.hooks.deleteEntry(f.del(first));
  await settle();
  assert.equal(f.reads.length, 2, 'the first delete reloads; that read is still in flight');
  const two = f.hooks.deleteEntry(f.del(second));
  await settle();
  assert.equal(f.reads.length, 2, 'the second delete waits for the read in flight');

  held.resolve(AFTER);
  await Promise.all([one, two]);

  assert.deepEqual(f.writes.map(w => w.args.payload.id), [12, 11]);
  assert.equal(f.reads.length, 3, 'one follow-up read after the second delete');
  assert.deepEqual(f.ids(), ['10']);
});

test('the row template holds one quiet kit Delete button with a label and a decorative icon', () => {
  const row = html.match(/<template id="tpl-recent">([\s\S]*?)<\/template>/)[1];
  const buttons = [...row.matchAll(/<button[^>]*>/g)].map(match => match[0]);
  assert.equal(buttons.length, 1);
  for (const attribute of ['class="ui-iconbtn tc-row-del"', 'type="button"', 'data-action="delete_entry"',
    'title="Delete"', 'aria-label="Delete this entry"']) assert.ok(buttons[0].includes(attribute), attribute);
  assert.match(row, /<button[^>]*><svg viewBox="0 0 24 24" aria-hidden="true">/);
});
