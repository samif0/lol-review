import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { planWithoutThen, recordLabel, shortDate } from '../ui/rule-labels.mjs';

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
