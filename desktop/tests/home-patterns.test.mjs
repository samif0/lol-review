import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { mountPatternPill, renderPatternPill } from '../ui/home-patterns.mjs';

// Home's pill for a mistake trend ready to review: one calm link to the Patterns page,
// hidden whenever the sidecar holds the nudge back or the check fails, and hidden at once
// when champ select or a game starts while Home stays open.

const ready = { notify: true, readyCount: 2, patternKey: 'bad_objective_evidence:obj3', title: 'Track the jungler',
  momentCount: 4, gameCount: 3, quietReason: '', errorText: '' };

function home() {
  const els = {
    'home-suggest-patterns': { hidden: true, href: 'patterns.html', title: '' },
    'home-suggest-patterns-text': { textContent: 'Review repeated mistakes' },
  };
  return { els, doc: { getElementById: id => els[id] || null }, pill: els['home-suggest-patterns'] };
}

const settle = async () => { for (let i = 0; i < 4; i++) await new Promise(resolve => setImmediate(resolve)); };

test('a ready trend shows one pill that opens it on the Patterns page', () => {
  const { doc, els, pill } = home();
  renderPatternPill(doc, ready);
  assert.equal(pill.hidden, false);
  assert.equal(els['home-suggest-patterns-text'].textContent, 'Review 4 repeated mistakes');
  assert.equal(pill.href, 'patterns.html?pattern=bad_objective_evidence%3Aobj3');
  assert.equal(pill.title, 'Track the jungler, across 3 games');

  for (const quiet of [{ ...ready, notify: false, quietReason: 'recent_review' }, { ...ready, notify: false, quietReason: 'live' },
    { ...ready, errorText: "Couldn't check your patterns." }, { ...ready, momentCount: 0 }, { ...ready, patternKey: '' }, null]) {
    renderPatternPill(doc, quiet);
    assert.equal(pill.hidden, true, JSON.stringify(quiet));
  }
});

test('a failed read shows nothing', async () => {
  const { doc, pill } = home();
  const mounted = mountPatternPill(doc, { read: async () => { throw new Error('sidecar restarting'); }, getListen: async () => null });
  await mounted.ready;
  assert.equal(pill.hidden, true);
});

test('champ select or a game hides the pill at once while Home stays open; their end asks again', async () => {
  const { doc, pill } = home();
  const reads = [];
  let listener = null, reply = ready, release = null;
  const mounted = mountPatternPill(doc, {
    read: async command => { reads.push(command); if (release) await new Promise(resolve => { release = resolve; }); return structuredClone(reply); },
    getListen: async () => async (name, fn) => { assert.equal(name, 'lcu-event'); listener = fn; },
  });
  await mounted.ready;
  assert.deepEqual(reads, ['get_pattern_nudge']);
  assert.equal(pill.hidden, false);

  const emit = (type, payload = {}) => listener({ payload: { type, payload } });
  emit('champSelectStarted');
  assert.equal(pill.hidden, true, 'hidden before any read');

  // The sidecar holds the nudge while the lobby is open; a dodge asks again.
  reply = { ...ready, notify: false, quietReason: 'live' };
  emit('champSelectUpdated');
  await settle();
  assert.equal(reads.length, 1, 'other live events change nothing');
  reply = ready;
  emit('champSelectCancelled');
  await settle();
  assert.equal(pill.hidden, false);

  // A read already in flight when a game starts cannot bring the pill back.
  release = () => {};
  const inFlight = mounted.refresh();
  emit('gameInProgress');
  release();
  await inFlight;
  assert.equal(pill.hidden, true);
  release = null;

  emit('gameEnded', { saved: true });
  await settle();
  assert.equal(pill.hidden, false);

  emit('liveState', { isGameInProgress: true });
  assert.equal(pill.hidden, true);
  emit('lcuConnection', { connected: false });
  await settle();
  assert.equal(pill.hidden, false, 'closing the client ends the lobby; the sidecar decides');
});

test('the Home pill only reads and paints', async () => {
  const code = await readFile(new URL('../ui/home-patterns.mjs', import.meta.url), 'utf8');
  const source = code.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
  assert.doesNotMatch(source, /innerHTML|Notification|showNotification|location\./);
  assert.doesNotMatch(code, /[–—]/);
});
