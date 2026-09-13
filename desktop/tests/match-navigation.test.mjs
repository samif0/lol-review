import test from 'node:test';
import assert from 'node:assert/strict';
import { createMatchViewStore, createMatchNavigation, matchViewTarget, hasExplicitVodTarget } from '../ui/match-navigation.mjs';

test('view snapshots copy plain data and keep only the most recently used matches', () => {
  const store = createMatchViewStore({ maxGames: 2 });
  const state = { notes: ['Keep this exact draft  '] };
  store.save(1, 'review', state); state.notes[0] = 'Changed elsewhere';
  store.save(1, 'vod', { time: 45 });
  assert.deepEqual(store.peek(1, 'review'), { notes: ['Keep this exact draft  '] });
  const read = store.peek(1, 'review'); read.notes.push('mutated');
  assert.equal(store.peek(1, 'review').notes.length, 1);
  store.save(2, 'review', { text: 'Match two' });
  store.save(1, 'vod', { time: 70 }); // Touch match one before adding match three.
  store.save(3, 'review', { text: 'Match three' });
  assert.equal(store.peek(2, 'review'), null);
  store.clear(1, 'review');
  assert.deepEqual(store.peek(1, 'vod'), { time: 70 });
  assert.equal(store.save(0, 'review', {}), false);
  assert.equal(store.save(1, 'settings', {}), false);
});

test('match links keep explicit moments and add resume only for the same match', () => {
  const base = 'revu-app://ui/review.html?gameId=12';
  const link = matchViewTarget('vodplayer.html?gameId=12&t=0&clip=7', base, 12);
  assert.equal(link.searchParams.get('t'), '0');
  assert.equal(link.searchParams.get('clip'), '7');
  assert.equal(link.searchParams.get('resume'), '1');
  assert.equal(matchViewTarget('review.html?gameId=13', base, 12).searchParams.has('resume'), false);
  for (const target of ['https://example.com/review.html?gameId=12', '//other/review.html?gameId=12',
    'settings.html?gameId=12', '/nested/review.html?gameId=12', 'vodplayer.html?gameId=-1']) {
    assert.equal(matchViewTarget(target, base, 12), null);
  }
  assert.equal(hasExplicitVodTarget('?t=0'), true);
  assert.equal(hasExplicitVodTarget('?clip=4'), true);
  assert.equal(hasExplicitVodTarget('?t=&clip=0'), false);
  assert.equal(hasExplicitVodTarget('?t=wrong&clip=-2'), false);
});

function fixture({ view = 'review', search = '?gameId=12&resume=1', store = createMatchViewStore(), beforeLeave, canCapture } = {}) {
  const listeners = new Map(), elements = new Map();
  const links = ['review', 'vod'].map(value => ({ dataset: { matchView: value }, attributes: {},
    setAttribute(name, text) { this.attributes[name] = text; }, removeAttribute(name) { delete this.attributes[name]; } }));
  const status = { hidden: true, textContent: '' };
  const root = { hidden: true, querySelector: () => status, querySelectorAll: () => links,
    addEventListener(name, callback) { listeners.set(`root:${name}`, callback); } };
  const detail = { id: 'practice', tagName: 'DETAILS', open: false };
  elements.set('match-navigation', root); elements.set('practice', detail);
  const doc = { activeElement: null, getElementById: id => elements.get(id), querySelectorAll: () => [detail] };
  const scrolls = [];
  const scope = { location: { href: `revu-app://ui/${view === 'review' ? 'review' : 'vodplayer'}.html${search}`, search },
    scrollX: 0, scrollY: 210, scrollTo: value => scrolls.push(value),
    addEventListener(name, callback) { listeners.set(name, callback); } };
  let draft = { raw: '  Unfinished note  ' };
  const restored = [];
  const nav = createMatchNavigation({ view, document: doc, scope, store, beforeLeave, canCapture,
    capture: () => draft, restore: state => restored.push(state) });
  nav.setGame(12);
  return { nav, store, scope, doc, root, links, status, detail, scrolls, restored, listeners,
    edit: value => { draft = value; } };
}

test('switch captures edits before and after an in-flight draft write, then navigates to the same match', async () => {
  let finish;
  const saving = new Promise(resolve => { finish = resolve; });
  const f = fixture({ beforeLeave: () => saving });
  const switching = f.nav.navigate('vodplayer.html?gameId=12');
  assert.deepEqual(f.store.peek(12, 'review').state, { raw: '  Unfinished note  ' });
  assert.equal(f.scope.location.href.includes('review.html'), true);
  f.edit({ raw: 'A last edit while saving' }); finish();
  assert.equal(await switching, true);
  assert.deepEqual(f.store.peek(12, 'review').state, { raw: 'A last edit while saving' });
  assert.equal(f.scope.location.href, 'revu-app://ui/vodplayer.html?gameId=12&resume=1');
});

test('leaving a loading view preserves the unconsumed snapshot until the provider is ready', async () => {
  let ready = false;
  const f = fixture({ view: 'vod', canCapture: () => ready });
  const previous = { version: 1, gameId: 12, state: { mediaTime: 400 } };
  f.store.save(12, 'vod', previous);
  assert.equal(await f.nav.navigate('review.html?gameId=12'), true);
  f.listeners.get('pagehide')();
  assert.deepEqual(f.store.peek(12, 'vod'), previous);
  ready = true; f.nav.captureState();
  assert.deepEqual(f.store.peek(12, 'vod').state, { raw: '  Unfinished note  ' });
});

test('return restores only this match once, including disclosure and scroll state', async () => {
  const f = fixture(); f.detail.open = true; f.nav.captureState(); f.detail.open = false;
  f.store.save(99, 'review', { version: 1, gameId: 99, state: { raw: 'Different match' } });
  assert.equal(await f.nav.restoreState(), true);
  assert.deepEqual(f.restored, [{ raw: '  Unfinished note  ' }]);
  assert.equal(f.detail.open, true);
  assert.deepEqual(f.scrolls, [{ left: 0, top: 210, behavior: 'instant' }]);
  assert.equal(await f.nav.restoreState(), false);
  assert.equal(f.store.peek(12, 'review'), null);
  assert.ok(f.store.peek(99, 'review'));
});

test('ordinary entry never overlays old state and explicit VOD targets never restore old scroll', async () => {
  const ordinary = fixture({ search: '?gameId=12' }); ordinary.nav.captureState();
  assert.equal(await ordinary.nav.restoreState(), false);
  assert.deepEqual(ordinary.restored, []);
  const explicit = fixture({ view: 'vod', search: '?gameId=12&resume=1&t=0' }); explicit.nav.captureState();
  assert.equal(await explicit.nav.restoreState(), true);
  assert.equal(explicit.restored.length, 1);
  assert.deepEqual(explicit.scrolls, []);
});

test('successful commit invalidation cannot resurrect a stale draft during teardown', async () => {
  const f = fixture(); f.nav.captureState(); f.nav.invalidate();
  f.listeners.get('pagehide')();
  assert.equal(f.store.peek(12, 'review'), null);
  f.nav.setGame(12); f.nav.captureState();
  assert.equal(f.store.peek(12, 'review'), null);
  f.nav.enableCapture(); f.edit({ raw: 'New edit after saving' }); f.nav.captureState();
  assert.deepEqual(f.store.peek(12, 'review').state, { raw: 'New edit after saving' });
});

test('failed departure leaves the current document and its snapshot available', async () => {
  const f = fixture({ beforeLeave: () => { throw new Error('Write unavailable'); } });
  const original = f.scope.location.href;
  assert.equal(await f.nav.navigate('vodplayer.html?gameId=12'), false);
  assert.equal(f.scope.location.href, original);
  assert.equal(f.status.hidden, false);
  assert.ok(f.store.peek(12, 'review'));
});
