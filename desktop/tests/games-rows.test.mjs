import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, html, source, plain, tick, NOW } from './games-page-fixture.mjs';

test('match rows show the result, matchup, time, KDA, one status tag and the recording mark', () => {
  const f = fixture();
  const row = f.hooks.buildRow({ gameId: 42, championName: 'Jinx', enemyChampion: 'Caitlyn', win: true, winLossText: 'W',
    gameMode: 'RANKED SOLO/DUO', datePlayed: 'SEP 12, 14:08', duration: '32:11', metaLine: 'Stale metadata',
    kills: 4, deaths: 3, assists: 7, kdaText: 'Old KDA', kdaRatio: 11 / 3, kdaRatioText: '(3.7)',
    hasReview: false, reviewStateText: 'Reviewed', hasVod: true, hasNotes: false, vodStateText: 'VOD linked - no notes',
    objectivePracticed: false, hasObjectiveEvidence: false, objectiveStateText: 'No objective tag', action: 'watch_vod', primaryAction: 'Watch VOD' });
  assert.equal(row.querySelector('.lib-name').textContent, 'Jinx vs Caitlyn');
  assert.equal(row.querySelector('.lib-champ').textContent, 'Ji');
  assert.equal(row.querySelector('.lib-meta').textContent, 'Ranked Solo/Duo · 14:08 · 32:11', 'the day heading carries the date');
  assert.equal(row.querySelector('.lib-kda-n').textContent, '4 / 3 / 7');
  assert.equal(row.querySelector('.lib-kda-r').textContent, '3.67 KDA');
  const wl = row.querySelector('.grow-wl');
  assert.equal(wl.textContent, 'W'); assert.equal(wl.title, 'Win'); assert.equal(wl.getAttribute('aria-label'), 'Win');
  assert.equal(row.dataset.result, 'win'); assert.equal(wl.dataset.result, 'win');
  const cell = f.status(row);
  assert.deepEqual(cell.map(el => el.template), ['tpl-tag', 'tpl-mark-video', 'tpl-sr']);
  assert.equal(cell[0].textContent, 'To review'); assert.equal(cell[0].classList.contains('is-pending'), true);
  assert.equal(cell[0].dataset.status, 'review');
  assert.match(cell[1].title, /No timestamped bookmarks/);
  assert.equal(cell[2].textContent, 'To review, Recording', 'every status stays in the row text');
  assert.equal(row.querySelector('.lib-go').textContent, 'Watch');
  assert.equal(row.querySelector('.lib-take').hidden, true, 'no takeaway without review notes');
});

test('recording notes and meaningful objective state remain distinct from written review, with conservative legacy fallbacks', () => {
  const f = fixture();
  const statuses = game => plain(f.hooks.gameStatuses(game).map(s => s.text));
  assert.deepEqual(statuses({ hasReview: true, hasVod: true, hasNotes: true, hasObjectiveEvidence: true }),
    ['Reviewed', 'Recording with notes', 'Evidence tagged']);
  assert.deepEqual(statuses({ hasReview: true, hasVod: true, hasNotes: false, objectivePracticed: true }),
    ['Reviewed', 'Recording', 'Objective practiced']);
  assert.deepEqual(statuses({ hasVod: false, hasNotes: true, vodStateText: 'VOD linked' }), ['No recording']);
  assert.match(f.hooks.gameStatuses({ hasVod: false })[0].title, /No linked recording was available/);
  assert.deepEqual(statuses({ reviewStateText: 'Unreviewed', vodStateText: 'VOD linked', objectiveStateText: 'No objective tag' }),
    ['To review', 'Recording']);
  assert.doesNotMatch(f.hooks.gameStatuses({ vodStateText: 'VOD linked' })[0].title, /No timestamped|has timestamped/,
    'legacy availability alone does not establish whether bookmarks exist');
  assert.deepEqual(statuses({}), [], 'missing flags must not be presented as known absence');

  const evidence = f.status(f.hooks.buildRow({ hasReview: true, hasVod: true, hasNotes: true, hasObjectiveEvidence: true }));
  assert.deepEqual(evidence.map(el => el.template), ['tpl-mark-video', 'tpl-mark-objective', 'tpl-sr'], 'reviewed games carry no tag');
  assert.equal(evidence[0].title, 'A linked recording was available at the last check and has timestamped bookmarks.');
  assert.equal(evidence[1].title, 'Evidence tagged');
  const none = f.status(f.hooks.buildRow({ hasVod: false }));
  assert.deepEqual(none.map(el => el.template), ['tpl-sr'], 'No recording draws no mark');
  assert.equal(none[0].textContent, 'No recording');

  const unknown = f.hooks.buildRow({ winLossText: 'Remake', kdaText: ' 1/0/9 ', kdaRatioText: '(10.0)', statsLine: 'CS 150 · 12k dmg' });
  assert.equal(unknown.querySelector('.grow-wl').textContent, '', 'an unknown result is never invented');
  assert.equal(unknown.querySelector('.grow-wl').title, '');
  assert.equal(unknown.dataset.result, 'unknown'); assert.equal(unknown.querySelector('.lib-kda-n').textContent, '1 / 0 / 9');
  assert.equal(unknown.querySelector('.lib-kda-r').textContent, '10 KDA');
  assert.equal(unknown.querySelector('.lib-meta').textContent, 'CS 150 · 12k dmg');
});

test('a remake shows no W or L, carries its tag, and the summary shows the server counts without it', async () => {
  const f = fixture(async () => ({ view: 'today', totals: { games: 2, wins: 1, reviewed: 1 }, items: [
    { gameId: 1, win: true, hasReview: true },
    { gameId: 2, win: false, winLossText: 'L', isRemake: true, hasReview: false },
    { gameId: 3, win: false, hasReview: false },
  ] }));
  const row = f.hooks.buildRow({ gameId: 2, win: false, winLossText: 'L', isRemake: true, hasReview: false });
  const wl = row.querySelector('.grow-wl');
  assert.equal(wl.textContent, '');
  assert.equal(wl.getAttribute('aria-label'), 'Remake');
  assert.match(wl.title, /Does not count toward your rules, stats or review queue/);
  assert.equal(row.dataset.result, 'remake'); assert.equal(wl.dataset.result, 'remake');
  const cell = f.status(row);
  assert.equal(cell[0].textContent, 'Remake'); assert.equal(cell[0].dataset.status, 'remake');
  assert.equal(cell[0].classList.contains('is-pending'), false, 'a remake is not waiting for review');
  assert.equal(cell[0].title, wl.title);
  assert.equal(cell.at(-1).textContent, 'Remake, To review');
  assert.doesNotMatch(wl.title + cell.map(t => t.title || '').join(''), /[—–]/, 'no em or en dashes in remake copy');

  await f.hooks.loadView('today');
  assert.deepEqual(f.summary(), [['', 'Today'], ['2', 'games'], ['50%', 'won'], ['1', 'reviewed']]);
  assert.equal(f.rows().length, 3, 'the remake is still listed and reviewable');

  await f.fire('revu:remake-updated', { gameId: 2, isRemake: false });
  assert.equal(f.requests.length, 2, 'a verdict change refetches the current view');
  assert.deepEqual(f.requests.at(-1).args, { view: 'today', page: 0 });
});

test('untrusted row strings remain plain text and cannot invent state or alter action destinations', async () => {
  const f = fixture(), text = '<img src=x onerror=alert(1)>';
  const row = f.hooks.buildRow({ gameId: '42&other=7', championName: text, enemyChampion: '<script>bad()</script>',
    metaLine: text, kdaText: text, kdaRatioText: text, reviewStateText: text, vodStateText: text,
    objectiveStateText: text, winLossText: '?', reviewNotes: text, action: 'open_review', primaryAction: text });
  assert.equal(row.querySelector('.lib-name').textContent, `${text} vs <script>bad()</script>`);
  assert.equal(row.querySelector('.lib-champ').textContent, 'Im');
  assert.equal(row.querySelector('.lib-meta').textContent, text); assert.equal(row.querySelector('.lib-kda-n').textContent, text);
  assert.equal(row.querySelector('.lib-kda-r').textContent, text);
  assert.equal(row.querySelector('.lib-take').textContent, text);
  assert.deepEqual(f.status(row).map(el => el.template), ['tpl-sr']);
  assert.equal(f.status(row)[0].textContent, '');
  await f.emit('click', row);
  assert.equal(f.scope.location.href, 'review.html?gameId=42%26other%3D7');
});

test('row cues match supplied actions and Enter/Space preserve whole-row destinations', async () => {
  const f = fixture();
  for (const [game, cue, destination, key] of [
    [{ gameId: 1, action: 'watch_vod', primaryAction: 'Review', hasVod: true }, 'Watch', 'vodplayer.html?gameId=1', 'Enter'],
    [{ gameId: 2, action: 'open_review', primaryAction: 'Watch VOD', hasReview: false, hasVod: true }, 'Review', 'review.html?gameId=2', ' '],
    [{ gameId: 3, action: 'open_review', primaryAction: 'Watch VOD', hasReview: true }, 'Open', 'review.html?gameId=3', 'Enter'],
    [{ gameId: 4, primaryAction: 'Review', winLossText: 'L' }, 'Review', 'review.html?gameId=4', ' '],
  ]) {
    const row = f.hooks.buildRow(game);
    assert.equal(row.querySelector('.lib-go').textContent, cue);
    const event = await f.emit('keydown', row.querySelector('.lib-name'), { key });
    assert.equal(event.defaultPrevented, true); assert.equal(f.scope.location.href, destination);
  }
  const loss = f.hooks.buildRow({ winLossText: 'L' });
  assert.equal(loss.querySelector('.grow-wl').title, 'Loss'); assert.equal(loss.querySelector('.grow-wl').textContent, 'L');
});

test('native snapshot views and History pagination retain filters, row order and shown counts', async () => {
  const replies = {
    'history:0': { view: 'history', items: [{ gameId: 1, championName: 'Jinx', hasReview: true }], totalCount: 2, hasMore: true },
    'history:1': { view: 'history', items: [{ gameId: 2, championName: 'Ahri', hasReview: false }], totalCount: 2, hasMore: false },
    'today:0': { view: 'today', items: [{ gameId: 3, championName: 'Ashe', hasReview: false }], hasMore: false },
  };
  const f = fixture(async ({ view, page }) => replies[`${view}:${page}`]);
  const today = f.segments.find(segment => segment.dataset.view === 'today');
  await f.hooks.loadView('history');
  assert.equal(f.$('lib-count').textContent, 'Showing 1 of 2'); assert.equal(f.$('lib-count').hidden, false);
  assert.equal(f.$('loadwrap').hidden, false);
  assert.equal(today.hidden, true, 'Today has no pill of its own');
  await f.hooks.loadMore();
  assert.deepEqual(plain(f.hooks.rows).map(game => game.gameId), [1, 2]);
  assert.equal(f.$('lib-count').textContent, 'Showing 2 of 2'); assert.equal(f.$('loadwrap').hidden, true);
  await f.emit('click', today);
  assert.equal(f.hooks.view, 'today'); assert.deepEqual(plain(f.hooks.rows).map(game => game.gameId), [3]);
  assert.equal(f.$('lib-count').hidden, true, 'only History pages through its games');
  assert.equal(today.getAttribute('aria-selected'), 'true'); assert.equal(today.hidden, false, 'a selected Today shows its pill');
  assert.equal(today.classList.contains('is-on'), true);
  assert.deepEqual(f.segments.filter(s => s.getAttribute('aria-selected') === 'true').map(s => s.dataset.view), ['today']);
  assert.deepEqual(f.requests.map(request => request.args), [{ view: 'history', page: 0 }, { view: 'history', page: 1 }, { view: 'today', page: 0 }]);
  assert.equal(replies['history:0'].items[0].hasReview, true, 'presentation does not mutate server-owned status fields');
});

test('the page opens on ?view= when given and on To review otherwise', async () => {
  const f = fixture();
  for (const [search, view] of [['', 'queue'], ['?view=vod', 'vod'], ['?appearance=warm-dark&view=removed', 'removed'],
    ['?view=ALL', 'history'], ['?view=history#x', 'history'], ['?view=today', 'today'], ['?view=bogus', 'queue'],
    ['?view=%E0%A4%A', 'queue'], ['?preview=1&review=vod', 'queue']]) {
    assert.equal(f.hooks.viewFromSearch(search), view, search);
  }
  for (const [search, view] of [[undefined, 'queue'], ['?view=history&appearance=journal-dark', 'history']]) {
    const page = fixture(async ({ view: asked }) => ({ view: asked, items: [] }));
    page.scope.location.search = search;
    await page.emit('DOMContentLoaded', null);
    await tick();
    assert.deepEqual(page.requests.map(request => request.args), [{ view, page: 0 }]);
    // A browser preview reads To review from its own queue snapshot, so it opens there too.
    assert.equal(page.requests[0].sample, view === 'queue' ? 'sample-games-queue.json' : 'sample-games.json');
    assert.equal(page.segments.find(s => s.dataset.view === view).getAttribute('aria-selected'), 'true');
  }
});

test('rows are grouped by day, and Today or Yesterday is said only when it can be proven', () => {
  const f = fixture();
  const now = NOW();
  const at = (id, datePlayed) => ({ gameId: id, datePlayed });
  const labels = (rows, view = 'history') => plain(f.hooks.dayGroups(rows, { now, view })
    .map(group => [group.label, group.rows.map(row => row.gameId)]));
  assert.deepEqual(labels([at(1, 'Oct 12, 14:00'), at(2, 'Oct 12, 10:00'), at(3, 'OCT 11, 22:00'), at(4, 'Oct 05, 20:00'),
    at(5, 'Oct 12, 09:00')]), [['Today', [1, 2]], ['Yesterday', [3]], ['Oct 05', [4]], ['Oct 12', [5]]],
  'a later group with the same date is from an earlier year');
  assert.deepEqual(labels([at(1, 'Oct 11, 22:00'), at(2, 'Oct 09, 10:00')]), [['Yesterday', [1]], ['Oct 09', [2]]]);
  assert.deepEqual(labels([at(1, 'Oct 09, 22:00'), at(2, 'Oct 11, 10:00'), at(3, 'Oct 12, 09:00')]),
    [['Oct 09', [1]], ['Oct 11', [2]], ['Oct 12', [3]]], 'older groups in a newest-first list are never Today or Yesterday');
  assert.deepEqual(labels([at(1, 'Oct 09, 22:00'), at(2, 'Oct 11, 10:00'), at(3, 'Oct 12, 09:00')], 'queue'),
    [['Oct 09', [1]], ['Yesterday', [2]], ['Today', [3]]], 'the 14-day queue cannot repeat a date');
  assert.deepEqual(labels([at(1, 'juin 12, 10:00'), { gameId: 2 }, at(3, 'Sep 30')]), [['juin 12', [1]], ['', [2]], ['Sep 30', [3]]],
    'another culture reads raw; a row without a date has no heading');

  const stamp = (id, ...date) => ({ gameId: id, datePlayed: 'ignored, 00:00', timestamp: new Date(...date).getTime() });
  assert.deepEqual(labels([stamp(1, 2026, 9, 3, 9), stamp(2, 2026, 9, 12, 1), stamp(3, 2026, 9, 11, 23), stamp(4, 2025, 11, 30, 20),
    { gameId: 5, timestamp: new Date(2026, 9, 12, 8).getTime() / 1000 }]),
  [['Oct 03', [1]], ['Today', [2]], ['Yesterday', [3]], ['Dec 30, 2025', [4]], ['Today', [5]]],
  'a timestamp proves the local day anywhere in the list, in milliseconds or seconds');
});

test('day headings sit between rows, and focus after a removal skips them', async () => {
  const items = [{ gameId: 1, datePlayed: 'Oct 12, 14:00', championName: 'Jinx' }, { gameId: 2, datePlayed: 'Oct 12, 10:00', championName: 'Ahri' },
    { gameId: 3, datePlayed: 'Oct 11, 20:00', championName: 'Ashe' }, { gameId: 4, datePlayed: 'Oct 05, 18:00', championName: 'Zed' }];
  const hidden = new Set();
  const f = fixture(async ({ view }) => ({ view, hasMore: false, items: items.filter(g => hidden.has(g.gameId) === (view === 'removed')) }),
    { invoke: async (_, { payload }) => { if (payload.hidden) hidden.add(payload.gameId); else hidden.delete(payload.gameId); return { ok: true }; } });
  await f.hooks.loadView('history');
  assert.deepEqual(f.$('games-list').children.map(el => el.template === 'tpl-day' ? el.textContent : el.dataset.gameId),
    ['Today', '1', '2', 'Yesterday', '3', 'Oct 05', '4']);
  assert.equal(f.rows()[1].querySelector('.lib-meta').textContent, '10:00');

  await f.emit('click', f.rows()[1].querySelector('.lib-remove'));
  assert.equal(f.focused?.dataset.gameId, '3', 'focus moves to the next row, not to the Yesterday heading');
  assert.deepEqual(f.headings(), ['Today', 'Yesterday', 'Oct 05']);
  await f.emit('click', f.rows()[2].querySelector('.lib-remove'));
  assert.equal(f.focused?.dataset.gameId, '3', 'removing the last row focuses the row above it');
  assert.deepEqual(f.headings(), ['Today', 'Yesterday']);

  await f.linked(3);
  assert.equal(f.rows()[1].dataset.action, 'watch_vod', 'a linked recording still finds its row among headings');

  await f.emit('click', f.segments.find(s => s.dataset.view === 'removed'));
  assert.deepEqual(f.headings(), ['Today', 'Oct 05']);
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.equal(f.focused?.dataset.gameId, '4');
  assert.equal(f.$('games-list').dataset.view, 'removed');
});

test('search filters the loaded rows by champion or enemy, says what it covered, and never calls the sidecar', async () => {
  const page = (n, items) => ({ view: 'history', page: n, pageSize: 4, totalCount: 40, hasMore: n === 0, items });
  const f = fixture(async ({ page: n }) => n === 0 ? page(0, [
    { gameId: 1, championName: "Kai'Sa", enemyChampion: 'Ezreal', datePlayed: 'Oct 12, 14:00' },
    { gameId: 2, championName: 'Jinx', enemyChampion: 'Caitlyn', datePlayed: 'Oct 12, 10:00' },
    { gameId: 3, championName: 'Ashe', enemyChampion: 'Lucian', datePlayed: 'Oct 11, 20:00' },
    { gameId: 4, championName: 'Caitlyn', enemyChampion: 'Jinx', datePlayed: 'Oct 05, 18:00' },
  ]) : page(1, [{ gameId: 5, championName: 'Ashe', enemyChampion: 'Draven', datePlayed: 'Oct 04, 18:00' }]));
  await f.hooks.loadView('history');
  const field = f.$('lib-search');
  const search = async value => { field.value = value; await f.emit('input', field); return f.rows().map(row => row.dataset.gameId); };
  assert.equal(f.$('lib-search-status').hidden, true);

  assert.deepEqual(await search('kaisa'), ['1'], "punctuation and case are ignored (kaisa finds Kai'Sa)");
  assert.equal(f.$('lib-search-status').hidden, false); assert.equal(f.$('lib-search-clear').hidden, false);
  assert.equal(f.$('lib-search-status').textContent, '1 of 4 loaded games match. Load more to search older games.');
  assert.deepEqual(f.headings(), ['Today'], 'headings without a match are left out');
  assert.deepEqual(await search('CAIT'), ['2', '4'], 'the enemy counts as well as your champion');
  assert.deepEqual(await search('ashe vs luc'), ['3'], 'every word must match; vs is ignored');
  assert.deepEqual(f.headings(), ['Yesterday'], 'labels come from the whole list, not the filtered one');

  assert.deepEqual(await search('zed'), []);
  assert.equal(f.$('lib-search-empty').hidden, false); assert.equal(f.$('games-empty').hidden, true);
  assert.equal(f.$('lib-search-status').textContent, 'No match in 4 loaded games. Load more to search older games.');
  const clear = { dataset: { action: 'clear_search' }, closest() { return this; } };
  await f.emit('click', clear);
  assert.equal(field.value, ''); assert.equal(f.rows().length, 4); assert.equal(f.focused, field);
  assert.equal(f.$('lib-search-empty').hidden, true); assert.equal(f.$('lib-search-status').hidden, true);
  assert.equal(f.$('lib-search-clear').hidden, true, 'the clear button shows only while the field holds text');

  assert.deepEqual(await search('ashe'), ['3']);
  await f.hooks.loadMore();
  assert.deepEqual(f.rows().map(row => row.dataset.gameId), ['3', '5'], 'newly loaded pages are searched too');
  assert.equal(f.$('lib-search-status').textContent, '2 of 5 loaded games match.');

  const enter = await f.emit('keydown', field, { key: 'Enter' });
  assert.notEqual(enter.defaultPrevented, true); assert.equal(f.scope.location.href, 'games.html');
  const escape = await f.emit('keydown', field, { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true); assert.equal(field.value, ''); assert.equal(f.rows().length, 5);
  assert.deepEqual(f.requests.map(request => request.args.page), [0, 1], 'only Load more reached the sidecar');

  assert.equal(f.hooks.matchesSearch({ championName: 'Nunu & Willump' }, f.hooks.searchTerms('nunu willump')), true);
  assert.equal(f.hooks.matchesSearch({ championName: 'Kaïsa' }, f.hooks.searchTerms('kaisa')), true);
  assert.deepEqual(plain(f.hooks.searchTerms('  vs  ')), []);
});

test('recovered games refresh the current view in place, keeping the typed search and its filter', async () => {
  let recovered = false;
  const f = fixture(async ({ view }) => ({ view, page: 0, pageSize: 30, hasMore: false, items: [
    ...(recovered ? [{ gameId: 9, championName: "Kai'Sa", enemyChampion: 'Varus', datePlayed: 'Oct 12, 16:00' }] : []),
    { gameId: 1, championName: "Kai'Sa", enemyChampion: 'Ezreal', datePlayed: 'Oct 12, 14:00' },
    { gameId: 2, championName: 'Jinx', enemyChampion: 'Caitlyn', datePlayed: 'Oct 12, 10:00' },
  ] }));
  await f.hooks.loadView('history');
  const field = f.$('lib-search');
  field.value = 'kaisa'; await f.emit('input', field);
  assert.deepEqual(f.rows().map(row => row.dataset.gameId), ['1']);
  recovered = true;
  await f.fire('revu:games-recovered', { gameIds: [9] });
  await tick();
  assert.equal(f.scope.location.href, 'games.html', 'the page is never reloaded');
  assert.deepEqual(f.requests.at(-1).args, { view: 'history', page: 0 }, 'the current view is fetched again');
  assert.equal(field.value, 'kaisa', 'the typed search survives');
  assert.deepEqual(f.rows().map(row => row.dataset.gameId), ['9', '1'], 'the filter applies to the refreshed rows');
  assert.equal(f.$('lib-search-status').textContent, '2 of 3 loaded games match.');
});

test('the To review count follows the queue through removals, Undo and restores, and hides when unknown', async () => {
  const replies = {
    queue: { view: 'queue', items: [{ gameId: 1, hasReview: false }, { gameId: 2, hasReview: false }], totals: { games: 2, wins: 0, reviewed: 0 } },
    history: { view: 'history', items: [{ gameId: 1, hasReview: false }, { gameId: 3, hasReview: true }], totals: { games: 2, wins: 0, reviewed: 1 } },
    removed: { view: 'removed', items: [{ gameId: 9, hasReview: false }], totals: null },
  };
  const f = fixture(async ({ view }) => replies[view], { invoke: async () => ({ ok: true }) });
  const count = () => f.$('queue-count').hidden ? null : f.$('queue-count').textContent;
  await f.hooks.loadView('history');
  assert.equal(count(), null, 'unknown until the queue loads');
  await f.hooks.loadView('queue');
  assert.equal(count(), '2');
  assert.deepEqual(f.summary(), [['', 'Last 14 days'], ['2', 'games'], ['0%', 'won']], 'the queue never repeats "0 reviewed"');
  await f.hooks.loadView('history');
  assert.equal(count(), '2', 'the count stays on other filters');
  assert.deepEqual(f.announced, [], 'reading the library changes nothing the sidebar shows');
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.equal(count(), '1');
  assert.deepEqual(f.announced, ['revu:games-changed'], 'a removal tells the shell sidebar to refresh');
  const undo = f.$('games-undo-btn'); undo.dataset.action = 'undo';
  await f.emit('click', undo);
  assert.equal(count(), '2');
  assert.equal(f.announced.length, 2, 'so does Undo');
  await f.emit('click', f.rows()[1].querySelector('.lib-remove'));
  assert.equal(count(), '2', 'a reviewed game was never waiting');
  await f.fire('revu:remake-updated', {});
  assert.equal(count(), null, 'a remake verdict can change the queue');
  await tick();
  await f.hooks.loadView('queue');
  assert.equal(count(), '2');
  await f.hooks.loadView('removed');
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.equal(count(), null, 'a restored unreviewed game may rejoin the queue');
});

test('Journal shows the review takeaway under a row only when the server sends one', () => {
  const f = fixture();
  const take = game => f.hooks.buildRow(game).querySelector('.lib-take');
  const quote = take({ gameId: 1, reviewNotes: '  Respect Caitlyn range until Nautilus can engage.  ' });
  assert.equal(quote.hidden, false);
  assert.equal(quote.textContent, 'Respect Caitlyn range until Nautilus can engage.');
  assert.equal(quote.title, quote.textContent);
  for (const reviewNotes of [undefined, '', '   ', 42, null]) {
    assert.equal(take({ gameId: 2, reviewNotes }).hidden, true, String(reviewNotes));
  }
  const row = html.match(/<template id="tpl-gamerow">([\s\S]*?)<\/template>/)[1];
  assert.match(row, /<p class="lib-take own-words" data-theme-only="journal" hidden><\/p>/, 'gated by theme attribute, filled by data');
});

test('the library markup keeps its hooks, the calm filters and plain copy', () => {
  const body = html.slice(html.indexOf('<body'));
  assert.match(body, /<input class="ui-field lib-search-input" id="lib-search" type="search" placeholder="Search by champion"/);
  assert.doesNotMatch(body, /notes"|and notes/i, 'search makes no claim about notes');
  const pills = [...body.match(/<div class="lib-pills" id="seg"[\s\S]*?<\/div>/)[0]
    .matchAll(/<button class="ui-pill lib-filter[^"]*"[^>]*data-view="(\w+)"[^>]*aria-selected="(\w+)"[^>]*>([^<]+)/g)]
    .map(([, view, selected, label]) => [view, selected, label.trim()]);
  assert.deepEqual(pills, [['history', 'false', 'All'], ['queue', 'true', 'To review'], ['vod', 'false', 'With recordings'],
    ['removed', 'false', 'Removed'], ['today', 'false', 'Today']]);
  assert.match(body, /data-view="today" role="tab" aria-selected="false" hidden>Today/);
  assert.match(body, /<h1 class="ui-title">Match library<\/h1>/);
  assert.match(body, /id="manual-entry" data-action="manual_entry"/);
  for (const id of ['games-list', 'games-empty', 'games-empty-h', 'games-empty-p', 'games-undo', 'games-undo-text', 'games-undo-btn',
    'loadwrap', 'loadmore', 'errpanel', 'err-line', 'err-detail', 'strip', 'queue-count', 'lib-count', 'lib-search-status', 'lib-search-empty',
    'lib-search-clear']) {
    assert.match(body, new RegExp(`id="${id}"`), id);
  }
  assert.match(body, /<div class="lib-row" data-action="open_review" tabindex="0" role="button">/);
  assert.match(body, /title="Remakes are listed but not counted\."/);
  const visible = body.replace(/<!--[\s\S]*?-->/g, '').replace(/<script[\s\S]*?<\/script>/g, '').replace(/<svg[\s\S]*?<\/svg>/g, '');
  assert.doesNotMatch(visible, /[–—]/, 'no em or en dashes in the page');
  const code = source.replace(/\/\*[\s\S]*?\*\//g, '').split('\n').map(line => line.replace(/(^|[^:'"`])\/\/.*$/, '$1')).join('\n');
  assert.doesNotMatch(code, /[–—]/, 'no em or en dashes in the page copy');
});

test('a linked recording patches only its mounted History row and keeps pagination, scroll and navigation intact', async () => {
  const f = fixture(async ({ page }) => ({ view: 'history', hasMore: page < 2, items: [
    { gameId: page + 1, championName: 'Jinx', hasVod: false, hasReview: false, action: 'open_review' },
  ] }));
  await f.hooks.loadView('history'); await f.hooks.loadMore();
  const rows = [...f.$('games-list').children];
  await f.linked(2);
  assert.equal(f.requests.length, 2, 'matching-row update does not refetch the library');
  assert.deepEqual(f.$('games-list').children, rows, 'focused row DOM stays mounted');
  assert.equal(rows[0].dataset.action, 'open_review');
  assert.equal(rows[1].dataset.action, 'watch_vod');
  assert.equal(rows[1].querySelector('.lib-go').textContent, 'Watch');
  assert.equal(f.status(rows[1]).find(el => el.dataset.status === 'recording').title, 'A linked recording was available at the last check.');
  assert.equal(f.scope.scrollY, 310);
  assert.equal(f.hooks.view, 'history');
  await f.emit('click', rows[1]);
  assert.equal(f.scope.location.href, 'vodplayer.html?gameId=2');
  await f.hooks.loadMore();
  assert.deepEqual(f.requests.at(-1).args, { view: 'history', page: 2 });
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [1, 2, 3]);
});

test('VOD filter refresh reveals a newly linked row, but a late result cannot overwrite a user-selected view', async () => {
  let linked = false, resolveRefresh;
  const f = fixture(async ({ view }) => {
    if (view !== 'vod') return { view, items: [{ gameId: 90, action: 'open_review' }] };
    if (!linked) return { view, items: [] };
    if (linked === 'pending') return new Promise(resolve => { resolveRefresh = resolve; });
    return { view, items: [{ gameId: 42, hasVod: true, action: 'watch_vod' }] };
  });
  await f.hooks.loadView('vod');
  linked = true; await f.linked(42);
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [42]);
  assert.equal(f.$('games-empty').hidden, true); assert.equal(f.scope.scrollY, 310);
  linked = 'pending';
  const pending = f.linked(43);
  await f.hooks.loadView('today');
  resolveRefresh({ view: 'vod', items: [{ gameId: 43, hasVod: true }] });
  await pending;
  assert.equal(f.hooks.view, 'today');
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [90]);
});

test('a link arriving during an older History response remains applied when that response renders', async () => {
  let resolve;
  const f = fixture(() => new Promise(done => { resolve = done; }));
  const pending = f.hooks.loadView('history');
  await f.linked(42); await f.linked(0); await f.linked('invalid');
  resolve({ view: 'history', items: [{ gameId: 42, hasVod: false, action: 'open_review' }] });
  await pending;
  assert.equal(f.rows()[0].dataset.action, 'watch_vod');
  assert.equal(f.hooks.rows[0].hasVod, true);
  assert.equal(f.requests.length, 1);
});

test('the summary shows the whole History from the server, not the loaded rows', async () => {
  const totals = { games: 181, wins: 94, reviewed: 62 };
  const f = fixture(async ({ page }) => ({ view: 'history', heading: 'All games', totalCount: 184, hasMore: page === 0, totals,
    items: [{ gameId: page + 1, win: true, hasReview: true }] }));
  await f.hooks.loadView('history');
  assert.deepEqual(f.summary(), [['181', 'games'], ['52%', 'won'], ['62', 'reviewed']]);
  assert.equal(f.$('strip').hidden, false);
  assert.equal(f.$('lib-count').textContent, 'Showing 1 of 184');
  assert.equal(f.$('statusline').hidden, true, 'the loading line goes once the library answers');
  await f.hooks.loadMore();
  assert.deepEqual(f.summary(), [['181', 'games'], ['52%', 'won'], ['62', 'reviewed']], 'Load more does not move the numbers');
  assert.equal(f.$('lib-count').textContent, 'Showing 2 of 184');
});

test('a library that could not be read shows an error, not the empty state or a zero summary', async () => {
  const f = fixture(async () => ({ view: 'history', items: [], totals: null,
    errorText: "Revu couldn't read your games from its local database. Try again, or restart Revu." }));
  await f.hooks.loadView('history');
  assert.equal(f.$('errpanel').hidden, false);
  assert.match(f.$('err-detail').textContent, /couldn't read your games/);
  assert.equal(f.$('games-empty').hidden, true);
  assert.equal(f.$('strip').hidden, true);
});

// A tiny sidecar: History pages over the visible games, honoring set_game_hidden.
function library(ids, size = 2) {
  const hidden = new Set();
  const visible = () => ids.filter(id => !hidden.has(id));
  const game = id => ({ gameId: id, championName: `Champ${id}`, enemyChampion: 'Zed', win: id % 2 === 1, hasReview: id === 1 });
  return {
    hidden,
    reply: async ({ view, page }) => {
      if (view === 'removed') {
        const removed = ids.filter(id => hidden.has(id)).reverse().map(game);
        return { view, heading: 'Removed games', totalCount: removed.length, hasMore: false, totals: null, items: removed };
      }
      const list = visible(), counted = list.map(game);
      return { view, page, pageSize: size, totalCount: list.length, hasMore: (page + 1) * size < list.length,
        totals: { games: counted.length, wins: counted.filter(g => g.win).length, reviewed: counted.filter(g => g.hasReview).length },
        items: list.slice(page * size, page * size + size).map(game) };
    },
    invoke: async (command, { payload }) => {
      assert.equal(command, 'set_game_hidden');
      if (payload.hidden) hidden.add(payload.gameId); else hidden.delete(payload.gameId);
      return { ok: true, ...payload };
    },
  };
}

test('Remove from history hides the game in place and Undo brings it back, without reloading the library', async () => {
  const lib = library([1, 2, 3], 30);
  const f = fixture(lib.reply, { invoke: lib.invoke });
  await f.hooks.loadView('history');
  const values = () => f.summary().map(([v]) => v);
  assert.deepEqual(values(), ['3', '67%', '1']);
  const second = f.rows()[1].querySelector('.lib-remove');
  assert.equal(second.getAttribute('aria-label'), 'Remove Champ2 vs Zed from history');

  await f.emit('click', second);
  assert.deepEqual(f.writes, [{ command: 'set_game_hidden', args: { payload: { gameId: 2, hidden: true } } }]);
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 3]);
  assert.deepEqual(values(), ['2', '100%', '1']);
  assert.equal(f.$('lib-count').textContent, 'Showing 2 of 2');
  assert.equal(f.$('games-undo').hidden, false);
  assert.equal(f.$('games-undo-btn').hidden, false);
  assert.equal(f.$('games-undo-text').textContent, 'Champ2 vs Zed was removed from history. You can restore it from Removed.');
  assert.equal(f.scope.location.href, 'games.html', 'removing never opens the review');
  assert.equal(f.requests.length, 1, 'no refetch: History pages and scroll stay');
  assert.equal(f.focused, f.rows()[1], 'keyboard focus moves to the row that took its place');
  assert.equal(f.focused.dataset.gameId, '3');

  const undo = f.$('games-undo-btn'); undo.dataset.action = 'undo';
  await f.emit('click', undo);
  assert.deepEqual(f.writes.at(-1), { command: 'set_game_hidden', args: { payload: { gameId: 2, hidden: false } } });
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 2, 3]);
  assert.deepEqual(values(), ['3', '67%', '1']);
  assert.equal(f.$('games-undo').hidden, true);
});

test('a failed removal keeps the row and explains the error in the toast; Enter on the button never opens the review', async () => {
  const f = fixture(library([1, 2], 30).reply,
    { invoke: async () => { throw new Error("Error invoking remote method 'revu:command': Error: The database is busy."); } });
  await f.hooks.loadView('history');
  const remove = f.rows()[0].querySelector('.lib-remove');
  const key = await f.emit('keydown', remove, { key: 'Enter' });
  assert.notEqual(key.defaultPrevented, true);
  assert.equal(f.scope.location.href, 'games.html');
  await f.emit('click', remove);
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 2]);
  assert.notEqual(f.$('errpanel').hidden, false, 'a failed write is not the "Couldn\'t load your games" panel');
  assert.equal(f.$('games-undo').hidden, false);
  assert.equal(f.$('games-undo-text').textContent, "Couldn't remove this game. The database is busy.");
  assert.equal(f.$('games-undo-btn').hidden, true, 'nothing was removed, so nothing is offered to undo');
  assert.equal(remove.disabled, false);
});

test('a failed Undo says so and keeps Undo offered to try again', async () => {
  const lib = library([1, 2], 30);
  let fail = false;
  const f = fixture(lib.reply, { invoke: async (command, args) => {
    if (fail) throw new Error('The database is busy.');
    return lib.invoke(command, args);
  } });
  await f.hooks.loadView('history');
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  fail = true;
  const undo = f.$('games-undo-btn'); undo.dataset.action = 'undo';
  await f.emit('click', undo);
  assert.equal(f.$('games-undo-text').textContent, "Couldn't bring Champ1 vs Zed back. The database is busy.");
  assert.equal(undo.hidden, false); assert.equal(undo.disabled, false);
  assert.notEqual(f.$('errpanel').hidden, false);
  fail = false;
  await f.emit('click', undo);
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 2]);
  assert.equal(f.$('games-undo').hidden, true);
});

test('the Removed view lists removed games with Restore, and Restore puts one back in History', async () => {
  const lib = library([1, 2, 3], 30);
  const f = fixture(lib.reply, { invoke: lib.invoke });
  await f.hooks.loadView('history');
  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  await f.emit('click', f.rows()[1].querySelector('.lib-remove'));
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [2]);

  const removed = f.segments.find(segment => segment.dataset.view === 'removed');
  await f.emit('click', removed);
  assert.equal(f.hooks.view, 'removed');
  assert.deepEqual(f.requests.at(-1).args, { view: 'removed', page: 0 });
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [3, 1]);
  assert.equal(f.$('strip').hidden, true, 'removed games count toward nothing');
  assert.equal(f.$('lib-count').hidden, true);
  const restore = f.rows()[0].querySelector('.lib-remove');
  assert.equal(restore.textContent, 'Restore');
  assert.equal(restore.dataset.action, 'restore_game');
  assert.equal(restore.getAttribute('aria-label'), 'Restore Champ3 vs Zed to history');
  assert.equal(restore.classList.contains('is-restore'), true); assert.equal(restore.classList.contains('ui-iconbtn'), false);

  await f.emit('click', restore);
  assert.deepEqual(f.writes.at(-1), { command: 'set_game_hidden', args: { payload: { gameId: 3, hidden: false } } });
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1]);
  assert.equal(f.$('games-undo-text').textContent, 'Champ3 vs Zed is back in your history.');
  assert.equal(f.$('games-undo-btn').hidden, true);
  assert.equal(f.scope.location.href, 'games.html', 'restoring never opens the review');
  assert.equal(f.focused, f.rows()[0]);

  await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), []);
  assert.equal(f.$('games-empty').hidden, false);
  assert.match(f.$('games-empty-p').textContent, /restore them/);
  assert.equal(f.focused, removed, 'with no row and nothing to undo, focus returns to the selected filter');

  await f.hooks.loadView('history');
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 2, 3]);
});

test('removing every loaded History row keeps Load more instead of saying there are no games', async () => {
  const lib = library([1, 2, 3, 4], 2);
  const f = fixture(lib.reply, { invoke: lib.invoke });
  await f.hooks.loadView('history');
  for (let i = 0; i < 2; i++) await f.emit('click', f.rows()[0].querySelector('.lib-remove'));
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), []);
  assert.equal(f.$('games-empty').hidden, true);
  assert.equal(f.$('loadwrap').hidden, false);
  assert.equal(f.focused, f.$('games-undo-btn'), 'with no row left, focus lands on Undo');
  await f.hooks.loadMore();
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [3, 4]);
});

test('Load more after a removal re-reads the shifted page so no game is skipped', async () => {
  const lib = library([1, 2, 3, 4, 5, 6], 2);
  const f = fixture(lib.reply, { invoke: lib.invoke });
  await f.hooks.loadView('history');
  await f.emit('click', f.rows()[1].querySelector('.lib-remove'));
  await f.hooks.loadMore();
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 3, 4, 5]);
  await f.hooks.loadMore();
  assert.deepEqual(plain(f.hooks.rows).map(g => g.gameId), [1, 3, 4, 5, 6]);
  assert.equal(f.$('loadwrap').hidden, true);
});
