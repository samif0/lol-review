import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import {
  RECENT_LIMIT, LOAD_THROTTLE_MS, EVENT_DEBOUNCE_MS, accountView, avatarInitial, createShellSidebar, crumbFor, frameGameId,
  matchHref, matchLabel, ownerName, pageOf, pendingCount, recentRows, refreshesOn, shouldRefreshAfter,
} from '../ui/shell-recent.mjs';

const ui = new URL('../ui/', import.meta.url);
const sample = JSON.parse(await readFile(new URL('sample-games.json', ui), 'utf8'));
const shell = await readFile(new URL('index.html', ui), 'utf8');
const outer = await readFile(new URL('shell-outer.js', ui), 'utf8');
const LABELS = { dashboard: 'Home', games: 'Match library', objectives: 'Learning objectives', settings: 'Settings',
  review: 'Match review', vodplayer: 'VOD review', pregame: 'Before the match', manualentry: 'Add a game manually' };
const row = (over = {}) => ({ gameId: 42, championName: 'Jinx', enemyChampion: 'Caitlyn', win: true, hasReview: true,
  action: 'open_review', datePlayed: 'Jun 14, 11:58', ...over });

test('a frame path becomes the page key that prepaint writes as data-page', () => {
  assert.equal(pageOf('/review.html'), 'review');
  assert.equal(pageOf('revu-app://ui/VodPlayer.html?gameId=4#t'), 'vodplayer');
  assert.equal(pageOf('/review'), 'review', 'clean preview URLs read the same');
  assert.equal(pageOf('/'), 'index');
  assert.equal(pageOf(undefined), 'index');
});

test('recent rows open the recording when the row does, the review otherwise, and keep the history order', () => {
  const rows = recentRows(sample);
  assert.ok(rows.length > 0 && rows.length <= RECENT_LIMIT);
  assert.deepEqual(rows.map(r => r.gameId), sample.items.slice(0, rows.length).map(item => item.gameId));
  rows.forEach((r, i) => {
    const item = sample.items[i];
    assert.equal(r.href, `${item.action === 'watch_vod' ? 'vodplayer' : 'review'}.html?gameId=${item.gameId}`);
    assert.equal(r.result, item.win ? 'win' : 'loss');
    assert.equal(r.pending, item.hasReview !== true);
  });
  assert.equal(matchHref(row({ action: 'watch_vod' })), 'vodplayer.html?gameId=42');
  assert.equal(matchHref(row()), 'review.html?gameId=42');
  for (const bad of [0, -3, 'x', 1.5, null, Number.MAX_SAFE_INTEGER + 2]) assert.equal(matchHref(row({ gameId: bad })), '');
});

test('recent rows: six at most, labels, the review dot and the words a screen reader hears', () => {
  const many = { items: Array.from({ length: 30 }, (_, i) => row({ gameId: i + 1 })) };
  assert.equal(recentRows(many).length, 6);
  assert.equal(recentRows(many, 2).length, 2);
  const [pending, remake, unnamed, skipped] = recentRows({ items: [
    row({ hasReview: false, win: false }),
    row({ gameId: 43, hasReview: false, isRemake: true }),
    row({ gameId: 44, championName: '', enemyChampion: ' ' }),
    row({ gameId: 'nope' }),
    row({ gameId: 45, championName: 'Ashe', enemyChampion: '' }),
  ] });
  assert.equal(pending.pending, true);
  assert.equal(pending.ariaLabel, 'Jinx vs Caitlyn, loss, to review');
  assert.equal(pending.title, 'Jinx vs Caitlyn · Loss · Jun 14, 11:58 · To review');
  assert.equal(remake.result, 'remake');
  assert.equal(remake.pending, false, 'a remake is never waiting for review');
  assert.equal(remake.ariaLabel, 'Jinx vs Caitlyn, remake');
  assert.equal(unnamed.label, 'Match');
  assert.equal(skipped.label, 'Ashe', 'a row without a game id is skipped');
  assert.equal(matchLabel({ enemyChampion: 'Lux' }), 'vs Lux');
  for (const r of recentRows(sample)) assert.doesNotMatch(`${r.ariaLabel}${r.title}`, /[–—]/);
});

test('a failed or empty history keeps or clears the list deliberately', () => {
  assert.equal(recentRows({ errorText: 'Revu could not read your games.', items: [] }), null);
  assert.equal(recentRows(null), null);
  assert.deepEqual(recentRows({ items: [] }), []);
  assert.deepEqual(recentRows({}), []);
});

test('the Match library count is the review queue total, or the fixture rows in a preview', () => {
  assert.equal(pendingCount({ view: 'queue', totalCount: 7, items: [] }), 7);
  assert.equal(pendingCount({ view: 'queue', totalCount: 0, items: [] }), 0);
  assert.equal(pendingCount({ view: 'queue', totalCount: 'x' }), null);
  assert.equal(pendingCount({ view: 'queue', totalCount: 3, errorText: 'locked' }), null);
  assert.equal(pendingCount(null), null);
  assert.equal(pendingCount(sample), sample.items.filter(item => !item.hasReview && !item.isRemake).length);
  assert.equal(pendingCount({ view: 'history', items: [row({ hasReview: false }), row({ hasReview: false, isRemake: true }), row()] }), 1);
});

test('the preview queue fixture is the unreviewed part of the history fixture, counted like the sidecar', async () => {
  const queue = JSON.parse(await readFile(new URL('sample-games-queue.json', ui), 'utf8'));
  const waiting = sample.items.filter(item => !item.hasReview && !item.isRemake);
  assert.equal(queue.view, 'queue');
  assert.deepEqual(queue.items, waiting);
  assert.equal(queue.totalCount, waiting.length);
  assert.equal(queue.returnedCount, waiting.length);
  assert.equal(queue.hasMore, false);
  assert.equal(pendingCount(queue), waiting.length);
});

test('the footer shows the game name of the Riot ID and its initial', () => {
  assert.equal(ownerName('bye#NA1'), 'bye');
  assert.equal(ownerName('  Faker #KR1 '), 'Faker');
  assert.equal(ownerName(''), '');
  assert.equal(ownerName(undefined), '');
  assert.equal(ownerName('#NA1'), '');
  assert.equal(avatarInitial('bye'), 'B');
  assert.equal(avatarInitial('élan'), 'É');
  assert.equal(avatarInitial(''), '');
});

test('the account row shows a live Revu sign-in, never a saved Riot ID on its own', () => {
  const signedIn = { riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com' };
  assert.deepEqual(accountView({ ...signedIn, riotId: 'bye#NA1' }),
    { signedIn: true, name: 'bye', initial: 'B', state: 'Signed in as you@example.com.' });
  assert.deepEqual(accountView(signedIn), { signedIn: true, name: 'you@example.com', initial: 'Y', state: 'Signed in as you@example.com.' },
    'no Riot ID linked yet: the email');
  assert.equal(accountView({ ...signedIn, riotId: '   ' }).name, 'you@example.com', 'a blank Riot ID is no Riot ID');
  assert.equal(accountView({ ...signedIn, riotSessionEmail: '  you@example.com ' }).state, 'Signed in as you@example.com.');
  const signedOut = { signedIn: false, name: 'Sign in', initial: '', state: 'Not signed in.' };
  assert.deepEqual(accountView({ riotAuthState: 'loggedOut', riotId: 'bye#NA1' }), signedOut, 'signing out keeps the Riot ID, not the session');
  // An expired 30-day session keeps its email; get_config reports it loggedOut.
  assert.deepEqual(accountView({ riotAuthState: 'loggedOut', riotSessionEmail: 'you@example.com', riotId: 'bye#NA1' }), signedOut);
  assert.deepEqual(accountView({ riotAuthState: 'loggedIn', riotSessionEmail: '', riotId: 'bye#NA1' }), signedOut);
  assert.deepEqual(accountView({ riotAuthState: 'loggedIn', riotSessionEmail: '   ', riotId: 'bye#NA1' }), signedOut);
  assert.deepEqual(accountView(null), signedOut);
  assert.deepEqual(accountView(undefined), signedOut);
  for (const view of [accountView({ ...signedIn, riotId: 'bye#NA1' }), accountView(signedIn), accountView(null)]) {
    assert.doesNotMatch(`${view.name} ${view.state}`, /[–—]/);
  }
});

test('the title-bar crumb: nothing on Home, the library above a match, the rail label elsewhere', () => {
  assert.deepEqual(crumbFor('dashboard', 'dashboard', LABELS), { parent: '', current: '' });
  assert.deepEqual(crumbFor('index', 'dashboard', LABELS), { parent: '', current: '' });
  assert.deepEqual(crumbFor('review', 'games', LABELS), { parent: 'Match library', current: 'Match review' });
  assert.deepEqual(crumbFor('vodplayer', 'games', LABELS), { parent: 'Match library', current: 'VOD review' });
  assert.deepEqual(crumbFor('manualentry', 'games', LABELS), { parent: 'Match library', current: 'Add a game manually' });
  assert.deepEqual(crumbFor('games', 'games', LABELS), { parent: '', current: 'Match library' });
  assert.deepEqual(crumbFor('pregame', 'dashboard', LABELS), { parent: '', current: 'Before the match' });
  assert.deepEqual(crumbFor('objectivegames', 'objectives', LABELS), { parent: '', current: 'Learning objectives' });
});

test('only a match page names the game it shows', () => {
  assert.equal(frameGameId('review', '?gameId=42'), 42);
  assert.equal(frameGameId('vodplayer', '?gameId=42&t=12'), 42);
  assert.equal(frameGameId('games', '?gameId=42'), null);
  assert.equal(frameGameId('review', '?gameId=abc'), null);
  assert.equal(frameGameId('review', ''), null);
});

test('refresh rules: listing and match pages on leave, throttled; saved game ends and recoveries live', () => {
  assert.equal(shouldRefreshAfter('review', { now: 10_000, last: 0 }), true);
  assert.equal(shouldRefreshAfter('games', { now: LOAD_THROTTLE_MS - 1, last: 0 }), false, 'at most once per 5 s');
  assert.equal(shouldRefreshAfter('dashboard', { now: 10_000, last: 0, visible: false }), false);
  assert.equal(shouldRefreshAfter('settings', { now: 10_000, last: 0 }), false);
  assert.equal(refreshesOn({ type: 'gameEnded', payload: { saved: true, gameId: 4 } }), true);
  assert.equal(refreshesOn({ type: 'gameEnded', payload: { saved: false, pending: true } }), false);
  assert.equal(refreshesOn({ type: 'gameRecovered', payload: { saved: false } }), false);
  for (const type of ['gamesRecovered', 'remakeUpdated', 'vodLinked']) assert.equal(refreshesOn({ type, payload: {} }), true, type);
  for (const type of ['liveState', 'champSelectStarted', 'mapStateUpdated', undefined]) assert.equal(refreshesOn({ type }), false);
});

// A small DOM: enough of Element and Document for the sidebar's own markup.
class FakeElement {
  constructor(tag, doc) { this.tagName = tag.toUpperCase(); this.ownerDocument = doc; this.attributes = new Map();
    this.children = []; this.hidden = false; this.title = ''; this._text = ''; this.classList = new FakeClassList(this); }
  get className() { return this.attributes.get('class') || ''; }
  set className(value) { this.attributes.set('class', String(value)); }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set textContent(value) { this._text = String(value); this.children = []; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  hasAttribute(name) { return this.attributes.has(name); }
  removeAttribute(name) { this.attributes.delete(name); }
  append(...nodes) { for (const node of nodes) node.isFragment ? this.children.push(...node.children) : this.children.push(node); }
  replaceChildren(...nodes) { this.children = []; this._text = ''; this.append(...nodes); }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
  querySelectorAll(selector) {
    const cls = selector.replace(/^\./, '');
    return this.descendants().filter(el => el.className.split(' ').includes(cls));
  }
}
class FakeClassList {
  constructor(el) { this.el = el; }
  toggle(name, on) { const set = new Set(this.el.className.split(' ').filter(Boolean)); if (on) set.add(name); else set.delete(name); this.el.className = [...set].join(' '); }
  contains(name) { return this.el.className.split(' ').includes(name); }
}
function fakeShell() {
  const listeners = {};
  const doc = {
    visibilityState: 'visible',
    createElement: tag => new FakeElement(tag, doc),
    createDocumentFragment: () => Object.assign(new FakeElement('#fragment', doc), { isFragment: true }),
    addEventListener: (type, fn) => { (listeners[type] ||= []).push(fn); },
    fire: type => (listeners[type] || []).forEach(fn => fn()),
    els: {},
    getElementById: id => doc.els[id] || null,
  };
  for (const id of ['side-recent', 'side-recent-list', 'nav-games-count', 'nav-games-count-text', 'side-acct', 'side-acct-state', 'side-name', 'side-avatar', 'side-avatar-i']) {
    doc.els[id] = new FakeElement('span', doc);
  }
  doc.els['side-recent'].hidden = true;
  doc.els['nav-games-count'].hidden = true;
  return doc;
}
const SIGNED_IN = Object.freeze({ riotId: 'bye#NA1', riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com' });
function harness({ history = sample, queue = { view: 'queue', totalCount: 3, items: [] }, config = SIGNED_IN, listen = true } = {}) {
  const doc = fakeShell(), calls = [], timers = [], winListeners = {};
  let clock = 100_000, callback = null;
  const sources = { history, queue, config };
  const read = async (command, file, args) => {
    calls.push([command, file, args]);
    const value = await (command === 'get_config' ? sources.config : sources[args.view]);
    if (value instanceof Error) throw value;
    return structuredClone(value);
  };
  const sidebar = createShellSidebar({
    doc, read, now: () => clock,
    win: { addEventListener: (type, fn) => { winListeners[type] = fn; } },
    getListen: async () => (listen ? async (name, fn) => { assert.equal(name, 'lcu-event'); callback = fn; return () => {}; } : null),
    schedule: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
    cancel: id => { timers[id - 1].fn = null; },
  });
  const settle = () => new Promise(resolve => setImmediate(resolve));
  return {
    doc, calls, timers, sidebar, sources, settle, winListeners,
    tick: ms => { clock += ms; },
    emit: (type, payload = {}) => callback({ payload: { type, payload } }),
    flush: async () => { const due = timers.splice(0); due.forEach(t => t.fn?.()); await settle(); await settle(); },
    rows: () => doc.els['side-recent-list'].querySelectorAll('side-rec'),
  };
}

test('the sidebar renders recent matches, the review count and the account from its two reads', async () => {
  const h = harness();
  h.sidebar.start();
  await h.settle(); await h.settle();
  assert.deepEqual(h.calls.map(([command, file, args]) => [command, file, args?.view]),
    [['get_games', 'sample-games.json', 'history'], ['get_games', 'sample-games-queue.json', 'queue'], ['get_config', 'sample-settings.json', undefined]]);
  const links = h.rows();
  assert.equal(links.length, Math.min(6, sample.items.length));
  assert.equal(h.doc.els['side-recent'].hidden, false);
  const first = links[0];
  assert.equal(first.tagName, 'A');
  assert.ok(first.hasAttribute('data-shell-goto'), 'rows go through the shell navigation, never the top document');
  assert.equal(first.getAttribute('href'), matchHref(sample.items[0]));
  assert.equal(first.children[0].getAttribute('data-result'), sample.items[0].win ? 'win' : 'loss');
  assert.equal(first.children[1].textContent, matchLabel(sample.items[0]));
  const todo = links.filter(link => link.children.some(child => child.className === 'side-rec-todo'));
  assert.equal(todo.length, sample.items.slice(0, 6).filter(item => !item.hasReview).length);
  assert.equal(h.doc.els['nav-games-count'].textContent, '3');
  assert.equal(h.doc.els['nav-games-count'].hidden, false);
  assert.equal(h.doc.els['nav-games-count-text'].textContent, '3 matches to review');
  assert.equal(h.doc.els['side-name'].textContent, 'bye');
  assert.equal(h.doc.els['side-avatar-i'].textContent, 'B');
  assert.ok(h.doc.els['side-avatar'].classList.contains('has-initial'));
  assert.equal(h.doc.els['side-acct-state'].textContent, 'Signed in as you@example.com.');
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-in');
  assert.equal(h.doc.els['side-acct'].getAttribute('aria-label'), null, 'the row keeps its markup name, Account');
});

test('no account, no matches and a failed read degrade without blanking what is shown', async () => {
  const h = harness({ history: { items: [] }, queue: { view: 'queue', totalCount: 0, items: [] }, config: { riotId: '' } });
  h.sidebar.start();
  await h.settle(); await h.settle();
  assert.equal(h.doc.els['side-recent'].hidden, true, 'an empty history hides the section');
  assert.equal(h.doc.els['nav-games-count'].hidden, true, 'nothing to review shows no count');
  assert.equal(h.doc.els['side-name'].textContent, 'Sign in');
  assert.ok(!h.doc.els['side-avatar'].classList.contains('has-initial'));
  assert.equal(h.doc.els['side-acct-state'].textContent, 'Not signed in.');

  // Signing out clears the session but keeps the Riot ID: the row still says Sign in.
  const out = harness({ config: { riotId: 'bye#NA1', riotAuthState: 'loggedOut' } });
  await out.sidebar.refreshIdentity();
  assert.equal(out.doc.els['side-name'].textContent, 'Sign in');
  assert.equal(out.doc.els['side-avatar-i'].textContent, '');
  assert.ok(!out.doc.els['side-avatar'].classList.contains('has-initial'));
  assert.equal(out.doc.els['side-acct'].getAttribute('data-account'), 'signed-out');
  out.sources.config = new Error('sidecar restarting');
  await out.sidebar.refreshIdentity();
  assert.equal(out.doc.els['side-name'].textContent, 'Sign in', 'a failed read keeps the last state');
  assert.equal(out.doc.els['side-acct'].getAttribute('data-account'), 'signed-out');

  const kept = harness();
  await kept.sidebar.refresh();
  kept.sources.history = new Error('sidecar restarting');
  kept.sources.queue = { view: 'queue', totalCount: 1, errorText: 'locked', items: [] };
  await kept.sidebar.refresh();
  assert.equal(kept.rows().length, Math.min(6, sample.items.length), 'a failed read keeps the rows');
  assert.equal(kept.doc.els['nav-games-count'].textContent, '3', 'an errored queue keeps the count');
});

test('frame loads mark the shown match and refresh only after listing pages, at most once per 5 s', async () => {
  const h = harness();
  await h.sidebar.refresh();
  const reads = () => h.calls.filter(([command]) => command === 'get_games').length;
  assert.equal(reads(), 2);
  const id = sample.items[1].gameId;
  h.sidebar.frameLoaded('/games.html', '');
  h.tick(LOAD_THROTTLE_MS);
  h.sidebar.frameLoaded('/review.html', `?gameId=${id}`);
  await h.settle();
  assert.equal(reads(), 4, 'leaving the library refreshes');
  const current = h.rows().filter(link => link.getAttribute('aria-current') === 'true');
  assert.deepEqual(current.map(link => Number(link.getAttribute('data-game-id'))), [id]);

  h.tick(1000);
  h.sidebar.frameLoaded('/vodplayer.html', `?gameId=${id}`);
  await h.settle();
  assert.equal(reads(), 4, 'throttled');
  assert.deepEqual(h.timers.filter(t => t.fn).map(t => t.ms), [LOAD_THROTTLE_MS - 1000], 'one trailing refresh when the window ends');
  h.tick(LOAD_THROTTLE_MS);
  await h.flush();
  assert.equal(reads(), 6, 'the trailing refresh picks up a review saved inside the window');
  h.sidebar.frameLoaded('/settings.html', '');
  await h.settle();
  assert.equal(reads(), 6, 'leaving the VOD player changes no list');
  assert.equal(h.timers.filter(t => t.fn).length, 0);
  assert.equal(h.rows().filter(link => link.hasAttribute('aria-current')).length, 0);

  h.tick(LOAD_THROTTLE_MS);
  h.sidebar.frameLoaded('/dashboard.html', '');
  await h.settle();
  const configReads = h.calls.filter(([command]) => command === 'get_config').length;
  assert.equal(configReads, 2, 'opening and leaving Settings each reread the account name');
  assert.equal(reads(), 6);
  h.doc.visibilityState = 'hidden';
  h.tick(LOAD_THROTTLE_MS);
  h.sidebar.frameLoaded('/games.html', '');
  await h.settle();
  assert.equal(reads(), 6, 'a hidden window does not refresh on navigation');
  assert.equal(h.timers.filter(t => t.fn).length, 0, 'it only marks the list stale for when the window is shown');
});

test('live events refresh once per burst, wait while the window is hidden, and never navigate', async () => {
  const h = harness();
  h.sidebar.start();
  await h.settle(); await h.settle();
  const reads = () => h.calls.filter(([command]) => command === 'get_games').length;
  assert.equal(reads(), 2);
  h.emit('vodLinked', { gameId: 1 }); h.emit('vodLinked', { gameId: 2 }); h.emit('gamesRecovered', { gameIds: [1, 2] });
  h.emit('gameEnded', { saved: false });
  h.emit('liveState', {});
  assert.equal(h.timers.filter(t => t.fn).length, 1, 'debounced to one refresh');
  assert.equal(h.timers.at(-1).ms, EVENT_DEBOUNCE_MS);
  await h.flush();
  assert.equal(reads(), 4);

  h.doc.visibilityState = 'hidden';
  h.emit('remakeUpdated', { gameId: 3, isRemake: true });
  assert.equal(h.timers.filter(t => t.fn).length, 0, 'a hidden window only remembers');
  h.doc.visibilityState = 'visible';
  h.doc.fire('visibilitychange');
  await h.flush();
  assert.equal(reads(), 6);

  h.winListeners['revu:games-changed']();
  await h.flush();
  assert.equal(reads(), 8, 'a framed page can announce a changed list');
  for (const [command] of h.calls) assert.match(command, /^get_(games|config)$/, 'the sidebar only reads');
});

test('a page that signs in or out updates the account row in place, without rereading matches', async () => {
  const h = harness();
  h.sidebar.start();
  await h.settle(); await h.settle();
  const count = command => h.calls.filter(([name]) => name === command).length;
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-in');
  h.sources.config = { riotId: 'bye#NA1', riotAuthState: 'loggedOut', riotSessionEmail: '' };
  h.winListeners['revu:account-changed']();
  await h.settle(); await h.settle();
  assert.equal(count('get_config'), 2, 'one more config read');
  assert.equal(count('get_games'), 2, 'no match reads');
  assert.equal(h.timers.filter(t => t.fn).length, 0);
  assert.equal(h.doc.els['side-name'].textContent, 'Sign in');
  assert.ok(!h.doc.els['side-avatar'].classList.contains('has-initial'));
  assert.equal(h.doc.els['side-acct-state'].textContent, 'Not signed in.');
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-out');

  // Signing back in flips it back the same way, before any Riot ID is linked.
  h.sources.config = { riotId: '', riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com' };
  h.winListeners['revu:account-changed']();
  await h.settle(); await h.settle();
  assert.equal(h.doc.els['side-name'].textContent, 'you@example.com');
  assert.equal(h.doc.els['side-avatar-i'].textContent, 'Y');
  assert.ok(h.doc.els['side-avatar'].classList.contains('has-initial'));
  assert.equal(h.doc.els['side-acct-state'].textContent, 'Signed in as you@example.com.');
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-in');
  assert.equal(count('get_games'), 2, 'still no match reads');

  // Overlapping reads (sign in, then link a Riot ID): the latest one is what shows.
  let release;
  const slow = harness();
  slow.sources.config = new Promise(resolve => { release = resolve; });
  const first = slow.sidebar.refreshIdentity();
  slow.sources.config = SIGNED_IN;
  await slow.sidebar.refreshIdentity();
  release({ riotAuthState: 'loggedIn', riotSessionEmail: 'you@example.com' });
  await first;
  assert.equal(slow.doc.els['side-name'].textContent, 'bye', 'an older read never overwrites a newer one');
});

test('the row rereads the sign-in when the window is shown again and when the Account page opens', async () => {
  const h = harness();
  h.sidebar.start();
  await h.settle(); await h.settle();
  const configReads = () => h.calls.filter(([command]) => command === 'get_config').length;
  assert.equal(configReads(), 1);
  // The 30-day session runs out while Revu sits in the tray.
  h.doc.visibilityState = 'hidden';
  h.doc.fire('visibilitychange');
  assert.equal(configReads(), 1, 'nothing is read while hidden');
  h.sources.config = { riotId: 'bye#NA1', riotAuthState: 'loggedOut', riotSessionEmail: 'you@example.com' };
  h.doc.visibilityState = 'visible';
  h.doc.fire('visibilitychange');
  await h.settle(); await h.settle();
  assert.equal(configReads(), 2);
  assert.equal(h.doc.els['side-name'].textContent, 'Sign in');
  assert.equal(h.doc.els['side-acct-state'].textContent, 'Not signed in.');
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-out');
  assert.equal(h.calls.filter(([command]) => command === 'get_games').length, 2, 'a list that is not stale is not reread');

  // Opening the Account page rereads it too, so the row and the page agree.
  h.sources.config = SIGNED_IN;
  h.sidebar.frameLoaded('/dashboard.html', '');
  await h.settle();
  assert.equal(configReads(), 2, 'Home does not touch the account');
  h.sidebar.frameLoaded('/onboarding.html', '?signin=1');
  await h.settle(); await h.settle();
  assert.equal(configReads(), 3);
  assert.equal(h.doc.els['side-name'].textContent, 'bye');
  assert.equal(h.doc.els['side-acct'].getAttribute('data-account'), 'signed-in');
});

test('a refresh requested while one is running runs once more afterwards', async () => {
  const h = harness();
  const first = h.sidebar.refresh();
  h.sidebar.refresh(); h.sidebar.refresh();
  await first;
  await h.settle(); await h.settle();
  assert.equal(h.calls.filter(([command]) => command === 'get_games').length, 4);
});

test('no live bridge (browser preview) still fills the sidebar from the fixtures', async () => {
  const h = harness({ listen: false });
  h.sidebar.start();
  await h.settle(); await h.settle();
  assert.ok(h.rows().length > 0);
});

test('the shell markup carries the sidebar the module fills and routes every link through the frame', () => {
  for (const id of ['side-recent', 'side-recent-list', 'nav-games-count', 'nav-games-count-text', 'side-acct', 'side-acct-state', 'side-name', 'side-avatar', 'side-avatar-i', 'appbar-lcu', 'appbar-lcu-txt', 'appbar-ver', 'appbar-context', 'app-frame']) {
    assert.match(shell, new RegExp(`id="${id}"`), id);
  }
  assert.match(shell, /<a class="side-new" data-shell-goto data-start-session href="dashboard\.html\?intent=startblock">/);
  assert.match(shell, /<div class="side-top" data-window-drag>/);
  const brand = shell.slice(shell.indexOf('<div class="side-top"'), shell.indexOf('</div>', shell.indexOf('<div class="side-top"')));
  assert.doesNotMatch(brand, /<(a|button|input|select|textarea)\b/, 'the brand drag area holds nothing interactive');
  const body = shell.slice(shell.indexOf('<body>'));
  for (const [, tag] of body.matchAll(/<a\b([^>]*)>/g)) assert.match(tag, /class="nav-i\b|data-shell-goto/, `shell link ${tag} must stay in the frame`);
  // One account control: the footer row is the only way to Account, and it holds the
  // avatar, the name and the League Client line. The person glyph appears once.
  assert.equal(shell.match(/data-nav="onboarding"/g).length, 1);
  // Its name is "Account" (the tooltip) then the visible name, so "Sign in" or the game
  // name is part of it (WCAG 2.5.3, voice control).
  assert.match(shell, /<a class="nav-i side-acct" id="side-acct" data-nav="onboarding" href="onboarding\.html\?signin=1" aria-labelledby="side-acct-tip side-name" aria-describedby="side-acct-state appbar-lcu-txt"/);
  assert.doesNotMatch(shell.match(/<a class="nav-i side-acct"[^>]*>/)[0], /aria-label=/, 'aria-label would hide the visible name');
  const foot = shell.slice(shell.indexOf('<div class="side-foot">'), shell.indexOf('</aside>'));
  assert.equal(foot.match(/<a /g).length, 2, 'the account row and Settings');
  const row = foot.slice(foot.indexOf('id="side-acct"'), foot.indexOf('</a>', foot.indexOf('id="side-acct"')));
  for (const id of ['side-avatar', 'side-avatar-i', 'side-name', 'appbar-lcu', 'appbar-lcu-txt', 'side-acct-state']) assert.match(row, new RegExp(`id="${id}"`), `${id} sits inside the account row`);
  assert.match(row, /<span class="tip" id="side-acct-tip">Account<\/span>\s*$/);
  assert.equal(shell.split('<circle cx="12" cy="8.5" r="3.5"/>').length - 1, 1);
  assert.doesNotMatch(shell.match(/<span class="appbar-lcu"[^>]*>/)[0], /\stitle=/, 'no native tooltip stacked on the row label');
  assert.doesNotMatch(outer.slice(outer.indexOf('function setLcuIndicator'), outer.indexOf('\n}', outer.indexOf('function setLcuIndicator'))), /\.title\s*=/);
  // One delegated handler covers rail items and every [data-shell-goto] link.
  assert.match(outer, /closest\('\.nav-i,\[data-shell-goto\]'\)/);
  assert.match(outer, /setAttribute\('data-frame-page', page\)/);
  // The sidebar module never touches the frame or a page's active-work flag.
  return readFile(new URL('shell-recent.mjs', ui), 'utf8').then(code => {
    const source = code.replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
    assert.doesNotMatch(source, /__revuActiveWork|contentWindow|location\.(reload|href\s*=|assign|replace)|frameGoto/);
    assert.doesNotMatch(code, /[–—]/);
  });
});

// The shell's real "Start a session" routing, run against a fake frame.
function startSessionFor({ page = 'dashboard', start, activeWork = false } = {}) {
  const from = outer.indexOf("// The frame's page key");
  const to = outer.indexOf('// ── Sidebar clicks', from);
  assert.ok(from >= 0 && to > from, 'the start-session section must remain identifiable');
  const navigations = [], started = [];
  const contentWindow = { location: { pathname: `/${page}.html` }, __revuActiveWork: activeWork };
  if (start) contentWindow.__revuStartSession = () => { started.push(true); };
  const frame = { contentWindow, contentDocument: { documentElement: { getAttribute: name => (name === 'data-page' ? page : null) } } };
  const context = vm.createContext({ frame, pageOf, frameGoto: href => { navigations.push(href); return true; }, console: { warn() {} } });
  vm.runInContext(`${outer.slice(from, to)}\nglobalThis.go = startSession;`, context);
  context.go('dashboard.html?intent=startblock');
  return { navigations, started };
}

test('Start a session opens Home\'s composer in place and never reloads over an open editor', () => {
  assert.deepEqual(startSessionFor({ start: true }), { navigations: [], started: [true] });
  assert.deepEqual(startSessionFor({ start: true, activeWork: true }), { navigations: [], started: [true] });
  assert.deepEqual(startSessionFor({ activeWork: true }), { navigations: [], started: [] });
  assert.deepEqual(startSessionFor({}), { navigations: ['dashboard.html?intent=startblock'], started: [] });
  assert.deepEqual(startSessionFor({ page: 'review', start: true }), { navigations: ['dashboard.html?intent=startblock'], started: [] });
});
