import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  FOLLOWTHROUGH_KEY, FOLLOWTHROUGH_LIMIT, TAKEAWAY_EMPTY, createTakeawayCard, describeTakeaway, findLastTakeaway,
  followThroughFor, mountJournalTakeaway, readFollowThrough, relativeDay, saveFollowThrough, takeawayText,
} from '../ui/journal-takeaway.mjs';

// Journal's Home pull-quote: the latest takeaway, found without a sidecar change, and a
// "Did you do it?" check-in kept on this device only.

function memoryStorage(initial = {}) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    getItem: key => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => { data.set(key, String(value)); },
  };
}
const brokenStorage = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); } };

test('saved answers survive bad JSON, foreign values and storage that throws', () => {
  assert.deepEqual(readFollowThrough(memoryStorage()), {});
  assert.deepEqual(readFollowThrough(memoryStorage({ [FOLLOWTHROUGH_KEY]: '{not json' })), {});
  assert.deepEqual(readFollowThrough(memoryStorage({ [FOLLOWTHROUGH_KEY]: '[1,2]' })), {});
  assert.deepEqual(readFollowThrough(brokenStorage), {});
  assert.deepEqual(readFollowThrough(null), {});
  const mixed = JSON.stringify({ 41: 'yes', 42: 'maybe', abc: 'yes', '-3': 'partly', 43: 'notyet' });
  assert.deepEqual(readFollowThrough(memoryStorage({ [FOLLOWTHROUGH_KEY]: mixed })), { 41: 'yes', 43: 'notyet' });
});

test('an answer saves, reads back, changes and clears under the one key', () => {
  const storage = memoryStorage();
  assert.deepEqual(saveFollowThrough(5581701721, 'yes', storage), { ok: true, answers: { 5581701721: 'yes' } });
  assert.equal(followThroughFor(5581701721, storage), 'yes');
  saveFollowThrough('5581701721', 'partly', storage);
  assert.equal(followThroughFor(5581701721, storage), 'partly');
  saveFollowThrough(5581701721, null, storage);
  assert.equal(followThroughFor(5581701721, storage), null);
  assert.deepEqual(JSON.parse(storage.data.get(FOLLOWTHROUGH_KEY)), {});
  assert.equal(saveFollowThrough('nope', 'yes', storage).ok, false, 'a bad game id is never stored');
  assert.deepEqual([...storage.data.keys()], [FOLLOWTHROUGH_KEY], 'nothing else is written');
});

test(`only the newest ${FOLLOWTHROUGH_LIMIT} answers are kept, and the one just given always stays`, () => {
  const initial = {};
  for (let id = 1000; id < 1000 + FOLLOWTHROUGH_LIMIT; id++) initial[id] = 'yes';
  const storage = memoryStorage({ [FOLLOWTHROUGH_KEY]: JSON.stringify(initial) });
  const newer = saveFollowThrough(5000, 'partly', storage);
  assert.equal(Object.keys(newer.answers).length, FOLLOWTHROUGH_LIMIT);
  assert.equal(newer.answers[1000], undefined, 'the oldest game drops out');
  assert.equal(newer.answers[5000], 'partly');
  const older = saveFollowThrough(7, 'notyet', storage);
  assert.equal(Object.keys(older.answers).length, FOLLOWTHROUGH_LIMIT);
  assert.equal(older.answers[7], 'notyet', 'an answer to an old takeaway is not dropped the moment it is given');
});

test('a refused write reports failure without throwing', () => {
  assert.deepEqual(saveFollowThrough(42, 'yes', brokenStorage), { ok: false, answers: { 42: 'yes' } });
});

test('the takeaway is the review notes, else the older focus-next field', () => {
  assert.equal(takeawayText({ reviewNotes: '  Respect Caitlyn range  ', focusNext: 'Ward' }), 'Respect Caitlyn range');
  assert.equal(takeawayText({ reviewNotes: '', focusNext: ' Ward river first ' }), 'Ward river first');
  assert.equal(takeawayText({ reviewNotes: null, focusNext: '   ' }), '');
  assert.equal(takeawayText(undefined), '');
});

const history = { items: [
  { gameId: 9, championName: 'Ashe', enemyChampion: 'Lucian', win: false, hasReview: false, datePlayed: 'Jun 14, 12:00' },
  { gameId: 8, championName: 'Sivir', enemyChampion: 'Senna', win: true, hasReview: true, isRemake: true, datePlayed: 'Jun 14, 11:00' },
  { gameId: 7, championName: 'Jinx', enemyChampion: 'Caitlyn', win: false, hasReview: true, datePlayed: 'Jun 14, 10:00' },
  { gameId: 6, championName: "Kai'Sa", enemyChampion: 'Ezreal', win: true, hasReview: true, datePlayed: 'Jun 13, 22:14' },
  { gameId: 5, championName: 'Varus', enemyChampion: 'Draven', win: true, hasReview: true, datePlayed: 'Jun 13, 21:00' },
  { gameId: 4, championName: 'Xayah', enemyChampion: 'Jhin', win: true, hasReview: true, datePlayed: 'Jun 13, 20:00' },
] };
function reader(forms) {
  const calls = [];
  const read = async (command, sample, args) => {
    calls.push([command, sample, args]);
    if (command === 'get_games') return history;
    if (forms[args.gameId] instanceof Error) throw forms[args.gameId];
    return { subject: { form: forms[args.gameId] || {} } };
  };
  return { calls, read };
}

test('the latest takeaway walks reviewed, non-remake games newest first, at most three reviews', async () => {
  const { calls, read } = reader({ 7: { reviewNotes: '' }, 6: new Error('timeout'), 5: { reviewNotes: 'Track the enemy jungler' } });
  const warn = console.warn; console.warn = () => {};
  let found;
  try { found = await findLastTakeaway({ readSnapshot: read }); } finally { console.warn = warn; }
  assert.deepEqual(found, { gameId: 5, championName: 'Varus', enemyChampion: 'Draven', win: true, datePlayed: 'Jun 13, 21:00',
    timestamp: null, text: 'Track the enemy jungler' });
  assert.deepEqual(calls.map(call => call[0] === 'get_games' ? call : [call[0], call[2].gameId]), [
    ['get_games', 'sample-games.json', { view: 'history', page: 0 }], ['get_review', 7], ['get_review', 6], ['get_review', 5]]);

  const none = reader({});
  assert.equal(await findLastTakeaway({ readSnapshot: none.read }), null);
  assert.equal(none.calls.filter(call => call[0] === 'get_review').length, 3, 'never more than three reviews');
});

test('a history page that reports an error rejects, so the figure hides and the next Home render retries', async () => {
  let locked = true;
  const calls = [];
  const read = async (command, sample, args) => {
    calls.push(command);
    if (command === 'get_games') return locked ? { items: [], errorText: 'The database is busy.' } : history;
    return { subject: { form: { reviewNotes: 'Respect range' } } };
  };
  await assert.rejects(findLastTakeaway({ readSnapshot: read }), /The database is busy\./);
  assert.deepEqual(calls, ['get_games'], 'no review is opened for an unreadable library');
  assert.equal(await findLastTakeaway({ readSnapshot: async () => ({ items: [], errorText: '   ' }) }), null,
    'a blank error is no error');

  const ids = new Map(['home-last', 'home-last-cap', 'home-last-link', 'home-last-when', 'home-last-quote', 'home-check', 'home-last-empty']
    .map(id => [id, node()]));
  ids.get('home-last').querySelectorAll = () => [];
  const doc = { getElementById: id => ids.get(id) || null, documentElement: { getAttribute: () => 'journal' } };
  const mounted = mountJournalTakeaway(doc, { readSnapshot: read, storage: memoryStorage(), win: { addEventListener() {} } });
  const warn = console.warn; console.warn = () => {};
  try {
    mounted.update({ today: '2026-06-14' });
    await new Promise(resolve => setTimeout(resolve, 0));
  } finally { console.warn = warn; }
  assert.equal(ids.get('home-last').hidden, true, 'the figure hides instead of claiming there is no takeaway');
  assert.notEqual(ids.get('home-last-empty').textContent, TAKEAWAY_EMPTY);
  locked = false;
  mounted.update({ today: '2026-06-14' });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(ids.get('home-last').hidden, false);
  assert.equal(ids.get('home-last-quote').textContent, 'Respect range', 'the next render retries and finds it');
});

test('a dashboard that carries lastTakeaway is used as-is, even when it is null', async () => {
  const { calls, read } = reader({});
  const given = { gameId: 77, championName: 'Jinx', enemyChampion: 'Caitlyn', win: false, timestamp: 1, text: ' Respect range ' };
  assert.equal((await findLastTakeaway({ dashboard: { lastTakeaway: given }, readSnapshot: read })).text, 'Respect range');
  assert.equal(await findLastTakeaway({ dashboard: { lastTakeaway: null }, readSnapshot: read }), null);
  assert.deepEqual(calls, [], 'no history or review reads');
});

test('the caption says when the takeaway was written', () => {
  assert.equal(relativeDay('Jun 14, 12:32', '2026-06-14'), 'today');
  assert.equal(relativeDay('Jun 13, 22:14', '2026-06-14'), 'yesterday');
  assert.equal(relativeDay('Jun 01, 09:00', '2026-06-02'), 'yesterday', 'the zero-padded day still matches');
  assert.equal(relativeDay('May 31, 23:00', '2026-06-01'), 'yesterday', 'across a month');
  assert.equal(relativeDay('Jun 11, 22:14', '2026-06-14'), '');
  assert.equal(relativeDay('juin 14, 12:32', '2026-06-14'), '', 'another language reads as itself');
  const row = { gameId: 7, championName: 'Jinx', enemyChampion: 'Caitlyn', text: 'Respect range' };
  assert.deepEqual(describeTakeaway({ ...row, datePlayed: 'Jun 14, 10:00' }, { today: '2026-06-14' }),
    { matchup: 'Jinx vs Caitlyn', when: ' today', href: 'review.html?gameId=7', text: 'Respect range' });
  assert.equal(describeTakeaway({ ...row, datePlayed: 'Jun 10, 10:00' }, { today: '2026-06-14' }).when, ' on Jun 10');
  assert.equal(describeTakeaway({ ...row, datePlayed: '' }, { today: '2026-06-14' }).when, '');
  const noon = new Date(2026, 5, 13, 12).getTime();
  assert.equal(describeTakeaway({ ...row, timestamp: noon }, { today: new Date(2026, 5, 14, 9) }).when, ' yesterday');
  assert.equal(describeTakeaway({ gameId: 7, text: 'x' }).matchup, 'your last game');
  assert.equal(describeTakeaway({ gameId: 7, text: '   ' }), null);
});

function node(tag = 'DIV') {
  const listeners = new Map(), attributes = new Map(), names = new Set();
  return {
    tagName: tag, hidden: false, textContent: '', dataset: {},
    set innerHTML(_) { throw new Error('Takeaways must remain plain text'); },
    setAttribute(name, value) { attributes.set(name, String(value)); },
    getAttribute: name => attributes.get(name) ?? null,
    addEventListener(type, listener) { listeners.set(type, listener); },
    click() { listeners.get('click')?.({ type: 'click' }); },
    classList: { toggle(name, on) { if (on) names.add(name); else names.delete(name); }, contains: name => names.has(name) },
  };
}
function card({ load, storage = memoryStorage(), today = () => '2026-06-14' } = {}) {
  const answers = ['yes', 'partly', 'notyet'].map(answer => Object.assign(node('BUTTON'), { dataset: { answer } }));
  const elements = { figure: node('FIGURE'), caption: node(), link: node('A'), when: node('SPAN'), quote: node('BLOCKQUOTE'),
    check: node(), empty: node('P'), answers };
  elements.figure.hidden = true;
  return { elements, storage, card: createTakeawayCard({ elements, load, storage, today }) };
}

test('the figure quotes the takeaway, links its review and remembers the check-in', async () => {
  let loads = 0;
  const takeaway = { gameId: 7, championName: 'Jinx', enemyChampion: 'Caitlyn', datePlayed: 'Jun 14, 10:00', text: 'Respect range' };
  const f = card({ load: async () => { loads++; return takeaway; } });
  await f.card.ensure();
  await f.card.ensure();
  assert.equal(loads, 1, 'loaded once');
  const { figure, link, when, quote, check, empty, answers } = f.elements;
  assert.equal(figure.hidden, false);
  assert.equal(link.textContent, 'Jinx vs Caitlyn');
  assert.equal(link.getAttribute('href'), 'review.html?gameId=7');
  assert.equal(when.textContent, ' today');
  assert.equal(quote.textContent, 'Respect range');
  assert.equal(check.hidden, false);
  assert.equal(empty.hidden, true);
  assert.deepEqual(answers.map(b => b.getAttribute('aria-pressed')), ['false', 'false', 'false']);

  answers[1].click();
  assert.deepEqual(answers.map(b => b.getAttribute('aria-pressed')), ['false', 'true', 'false']);
  assert.equal(followThroughFor(7, f.storage), 'partly');
  answers[1].click();
  assert.equal(followThroughFor(7, f.storage), null, 'a second press clears the answer');
  answers[0].click();

  const again = card({ load: async () => takeaway, storage: f.storage });
  await again.card.ensure();
  assert.deepEqual(again.elements.answers.map(b => b.getAttribute('aria-pressed')), ['true', 'false', 'false'], 'the answer is still there next time');
});

test('no takeaway shows the empty line; a failed load hides the figure; storage that throws keeps the answer for now', async () => {
  const empty = card({ load: async () => null });
  await empty.card.ensure();
  assert.equal(empty.elements.figure.hidden, false);
  assert.equal(empty.elements.empty.hidden, false);
  assert.equal(empty.elements.empty.textContent, TAKEAWAY_EMPTY);
  assert.equal(empty.elements.quote.hidden, true);
  assert.equal(empty.elements.check.hidden, true);
  assert.doesNotMatch(TAKEAWAY_EMPTY, /[–—]/);

  const failed = card({ load: async () => { throw new Error('offline'); } });
  const warn = console.warn; console.warn = () => {};
  try { await failed.card.ensure(); } finally { console.warn = warn; }
  assert.equal(failed.elements.figure.hidden, true);

  const locked = card({ load: async () => ({ gameId: 3, text: 'Breathe' }), storage: brokenStorage });
  await locked.card.ensure();
  console.warn = () => {};
  try { locked.elements.answers[2].click(); } finally { console.warn = warn; }
  assert.equal(locked.elements.answers[2].getAttribute('aria-pressed'), 'true');
});

test('the figure loads only while Journal is the theme, or when the player switches to it', async () => {
  const ids = new Map(['home-last', 'home-last-cap', 'home-last-link', 'home-last-when', 'home-last-quote', 'home-check', 'home-last-empty']
    .map(id => [id, node()]));
  ids.get('home-last').querySelectorAll = () => [];
  let theme = 'warm';
  const listeners = new Map();
  const doc = { getElementById: id => ids.get(id) || null, documentElement: { getAttribute: () => theme } };
  const win = { addEventListener: (type, listener) => listeners.set(type, listener) };
  const { calls, read } = reader({ 7: { reviewNotes: 'Respect range' } });
  const mounted = mountJournalTakeaway(doc, { readSnapshot: read, storage: memoryStorage(), win });
  mounted.update({ today: '2026-06-14' });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(calls, [], 'Warm never reads reviews for a figure it does not show');

  listeners.get('revu:appearance')({ detail: { theme: 'journal' } });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(ids.get('home-last-quote').textContent, 'Respect range');
  assert.equal(calls.length, 2);

  theme = 'journal';
  mounted.update({ today: '2026-06-14' });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(calls.length, 2, 'a Home refresh does not walk the reviews again');
  mounted.update({ today: '2026-06-14', lastTakeaway: { gameId: 12, championName: 'Ashe', text: 'New words' } });
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(ids.get('home-last-quote').textContent, 'New words', 'a snapshot with lastTakeaway repaints from it');
});

test('Home marks the takeaway as the player\'s own words and never writes focus adherence', async () => {
  const html = await readFile(new URL('../ui/dashboard.html', import.meta.url), 'utf8');
  const source = await readFile(new URL('../ui/journal-takeaway.mjs', import.meta.url), 'utf8');
  assert.match(html, /<figure class="home-last" id="home-last" data-theme-only="journal" hidden>/);
  assert.match(html, /<blockquote class="home-last-quote own-words" id="home-last-quote">/);
  for (const answer of ['yes', 'partly', 'notyet']) assert.match(html, new RegExp(`data-answer="${answer}" aria-pressed="false"`));
  assert.doesNotMatch(source, /set_focus_adherence|focus-adherence/);
  assert.doesNotMatch(source.replace(/\/\/.*$/gm, ''), /[–—]/);
});
