import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { planWithoutThen, recordLabel, ruleBadgeTone, shortDate } from '../ui/rule-labels.mjs';

const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');

const today = new Date(2026, 9, 12);

test('rule records read in sentence case with a hyphenated record and a short date', () => {
  assert.equal(recordLabel('TRIPPED 3× (1W–2L) · BASELINE WR 54% · LAST 2026-06-10', today),
    'Tripped 3× (1W-2L) · Baseline WR 54% · Last Jun 10');
  assert.equal(recordLabel('TRIPPED 7× · WR WHEN TRIPPED 38% VS 52% BASELINE · LAST 2025-12-30', today),
    'Tripped 7× · WR when tripped 38% vs 52% baseline · Last Dec 30, 2025');
  assert.equal(recordLabel('HELD 3× THIS WEEK · OVERRIDDEN 1×', today), 'Held 3× this week · Overridden 1×');
  assert.equal(recordLabel('NO TRIPS ON RECORD', today), 'No trips on record');
  assert.equal(shortDate('not a date', today), 'not a date');
});

test('the rule-check banner never prints "Then Then"', async () => {
  assert.equal(planWithoutThen('Then I will close the client and walk.'), 'I will close the client and walk.');
  assert.equal(planWithoutThen('then, stand up'), 'stand up');
  assert.equal(planWithoutThen('Take a walk'), 'Take a walk');
  assert.equal(planWithoutThen('Thenceforth I rest'), 'Thenceforth I rest', 'only the word Then is dropped');
  assert.equal(planWithoutThen('Then'), 'Then', 'a plan that is only "Then" stays visible');
  assert.equal(planWithoutThen(null), '');

  // The sample shows the real banner input: the plan as the sidecar sends it.
  const sample = JSON.parse(await readFile(new URL('../ui/sample-rules.json', import.meta.url), 'utf8'));
  const plans = sample.activeRules.map(r => r.replacementPlan).filter(Boolean);
  assert.ok(plans.some(p => /^Then /.test(p)));
  for (const plan of plans) assert.doesNotMatch(`Then ${planWithoutThen(plan)}`, /^Then then\b/i);
});

test('the rule-check banner template joins name and reason without an em dash', async () => {
  const html = await readFile(new URL('../ui/rules.html', import.meta.url), 'utf8');
  const banner = html.match(/<template id="tpl-vbanner">([\s\S]*?)<\/template>/)[1];
  assert.doesNotMatch(banner, /—/);
  // The item is not flex, so whitespace before the colon would render "Name : Reason".
  assert.match(banner, /<span class="rule-check-name"><\/span><span class="rule-check-dash" aria-hidden="true">:<\/span>/);
});

test('the type badge carries a tone instead of the sidecar hex', () => {
  assert.equal(ruleBadgeTone({ ruleType: 'loss_streak', accentHex: '#9d8bff' }), 'muted');
  assert.equal(ruleBadgeTone({ ruleType: 'max_games' }), 'muted');
  assert.equal(ruleBadgeTone({ ruleType: 'custom', accentHex: '#7B8494' }), 'neutral');
  assert.equal(ruleBadgeTone({ ruleType: ' Custom ' }), 'neutral');
  assert.equal(ruleBadgeTone({}), 'neutral', 'an unknown rule reads quiet');
  assert.equal(ruleBadgeTone(null), 'neutral');
});

// A minimal element: every selector resolves to its own child node.
function node() {
  const children = {};
  const classes = new Set();
  return { dataset: {}, style: {}, hidden: false, textContent: '', title: '',
    classList: { add: (...names) => names.forEach(c => classes.add(c)), remove: (...names) => names.forEach(c => classes.delete(c)),
      toggle: (c, on) => ((on ?? !classes.has(c)) ? classes.add(c) : classes.delete(c)), contains: c => classes.has(c) },
    querySelector: sel => (children[sel] ||= node()) };
}

test('rule cards paint no sidecar color and mark the player\'s own words', async () => {
  const source = (await read('rules.js')).replace(/^import .*?;\r?\n/gm, '');
  assert.doesNotMatch(source, /accentHex/, 'the sidecar hex is never read');
  assert.doesNotMatch(source, /style\.(color|borderColor|background)/);
  const card = node();
  const context = vm.createContext({
    document: { readyState: 'complete', addEventListener() {}, querySelector: () => null }, window: { location: {} }, console,
    $: () => null, show: (el, on) => { if (el) el.hidden = !on; }, clear() {}, tpl: () => card,
    readSnapshot: () => new Promise(() => {}), getInvoke: async () => null,
    registerLeaveGuard: () => () => {}, trackForm: () => ({ isDirty: () => false, mark() {} }),
    recordLabel, planWithoutThen, ruleBadgeTone,
  });
  vm.runInContext(source, context);
  const sample = JSON.parse(await read('sample-rules.json'));
  context.buildRule(sample.activeRules.find(r => r.ruleType === 'loss_streak'));
  const badge = card.querySelector('.rule-badge');
  assert.equal(badge.dataset.tone, 'muted');
  assert.deepEqual(badge.style, {}, 'no inline color or border');
  context.buildRule(sample.activeRules.find(r => r.ruleType === 'custom'));
  assert.equal(badge.dataset.tone, 'neutral');

  const html = await read('rules.html');
  const template = html.match(/<template id="tpl-rule">([\s\S]*?)<\/template>/)[1];
  for (const cls of ['rule-name', 'rule-desc', 'rule-plan-t']) {
    assert.match(template, new RegExp(`class="${cls} own-words"`), cls);
  }
  assert.match(html, /class="rule-check-plan own-words"/);
});

test('the rules page keeps its meaning colors: ok checks, pending trips, If and Then legs', async () => {
  const css = await read('rules.css');
  assert.match(css, /\.rule-status\.ok\{background:color-mix\(in srgb,var\(--ok\) 14%,transparent\);color:var\(--ok\)\}/);
  assert.match(css, /\.rule-status\.tripped\{background:var\(--pending-soft\);color:var\(--pending\)\}/);
  assert.match(css, /\.rule-badge\[data-tone\]\{color:var\(--tone\)\}/);
  assert.match(css, /\.rule-plan-k\{[^}]*color:var\(--ok\)/);
});
