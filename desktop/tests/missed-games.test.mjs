import test from 'node:test';
import assert from 'node:assert/strict';
import { createMissedGamesCard, describeMissedGame, mountMissedGames, shortDate } from '../ui/missed-games.mjs';

// Finished games Revu did not save reach the player on Home: the card follows the
// sidecar's 'missedReviews' event and saves or dismisses one through the renderer command.

function element(tagName = 'DIV') {
  const listeners = new Map(), attributes = new Map();
  return {
    tagName, children: [], dataset: {}, hidden: false, disabled: false, className: '', textContent: '', type: '',
    get firstChild() { return this.children[0] || null; },
    set innerHTML(_) { throw new Error('Missed games must remain plain text'); },
    appendChild(child) { this.children.push(child); child.parent = this; return child; },
    removeChild(child) { this.children.splice(this.children.indexOf(child), 1); return child; },
    setAttribute(name, value) { attributes.set(name, String(value)); },
    getAttribute: name => attributes.get(name) ?? null,
    addEventListener(type, listener) { listeners.set(type, listener); },
    click() { return listeners.get('click')?.({ type: 'click' }); },
  };
}

function find(node, predicate) {
  if (predicate(node)) return node;
  for (const child of node.children || []) {
    const hit = find(child, predicate);
    if (hit) return hit;
  }
  return null;
}

function fixture(listResponses = []) {
  const doc = { createElement: tag => element(tag.toUpperCase()) };
  const section = element('SECTION'), list = element(), heading = element('H2'), status = element('P');
  section.hidden = true;
  status.hidden = true;
  const calls = [];
  const responses = [...listResponses];
  const invoke = async (command, args) => {
    calls.push({ command, args: args === undefined ? undefined : JSON.parse(JSON.stringify(args)) });
    if (command === 'get_missed_games') return { games: responses.length ? responses.shift() : [] };
    return { ok: true };
  };
  const card = createMissedGamesCard({ section, list, heading, status, invoke, doc });
  return { card, section, list, heading, status, calls };
}

const juneFourteenth = Date.UTC(2026, 5, 14, 12) / 1000;
const ahri = { gameId: 7001, timestamp: juneFourteenth, championName: 'Ahri', enemyLaner: 'Zed', win: true,
  kills: 8, deaths: 3, assists: 6, gameDuration: 1667 };

test('a missedReviews event shows the waiting games on Home with Save and Dismiss', () => {
  const f = fixture();
  f.card.handleEvent({ type: 'missedReviews', payload: { isPostGameReconcile: false, games: [ahri] } });
  assert.equal(f.section.hidden, false);
  assert.equal(f.list.children.length, 1);
  assert.equal(f.heading.textContent, '1 finished game wasn’t saved');
  const row = f.list.children[0];
  assert.equal(row.dataset.gameId, '7001');
  assert.equal(find(row, n => n.className === 'vline').textContent, 'Ahri vs Zed');
  assert.equal(find(row, n => n.className === 'home-review-result').textContent, 'Victory');
  assert.equal(find(row, n => n.className === 'vsmall').textContent, 'Jun 14 · 27:47 · 8/3/6 KDA');
  assert.equal(find(row, n => n.tagName === 'BUTTON' && n.className === 'cta').textContent, 'Save game');
  assert.equal(find(row, n => n.className === 'home-review-skip').textContent, 'Dismiss');
  assert.deepEqual(f.calls, [], 'an event that carries the list needs no round trip');
});

test('an empty missedReviews list hides the card, and other events are ignored', () => {
  const f = fixture();
  f.card.handleEvent({ type: 'missedReviews', payload: { games: [ahri, { ...ahri, gameId: 7002 }] } });
  assert.equal(f.heading.textContent, '2 finished games weren’t saved');
  f.card.handleEvent({ type: 'gameEnded', payload: { saved: true, gameId: 9 } });
  f.card.handleEvent({ type: 'champSelectStarted', payload: {} });
  assert.equal(f.list.children.length, 2);
  f.card.handleEvent({ type: 'missedReviews', payload: { games: [] } });
  assert.equal(f.section.hidden, true);
  assert.equal(f.list.children.length, 0);
});

test('a missedReviews event without a list refetches it', async () => {
  const f = fixture([[ahri]]);
  f.card.handleEvent({ type: 'missedReviews', payload: {} });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(f.calls.map(call => call.command), ['get_missed_games']);
  assert.equal(f.list.children.length, 1);
});

test('Save and Dismiss resolve the game through the renderer command, then refresh', async () => {
  const f = fixture([[{ ...ahri, gameId: 7002 }], []]);
  f.card.handleEvent({ type: 'missedReviews', payload: { games: [ahri, { ...ahri, gameId: 7002 }] } });
  await find(f.list.children[0], n => n.className === 'cta').click();
  assert.deepEqual(f.calls.slice(0, 2), [
    { command: 'resolve_missed_game', args: { payload: { gameId: 7001, action: 'save' } } },
    { command: 'get_missed_games', args: undefined },
  ]);
  assert.deepEqual(f.card.games.map(game => game.gameId), [7002]);
  await find(f.list.children[0], n => n.className === 'home-review-skip').click();
  assert.deepEqual(f.calls[2], { command: 'resolve_missed_game', args: { payload: { gameId: 7002, action: 'dismiss' } } });
  assert.equal(f.section.hidden, true);
  assert.equal(f.status.hidden, true);
});

test('a failed save keeps the game and says so in sentence case', async () => {
  const f = fixture();
  const section = f.section;
  const card = createMissedGamesCard({
    section, list: f.list, heading: f.heading, status: f.status,
    doc: { createElement: tag => element(tag.toUpperCase()) },
    invoke: async (command) => {
      if (command === 'resolve_missed_game') throw new Error('sidecar 500');
      return { games: [ahri] };
    },
  });
  card.handleEvent({ type: 'missedReviews', payload: { games: [ahri] } });
  const originalError = console.error;
  console.error = () => {};
  try { await card.resolve(7001, 'save'); } finally { console.error = originalError; }
  assert.equal(f.status.hidden, false);
  assert.equal(f.status.textContent, 'Couldn’t save this game. Try again.');
  assert.equal(card.games.length, 1);
});

test('describeMissedGame and shortDate never print ISO dates', () => {
  assert.equal(shortDate(juneFourteenth), 'Jun 14');
  assert.equal(shortDate(0), '');
  assert.deepEqual(describeMissedGame({ championName: 'Jinx', win: false }), { title: 'Jinx', detail: '', result: 'Defeat' });
});

test('browser previews and pages without the card mount nothing', async () => {
  assert.equal(await mountMissedGames({ getElementById: () => null }), null);
  assert.equal(await mountMissedGames({ getElementById: () => element('SECTION') }), null);
});
