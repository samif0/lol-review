import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, html, tick } from './games-page-fixture.mjs';
import { SKIPPED_TITLE, batches, createPicks, skipFailedToast, skipState, skipToast } from '../ui/games-skip.mjs';

// Skip and Unskip in the Match library: one row, a selection, Undo, and the counts that follow.

// A tiny sidecar. To review lists the unskipped games waiting for review; every other view
// lists them all (Removed lists `removed`). set_review_skipped answers the ids it changed.
function library(games, { fail = () => null, removed = [] } = {}) {
  const rows = games.map(g => ({ championName: `Champ${g.gameId}`, enemyChampion: 'Zed', win: g.gameId % 2 === 1,
    hasReview: false, isSkipped: false, ...g }));
  const totals = list => {
    const counted = list.filter(g => !g.isRemake);
    return { games: counted.length, wins: counted.filter(g => g.win).length, reviewed: counted.filter(g => g.hasReview).length,
      skipped: counted.filter(g => !g.hasReview && g.isSkipped).length };
  };
  let calls = 0;
  return {
    rows,
    reply: async ({ view }) => {
      if (view === 'removed') return { view, totalCount: removed.length, hasMore: false, totals: null, items: removed };
      const list = view === 'queue' ? rows.filter(g => !g.hasReview && !g.isSkipped && !g.isRemake) : rows;
      const items = list.map(g => ({ ...g, reviewStateText: g.hasReview ? 'Reviewed' : g.isSkipped ? 'Skipped' : 'Unreviewed' }));
      return { view, page: 0, pageSize: 30, totalCount: items.length, hasMore: false, totals: totals(list), items };
    },
    invoke: async (command, { payload }) => {
      assert.equal(command, 'set_review_skipped');
      const error = fail(payload, calls++);
      if (error) throw error;
      const changed = payload.gameIds.filter(id => rows.some(g => g.gameId === id));
      for (const g of rows) if (changed.includes(g.gameId)) g.isSkipped = payload.skipped;
      return { ok: true, skipped: payload.skipped, gameIds: changed };
    },
  };
}

const busy = () => new Error("Error invoking remote method 'revu:command': Error: The database is busy.");

function page(lib) {
  const f = fixture(lib.reply, { invoke: lib.invoke });
  const row = id => f.rows().find(r => r.dataset.gameId === String(id));
  return Object.assign(f, {
    row,
    ids: () => f.rows().map(r => r.dataset.gameId),
    skip: id => row(id).querySelector('.lib-skip'),
    pick: id => row(id).querySelector('.lib-pick'),
    tag: id => f.status(row(id))[0],
    picked: () => f.rows().filter(r => r.querySelector('.lib-pick').getAttribute('aria-checked') === 'true').map(r => r.dataset.gameId),
    count: () => f.$('queue-count').hidden ? null : f.$('queue-count').textContent,
    // The bar's and the toast's buttons carry their data-action in games.html.
    press: async (id, action) => { const el = f.$(id); el.dataset.action = action; return f.emit('click', el); },
  });
}

test('a game waiting for review offers Skip and a checkbox; a skipped one reads Skipped and offers Unskip', async () => {
  const f = page(library([]));
  const waiting = f.hooks.buildRow({ gameId: 1, championName: 'Jinx', enemyChampion: 'Caitlyn', hasReview: false });
  const skip = waiting.querySelector('.lib-skip'), pick = waiting.querySelector('.lib-pick');
  assert.equal(skip.hidden, false); assert.equal(skip.textContent, 'Skip');
  assert.equal(skip.dataset.action, 'skip_game'); assert.equal(skip.dataset.gameId, '1');
  assert.equal(skip.getAttribute('aria-label'), 'Skip review for Jinx vs Caitlyn');
  assert.equal(pick.hidden, false); assert.equal(pick.getAttribute('aria-checked'), 'false');
  assert.equal(pick.getAttribute('aria-label'), 'Select Jinx vs Caitlyn');
  assert.equal(waiting.classList.contains('is-pickable'), true);
  assert.equal(f.status(waiting)[0].textContent, 'To review');

  const skipped = f.hooks.buildRow({ gameId: 2, championName: 'Ahri', hasReview: false, isSkipped: true, reviewStateText: 'Skipped',
    hasVod: true });
  const tag = f.status(skipped)[0];
  assert.equal(tag.textContent, 'Skipped'); assert.equal(tag.dataset.status, 'skipped');
  assert.equal(tag.classList.contains('is-pending'), false, 'a skipped game is not waiting for review');
  assert.equal(tag.title, SKIPPED_TITLE);
  assert.equal(f.status(skipped).at(-1).textContent, 'Skipped, Recording', 'the state stays in the row text');
  assert.equal(skipped.querySelector('.lib-skip').textContent, 'Unskip');
  assert.equal(skipped.querySelector('.lib-skip').dataset.action, 'unskip_game');
  assert.equal(skipped.querySelector('.lib-skip').getAttribute('aria-label'), 'Unskip Ahri');
  assert.equal(skipped.querySelector('.lib-go').textContent, 'Review', 'a skipped game can still be reviewed');

  for (const game of [{ gameId: 3, hasReview: true }, { gameId: 4, hasReview: false, isRemake: true }, { gameId: 5 },
    { gameId: 6, hasReview: true, isSkipped: true }]) {
    const row = f.hooks.buildRow(game);
    assert.equal(row.querySelector('.lib-skip').hidden, true, JSON.stringify(game));
    assert.equal(row.querySelector('.lib-pick').hidden, true, JSON.stringify(game));
    assert.equal(row.classList.contains('is-pickable'), false, JSON.stringify(game));
  }
  assert.equal(f.status(f.hooks.buildRow({ gameId: 6, hasReview: true, isSkipped: true })).at(-1).textContent, 'Reviewed',
    'a saved review always reads as reviewed');
});

test('Skip on a To review row takes it out of the list, the count and the summary, and Undo puts it back', async () => {
  const f = page(library([{ gameId: 1 }, { gameId: 2 }, { gameId: 3, hasReview: true }]));
  await f.hooks.loadView('queue');
  assert.equal(f.count(), '2');
  assert.deepEqual(f.summary(), [['', 'Last 14 days'], ['2', 'games'], ['50%', 'won']]);

  await f.emit('click', f.skip(1));
  assert.deepEqual(f.writes, [{ command: 'set_review_skipped', args: { payload: { gameIds: [1], skipped: true } } }]);
  assert.deepEqual(f.ids(), ['2']);
  assert.equal(f.count(), '1');
  assert.deepEqual(f.summary(), [['', 'Last 14 days'], ['1', 'game'], ['0%', 'won']]);
  assert.equal(f.$('games-undo').hidden, false);
  assert.equal(f.$('games-undo-text').textContent, 'Champ1 vs Zed was skipped. It no longer waits for review.');
  assert.equal(f.$('games-undo-btn').hidden, false);
  assert.deepEqual(f.announced, ['revu:games-changed'], 'the sidebar count and dots refresh');
  assert.equal(f.focused?.dataset.gameId, '2', 'focus moves to the row that took its place');
  assert.equal(f.requests.length, 1, 'no refetch: To review changed in place');
  assert.equal(f.scope.location.href, 'games.html', 'skipping never opens the review');

  await f.press('games-undo-btn', 'undo');
  assert.deepEqual(f.writes.at(-1), { command: 'set_review_skipped', args: { payload: { gameIds: [1], skipped: false } } });
  assert.deepEqual(f.ids(), ['1', '2']);
  assert.equal(f.count(), '2');
  assert.deepEqual(f.summary(), [['', 'Last 14 days'], ['2', 'games'], ['50%', 'won']]);
  assert.equal(f.$('games-undo').hidden, true);
  assert.equal(f.announced.length, 2, 'so does Undo');
});

test('in All a skipped row stays and reads Skipped, the summary counts it, and Unskip reads the To review count again', async () => {
  const f = page(library([{ gameId: 1 }, { gameId: 2, hasReview: true }, { gameId: 3, isSkipped: true }]));
  await f.hooks.loadView('queue');
  assert.equal(f.count(), '1');
  await f.hooks.loadView('history');
  assert.deepEqual(f.summary(), [['3', 'games'], ['67%', 'won'], ['1', 'reviewed'], ['1', 'skipped']]);
  assert.equal(f.tag(3).textContent, 'Skipped');

  await f.emit('click', f.skip(1));
  assert.deepEqual(f.ids(), ['1', '2', '3'], 'the row stays in All');
  assert.equal(f.tag(1).textContent, 'Skipped');
  assert.equal(f.skip(1).textContent, 'Unskip');
  assert.equal(f.focused, f.skip(1), 'focus stays on the row, on its Unskip');
  assert.deepEqual(f.summary(), [['3', 'games'], ['67%', 'won'], ['1', 'reviewed'], ['2', 'skipped']]);
  assert.equal(f.count(), null, 'nothing waits in To review now');

  const reads = f.requests.length;
  await f.emit('click', f.skip(3));
  assert.deepEqual(f.writes.at(-1), { command: 'set_review_skipped', args: { payload: { gameIds: [3], skipped: false } } });
  assert.equal(f.tag(3).textContent, 'To review');
  assert.equal(f.$('games-undo-text').textContent, 'Champ3 vs Zed is waiting for review again.');
  assert.deepEqual(f.summary(), [['3', 'games'], ['67%', 'won'], ['1', 'reviewed'], ['1', 'skipped']]);
  await tick(); await tick();
  assert.deepEqual(f.requests.slice(reads).map(r => r.args), [{ view: 'queue', page: 0 }], 'the queue is read again, for its count');
  assert.equal(f.count(), '1', 'an unskipped game can wait in To review again');
  assert.deepEqual(f.ids(), ['1', '2', '3'], 'the rows on screen stay');

  await f.press('games-undo-btn', 'undo');
  assert.deepEqual(f.writes.at(-1), { command: 'set_review_skipped', args: { payload: { gameIds: [3], skipped: true } } });
  assert.equal(f.tag(3).textContent, 'Skipped');
  assert.equal(f.count(), null);
  assert.deepEqual(f.summary(), [['3', 'games'], ['67%', 'won'], ['1', 'reviewed'], ['2', 'skipped']]);
});

test('select several games with a checkbox, Shift, x, Select all and Clear; Skip takes them out together with one Undo', async () => {
  const f = page(library([1, 2, 3, 4, 5].map(gameId => ({ gameId }))));
  await f.hooks.loadView('queue');
  const bar = f.$('games-bulk');
  assert.equal(bar.hidden, true);

  await f.emit('click', f.pick(1));
  assert.deepEqual(f.picked(), ['1']);
  assert.equal(bar.hidden, false); assert.equal(f.$('games-bulk-n').textContent, '1 selected');
  assert.equal(f.$('games-list').classList.contains('is-picking'), true, 'every checkbox shows while selecting');
  assert.equal(f.row(1).classList.contains('is-picked'), true);
  assert.equal(f.scope.location.href, 'games.html', 'a checkbox never opens the review');
  await f.emit('click', f.pick(4), { shiftKey: true });
  assert.deepEqual(f.picked(), ['1', '2', '3', '4'], 'Shift selects the run from the last checkbox');
  await f.emit('click', f.pick(2));
  assert.deepEqual(f.picked(), ['1', '3', '4']);
  const x = await f.emit('keydown', f.row(5), { key: 'x' });
  assert.equal(x.defaultPrevented, true);
  assert.deepEqual(f.picked(), ['1', '3', '4', '5'], 'x selects the focused row');
  assert.equal(f.$('games-bulk-n').textContent, '4 selected');
  assert.equal(f.$('games-bulk-skip').hidden, false); assert.equal(f.$('games-bulk-unskip').hidden, true);
  assert.equal(f.$('games-bulk-all').hidden, false);
  await f.press('games-bulk-all', 'pick_all');
  assert.deepEqual(f.picked(), ['1', '2', '3', '4', '5']);
  assert.equal(f.$('games-bulk-all').hidden, true, 'nothing is left to add');
  await f.press('games-bulk-clear', 'pick_none');
  assert.deepEqual(f.picked(), []);
  assert.equal(bar.hidden, true); assert.equal(f.$('games-list').classList.contains('is-picking'), false);
  assert.deepEqual(f.writes, [], 'selecting writes nothing');

  await f.emit('click', f.pick(2));
  await f.emit('click', f.pick(4));
  await f.press('games-bulk-skip', 'skip_picked');
  assert.deepEqual(f.writes, [{ command: 'set_review_skipped', args: { payload: { gameIds: [2, 4], skipped: true } } }], 'one request');
  assert.deepEqual(f.ids(), ['1', '3', '5']);
  assert.equal(f.count(), '3');
  assert.equal(f.$('games-undo-text').textContent, '2 games were skipped. They no longer wait for review.');
  assert.equal(bar.hidden, true, 'the selection is done');
  assert.equal(f.focused?.dataset.gameId, '3', 'focus lands where the first skipped row was');

  await f.press('games-undo-btn', 'undo');
  assert.deepEqual(f.writes.at(-1), { command: 'set_review_skipped', args: { payload: { gameIds: [2, 4], skipped: false } } });
  assert.deepEqual(f.ids(), ['1', '2', '3', '4', '5'], 'each game returns to its place');
  assert.equal(f.count(), '5');
  assert.deepEqual(f.summary(), [['', 'Last 14 days'], ['5', 'games'], ['60%', 'won']]);
});

test('in All a selection can hold skipped games too: Skip and Unskip each act on their own games', async () => {
  const f = page(library([{ gameId: 1 }, { gameId: 2, isSkipped: true }, { gameId: 3, hasReview: true }]));
  await f.hooks.loadView('history');
  assert.equal(f.pick(3).hidden, true, 'a reviewed game has nothing to skip');
  const x = await f.emit('keydown', f.row(3), { key: 'x' });
  assert.notEqual(x.defaultPrevented, true);
  assert.deepEqual(f.picked(), []);

  await f.emit('click', f.pick(1));
  await f.emit('click', f.pick(2));
  assert.equal(f.$('games-bulk-skip').hidden, false); assert.equal(f.$('games-bulk-unskip').hidden, false);
  assert.equal(f.$('games-bulk-all').hidden, true, 'every game that can change is selected');
  await f.press('games-bulk-unskip', 'unskip_picked');
  assert.deepEqual(f.writes, [{ command: 'set_review_skipped', args: { payload: { gameIds: [2], skipped: false } } }]);
  assert.equal(f.tag(2).textContent, 'To review');
  assert.equal(f.tag(1).textContent, 'To review', 'Unskip leaves the game that was waiting alone');
  assert.equal(f.$('games-bulk').hidden, true);
  assert.equal(f.$('games-undo-text').textContent, 'Champ2 vs Zed is waiting for review again.');
});

test('a failed skip keeps the rows and the selection, says why, and offers nothing to undo', async () => {
  const f = page(library([{ gameId: 1 }, { gameId: 2 }], { fail: busy }));
  await f.hooks.loadView('queue');
  await f.emit('click', f.pick(1));
  await f.emit('click', f.pick(2));
  await f.press('games-bulk-skip', 'skip_picked');
  assert.deepEqual(f.ids(), ['1', '2']);
  assert.equal(f.$('games-undo-text').textContent, "Couldn't skip these games. The database is busy.");
  assert.equal(f.$('games-undo-btn').hidden, true, 'nothing changed, so nothing is offered to undo');
  assert.deepEqual(f.picked(), ['1', '2'], 'the selection stays for another try');
  assert.notEqual(f.$('errpanel').hidden, false, 'a failed write is not the "Couldn\'t load your games" panel');
  assert.equal(f.count(), '2');
  assert.deepEqual(f.announced, [], 'nothing changed for the sidebar');

  await f.emit('click', f.skip(1));
  assert.equal(f.$('games-undo-text').textContent, "Couldn't skip this game. The database is busy.");
  assert.deepEqual(f.ids(), ['1', '2']);
  assert.equal(f.skip(1).disabled, false);
});

test('a selection larger than one request is written in parts, and a part that fails is reported with Undo for the rest', async () => {
  const games = Array.from({ length: 501 }, (_, i) => ({ gameId: i + 1 }));
  const f = page(library(games, { fail: (_, call) => call === 1 ? busy() : null }));
  await f.hooks.loadView('queue');
  await f.press('games-bulk-all', 'pick_all');
  assert.equal(f.$('games-bulk-n').textContent, '501 selected');
  await f.press('games-bulk-skip', 'skip_picked');
  assert.deepEqual(f.writes.map(w => w.args.payload.gameIds.length), [500, 1]);
  assert.deepEqual(f.ids(), ['501'], 'the first part was skipped');
  assert.equal(f.$('games-undo-text').textContent, "Couldn't skip 1 of these games. The database is busy.");
  assert.equal(f.$('games-undo-btn').hidden, false, 'Undo is offered for the games that were skipped');
  assert.deepEqual(f.picked(), ['501'], 'the game that is left stays selected');
  await f.press('games-undo-btn', 'undo');
  assert.equal(f.writes.at(-1).args.payload.skipped, false);
  assert.equal(f.ids().length, 501);
});

test('the selection clears with Escape or another view, narrows to a search, and the Removed view offers none', async () => {
  const f = page(library([{ gameId: 1, championName: 'Ahri' }, { gameId: 2, championName: 'Zed' }],
    { removed: [{ gameId: 9, championName: 'Lux', hasReview: false }] }));
  await f.hooks.loadView('history');
  await f.emit('click', f.pick(1));
  await f.emit('click', f.pick(2));
  const field = f.$('lib-search');
  field.value = 'ahri'; await f.emit('input', field);
  assert.equal(f.$('games-bulk-n').textContent, '1 selected', 'a search narrows the selection to what it shows');
  field.value = ''; await f.emit('input', field);
  assert.deepEqual(f.picked(), ['1']);
  const escape = await f.emit('keydown', f.row(1), { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true);
  assert.deepEqual(f.picked(), []); assert.equal(f.$('games-bulk').hidden, true);

  await f.emit('click', f.pick(2));
  await f.emit('click', f.segments.find(s => s.dataset.view === 'today'));
  assert.deepEqual(f.picked(), [], 'another view starts without a selection');
  assert.equal(f.$('games-bulk').hidden, true);

  await f.hooks.loadView('removed');
  assert.equal(f.pick(9).hidden, true); assert.equal(f.skip(9).hidden, true, 'a removed game counts toward nothing to skip');
});

test('removing a skipped game from history takes it out of the skipped count, and Undo puts it back', async () => {
  const lib = library([{ gameId: 1, isSkipped: true }, { gameId: 2 }]);
  const f = fixture(lib.reply, { invoke: async (command, args) => command === 'set_game_hidden' ? { ok: true } : lib.invoke(command, args) });
  await f.hooks.loadView('history');
  assert.deepEqual(f.summary(), [['2', 'games'], ['50%', 'won'], ['0', 'reviewed'], ['1', 'skipped']]);
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.deepEqual(f.summary(), [['1', 'game'], ['0%', 'won'], ['0', 'reviewed']]);
  const undo = f.$('games-undo-btn'); undo.dataset.action = 'undo';
  await f.emit('click', undo);
  assert.deepEqual(f.writes.at(-1), { command: 'set_game_hidden', args: { payload: { gameId: 1, hidden: false } } });
  assert.deepEqual(f.summary(), [['2', 'games'], ['50%', 'won'], ['0', 'reviewed'], ['1', 'skipped']]);
});

test('skip helpers: the state a row offers, the selection model, parts and the toast copy', () => {
  assert.equal(skipState({ gameId: 1 }, false), 'pending');
  assert.equal(skipState({ gameId: 1, isSkipped: true }, false), 'skipped');
  assert.equal(skipState({ gameId: 1, isSkipped: true }, null), 'skipped');
  assert.equal(skipState({ gameId: 1, isSkipped: true }, true), null, 'a saved review always wins');
  assert.equal(skipState({ gameId: 1, isRemake: true }, false), null, 'a remake never waits for review');
  assert.equal(skipState({ gameId: 1 }, null), null, 'an unknown state offers nothing');
  assert.equal(skipState(null, false), null);

  const picks = createPicks();
  const order = ['1', '2', '3', '4', '5'];
  assert.equal(picks.toggle(2, { order }), true);
  assert.equal(picks.toggle(4, { extend: true, order }), true);
  assert.deepEqual(picks.list(), ['2', '3', '4']);
  assert.equal(picks.toggle(3, { extend: true, order }), false, 'Shift on a selected row clears the run');
  assert.deepEqual(picks.list(), ['2']);
  picks.toggle(5, { order }); picks.toggle(1, { extend: true, order });
  assert.deepEqual(picks.list().sort(), ['1', '2', '3', '4', '5']);
  picks.keepOnly(['1', '5', '9']);
  assert.deepEqual(picks.list(), ['5', '1']);
  assert.equal(picks.toggle(2, { extend: true, order }), true, 'without its anchor, Shift picks just the row');
  assert.deepEqual(picks.list(), ['5', '1', '2']);
  picks.addAll([7, 8]); assert.equal(picks.size, 5); assert.equal(picks.has(7), true);
  picks.clear(); assert.equal(picks.size, 0);

  assert.deepEqual(batches([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(batches([]), []);
  assert.equal(batches(Array.from({ length: 1001 }, (_, i) => i)).length, 3);

  assert.equal(skipToast(['Jinx vs Caitlyn'], true), 'Jinx vs Caitlyn was skipped. It no longer waits for review.');
  assert.equal(skipToast(['a', 'b'], true), '2 games were skipped. They no longer wait for review.');
  assert.equal(skipToast(['Jinx vs Caitlyn'], false), 'Jinx vs Caitlyn is waiting for review again.');
  assert.equal(skipToast(['a', 'b', 'c'], false), '3 games are waiting for review again.');
  assert.equal(skipFailedToast(1, 1, true, 'Busy.'), "Couldn't skip this game. Busy.");
  assert.equal(skipFailedToast(2, 2, false, 'Busy.'), "Couldn't unskip these games. Busy.");
  assert.equal(skipFailedToast(1, 3, true, ''), "Couldn't skip 1 of these games.");
  for (const text of [SKIPPED_TITLE, skipToast(['a'], true), skipToast(['a', 'b'], false), skipFailedToast(1, 2, true, 'x')]) {
    assert.doesNotMatch(text, /[–—]/, 'no em or en dashes');
  }
});

test('the library markup carries the checkbox, Skip and the selection bar', () => {
  const row = html.match(/<template id="tpl-gamerow">([\s\S]*?)<\/template>/)[1];
  assert.match(row, /<button class="ui-iconbtn lib-pick" type="button" data-action="pick_game" role="checkbox" aria-checked="false" hidden>/);
  assert.match(row, /<button class="ui-btn ui-btn-ghost lib-skip" type="button" data-action="skip_game" hidden>Skip<\/button>/);
  const body = html.slice(html.indexOf('<body'));
  for (const [id, action] of [['games-bulk-all', 'pick_all'], ['games-bulk-unskip', 'unskip_picked'], ['games-bulk-skip', 'skip_picked'],
    ['games-bulk-clear', 'pick_none'], ['games-undo-btn', 'undo']]) {
    assert.match(body, new RegExp(`id="${id}" type="button" data-action="${action}"`), id);
  }
  assert.match(body, /<div class="games-bulk" id="games-bulk" role="group" aria-label="Selected games" hidden>/);
  assert.match(body, /<span class="games-bulk-n" id="games-bulk-n" role="status" aria-live="polite"><\/span>/);
});
