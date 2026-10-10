import test from 'node:test';
import assert from 'node:assert/strict';
import { shareProgressLabel, waitForShareJob } from '../ui/clip-share-wait.mjs';

const progress = detail => Object.assign(new Event('revu:clip-share-progress'), { detail });
const settled = async promise => {
  let state = 'pending';
  promise.then(() => { state = 'resolved'; }, () => { state = 'rejected'; });
  await new Promise(resolve => setImmediate(resolve));
  return state;
};

test('resolves on the done event for its own bookmark and reports progress first', async () => {
  const target = new EventTarget(), seen = [];
  const { promise } = waitForShareJob({ bookmarkId: 5, target, pollMs: 60_000, invoke: async () => assert.fail('no poll'),
    onProgress: detail => seen.push(detail.phase) });
  target.dispatchEvent(progress({ bookmarkId: 6, phase: 'done', url: 'https://revu.lol/other' }));
  target.dispatchEvent(progress({ bookmarkId: 5, phase: 'uploading', sentBytes: 10, totalBytes: 100 }));
  assert.equal(await settled(promise), 'pending', 'events for other bookmarks are ignored');
  target.dispatchEvent(progress({ bookmarkId: 5, phase: 'done', url: 'https://revu.lol/abc', narrated: true, transcriptAttached: true }));
  assert.deepEqual(await promise, { url: 'https://revu.lol/abc', narrated: true, transcriptAttached: true });
  assert.deepEqual(seen, ['uploading', 'done']);
  target.dispatchEvent(progress({ bookmarkId: 5, phase: 'error', error: 'late' })); // listener removed
});

test('rejects on the error event with its retry and sign-in flags', async () => {
  for (const [flags, expected] of [[{ retryable: true }, [true, false]], [{ needsLogin: true }, [false, true]], [{}, [false, false]]]) {
    const target = new EventTarget();
    const { promise } = waitForShareJob({ bookmarkId: '9', target, pollMs: 60_000, invoke: async () => ({}) });
    target.dispatchEvent(progress({ bookmarkId: 9, phase: 'error', error: 'Upload stopped.', ...flags }));
    await assert.rejects(promise, error => error.message === 'Upload stopped.'
      && error.retryable === expected[0] && error.needsLogin === expected[1]);
  }
  const target = new EventTarget();
  const { promise } = waitForShareJob({ bookmarkId: 9, target, pollMs: 60_000, invoke: async () => ({}) });
  target.dispatchEvent(progress({ bookmarkId: 9, phase: 'done', url: '' }));
  await assert.rejects(promise, error => /no link/.test(error.message) && error.retryable === false);
});

test('polling covers missed events: done resolves, error rejects, idle rejects as retryable', async t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const replies = { done: { state: 'done', url: 'https://revu.lol/polled', narrated: false },
    error: { state: 'error', error: 'Daily share limit reached.', retryable: false, needsLogin: false },
    idle: { state: 'idle' } };
  for (const [name, reply] of Object.entries(replies)) {
    const calls = [];
    const statuses = [{ state: 'queued', phase: 'queued' }, { state: 'running', phase: 'uploading', sentBytes: 50, totalBytes: 200 }, reply];
    const seen = [];
    const { promise } = waitForShareJob({ bookmarkId: 4, target: new EventTarget(), pollMs: 15_000,
      onProgress: detail => seen.push(shareProgressLabel(detail)),
      invoke: async (command, args) => { calls.push([command, args]); return statuses.shift(); } });
    const outcome = promise.then(value => ({ value }), error => ({ error }));
    for (let i = 0; i < 3; i++) { t.mock.timers.tick(15_000); await new Promise(resolve => setImmediate(resolve)); }
    assert.deepEqual(calls, Array(3).fill(['get_clip_share_status', { bookmarkId: 4 }]), name);
    assert.deepEqual(seen, ['Queued', 'Uploading 25%'], name);
    const { value, error } = await outcome;
    if (name === 'done') assert.deepEqual(value, { url: 'https://revu.lol/polled', narrated: false, transcriptAttached: false });
    else assert.ok(error.retryable === (name === 'idle') && (name !== 'error' || error.message === 'Daily share limit reached.'), name);
  }
});

test('queued and running jobs have no overall deadline; repeated poll failures give up as retryable', async t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const waiting = waitForShareJob({ bookmarkId: 3, target: new EventTarget(), invoke: async () => ({ state: 'running', phase: 'finishing' }) });
  for (let i = 0; i < 200; i++) { t.mock.timers.tick(15_000); await new Promise(resolve => setImmediate(resolve)); }
  assert.equal(await settled(waiting.promise), 'pending', '50 minutes of uploading is still waiting');
  waiting.cancel();
  await assert.rejects(waiting.promise, /cancelled/);
  let failures = 0;
  const lost = waitForShareJob({ bookmarkId: 3, target: new EventTarget(), invoke: async () => { failures++; throw new Error('sidecar down'); } });
  lost.promise.catch(() => {});
  for (let i = 0; i < 3; i++) { t.mock.timers.tick(15_000); await new Promise(resolve => setImmediate(resolve)); }
  assert.equal(await settled(lost.promise), 'pending');
  t.mock.timers.tick(15_000); await new Promise(resolve => setImmediate(resolve));
  await assert.rejects(lost.promise, error => error.retryable === true);
  assert.equal(failures, 4);
});

test('invalid input fails at once without listening', async () => {
  await assert.rejects(waitForShareJob({ bookmarkId: 0, target: new EventTarget(), invoke: async () => ({}) }).promise);
  await assert.rejects(waitForShareJob({ bookmarkId: 2, target: new EventTarget() }).promise);
});

test('progress labels follow the share phases', () => {
  assert.equal(shareProgressLabel({ phase: 'uploading', sentBytes: 42, totalBytes: 100 }), 'Uploading 42%');
  assert.equal(shareProgressLabel({ phase: 'uploading', sentBytes: 999, totalBytes: 1000 }), 'Uploading 99%');
  assert.equal(shareProgressLabel({ phase: 'uploading', sentBytes: 5, totalBytes: 0 }), 'Uploading 0%');
  assert.equal(shareProgressLabel({ phase: 'uploading', sentBytes: 200, totalBytes: 100 }), 'Uploading 100%');
  assert.equal(shareProgressLabel({ phase: 'finishing' }), 'Finishing...');
  assert.equal(shareProgressLabel({ phase: 'transcript' }), 'Adding transcript...');
  assert.equal(shareProgressLabel({ phase: 'queued' }), 'Queued');
  assert.equal(shareProgressLabel({ phase: 'preparing' }), 'Queued');
  assert.equal(shareProgressLabel({ phase: 'done' }), '');
  assert.equal(shareProgressLabel(null), '');
});
