import test from 'node:test';
import assert from 'node:assert/strict';
import { createVodWriteBarrier, sameVodDraft } from '../ui/vod-view-state.mjs';
import { createMatchNavigation, createMatchViewStore } from '../ui/match-navigation.mjs';

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function navigation(writes, capture) {
  const store = createMatchViewStore();
  const scope = { location: { href: 'revu-app://ui/vodplayer.html?gameId=42', search: '?gameId=42' }, addEventListener() {} };
  const nav = createMatchNavigation({ view: 'vod', scope, store, capture,
    document: { getElementById: () => null, querySelectorAll: () => [] }, beforeLeave: () => writes.flush() });
  nav.setGame(42);
  return { nav, scope, store };
}

test('switch waits for bookmark and clip completion including cleanup and reload before recapturing', async () => {
  const writes = createVodWriteBarrier();
  const bookmarkReply = deferred(), clipReply = deferred(), reload = deferred();
  let bookmark = 'Submitted bookmark', clip = { note: 'Submitted clip', start: 10, end: 20 }, calls = 0;
  const bookmarkJob = writes.run('bookmark', async () => { calls++; await bookmarkReply.promise; bookmark = ''; });
  assert.equal(writes.run('bookmark', () => { throw new Error('Duplicate keyboard submission'); }), bookmarkJob);
  writes.run('clip', async () => { await clipReply.promise; clip = { note: '', start: -1, end: -1 }; await reload.promise; });
  const f = navigation(writes, () => ({ bookmark, clip }));
  const originalUrl = f.scope.location.href;
  const leaving = f.nav.navigate('review.html?gameId=42');
  bookmarkReply.resolve(); clipReply.resolve();
  await bookmarkJob;
  assert.equal(f.scope.location.href, originalUrl);
  assert.equal(writes.pending, true, 'native completion alone does not finish UI cleanup');
  reload.resolve();
  assert.equal(await leaving, true);
  assert.deepEqual(f.store.peek(42, 'vod').state, { bookmark: '', clip: { note: '', start: -1, end: -1 } });
  assert.equal(calls, 1);
  assert.equal(writes.pending, false);
});

test('successful save retains newer raw note, range, quality and picker changes made before its reply', async () => {
  const writes = createVodWriteBarrier(), reply = deferred();
  let clip = { note: 'Submitted', start: 10, end: 20, quality: 'good', picker: 'obj:1' };
  const submitted = { ...clip };
  writes.run('clip', async () => { await reply.promise; if (sameVodDraft(clip, submitted)) clip = null; });
  const f = navigation(writes, () => ({ clip }));
  const leaving = f.nav.navigate('review.html?gameId=42');
  clip = { note: '  Next clip\n', start: 35, end: 49, quality: 'bad', picker: 'obj:2' };
  reply.resolve();
  await leaving;
  assert.deepEqual(f.store.peek(42, 'vod').state.clip, clip);
});

test('failed native save leaves the draft available and does not block subsequent switching or retry', async () => {
  const writes = createVodWriteBarrier(), reply = deferred();
  const draft = { note: 'Keep failed clip', start: 10, end: 20 };
  const job = writes.run('clip', () => reply.promise);
  const f = navigation(writes, () => draft);
  const leaving = f.nav.navigate('review.html?gameId=42');
  reply.reject(new Error('Native save failed'));
  await assert.rejects(job, /Native save failed/);
  assert.equal(await leaving, true);
  assert.deepEqual(f.store.peek(42, 'vod').state, draft);
  assert.equal(await writes.run('clip', async () => 'retry complete'), 'retry complete');
});
