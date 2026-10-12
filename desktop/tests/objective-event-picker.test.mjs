import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { MANUAL_EVENT_GROUP, TRACKABLE_EVENT_TOKENS } from '../ui/objective-labels.mjs';

// The create form renders TRACKABLE_EVENT_TOKENS before any sidecar snapshot names the
// catalog, so it must match the Core vocabulary the edit form and the matcher use.
const source = await readFile(new URL('../../src/Revu.Core/Models/GameEvent.cs', import.meta.url), 'utf8');

function coreCatalog() {
  const constants = new Map();
  for (const [, name, value] of source.matchAll(/const string (\w+) = "([^"]+)"/g)) constants.set(name, value);
  for (const [, name, ref] of source.matchAll(/const string (\w+) = EventTypes\.(\w+);/g)) constants.set(name, constants.get(ref));
  const resolve = expr => {
    const literal = expr.match(/^"(.*)"$/);
    if (literal) return literal[1];
    const value = constants.get(expr.replace(/^EventTypes\./, ''));
    assert.ok(value, `unresolved catalog constant ${expr}`);
    return value;
  };
  const body = source.slice(source.indexOf('> Catalog ='), source.indexOf('];', source.indexOf('> Catalog =')));
  return [...body.matchAll(/\(([\w.]+),\s*("[^"]*"|\w+),\s*"([^"]*)",\s*"(#[0-9a-f]{6})"\)/gi)]
    .map(([, token, group, label, color]) => ({ token: resolve(token), group: resolve(group), label, color }));
}

test('the create-form picker matches the Core trackable token catalog', () => {
  const core = coreCatalog();
  assert.equal(core.length, 25, 'every catalog row was parsed');
  assert.deepEqual(TRACKABLE_EVENT_TOKENS, core);
});

test('only events Revu detects on its own sit outside the manual group', () => {
  const detected = TRACKABLE_EVENT_TOKENS.filter(o => o.group !== MANUAL_EVENT_GROUP).map(o => o.token);
  assert.deepEqual(detected.sort(), ['ASSIST', 'BARON', 'DEATH', 'DRAGON', 'FIRST_BLOOD', 'HERALD',
    'INHIBITOR', 'KILL', 'MULTI_KILL', 'TURRET']);
  for (const token of ['TEAMFIGHT', 'TRADE', 'ALL_IN', 'RECALL', 'JUNGLE_GANK', 'FOG_DEATH', 'JUNGLE_PROXIMITY']) {
    assert.equal(TRACKABLE_EVENT_TOKENS.find(o => o.token === token)?.group, MANUAL_EVENT_GROUP, token);
  }
  // Groups render in first-seen order, so the manual group must come last.
  const groups = [...new Set(TRACKABLE_EVENT_TOKENS.map(o => o.group))];
  assert.deepEqual(groups, ['Combat', 'Map objectives', MANUAL_EVENT_GROUP]);
});
