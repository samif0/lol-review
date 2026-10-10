import test from 'node:test';
import assert from 'node:assert/strict';
import { clipRowNarrationContext } from '../ui/clip-narration.mjs';
import { clipboardTimers, deferredClipboard, fixture, plain, sharedClipControls } from './vod-player-fixture.mjs';

const transcript = { version: 1, language: 'en', segments: [{ start: 1.234, end: 3.5, text: 'first gank comes at three minutes' }] };
const narration = { bookmarkId: 301, narratedClipPath: 'clips/narrated/a.mp4', durationMs: 9000, offsetMs: -40, gameVolume: 0.8,
  narrationVolume: 1, duck: true, transcriptStatus: 'ready', transcriptLanguage: 'en', transcriptError: '', transcript, updatedAt: 5 };

function clipFixture(options = {}) {
  const f = fixture({ narrationOk: true, ...options });
  f.snapshot.savedClips = [{ id: 41, hasClip: true, startTimeSeconds: 20, endTimeSeconds: 30, shareBookmarkId: 301,
    shareUrl: '', clipPath: 'clips/a.mp4', narration }];
  f.snapshot.bookmarks = [{ id: 302, hasClip: true, gameTimeSeconds: 50, clipStartSeconds: 45, clipEndSeconds: 60,
    clipPath: 'clips/b.mp4', narration: null, shareUrl: 'https://revu.lol/b' }];
  f.hooks.setFilter('clips');
  f.hooks.renderMoments();
  const row = id => f.$('vp-bookmarks').children.find(item => item.dataset.shareBmId === String(id));
  const part = (id, name) => row(id).querySelector(`.${name}`);
  return { ...f, row, part };
}

test('the studio context reads either saved-clip row shape', () => {
  assert.deepEqual(clipRowNarrationContext({ id: 41, shareBookmarkId: 301, startTimeSeconds: 20, clipPath: 'clips/a.mp4', narration, shareUrl: 'u' }),
    { bookmarkId: 301, clipPath: 'clips/a.mp4', narration, clipStartSeconds: 20, shareUrl: 'u' });
  assert.deepEqual(clipRowNarrationContext({ id: 302, hasClip: true, gameTimeSeconds: 50, clipStartSeconds: 45, clipPath: null, narration: null }),
    { bookmarkId: 302, clipPath: null, narration: null, clipStartSeconds: 45, shareUrl: '' });
  assert.equal(clipRowNarrationContext({ id: 9, gameTimeSeconds: 12 }).clipStartSeconds, 12);
  assert.equal(clipRowNarrationContext({ id: 9, shareBookmarkId: 0, clipPath: '' }).bookmarkId, 0);
  assert.equal(clipRowNarrationContext(null), null);
});

test('clip rows offer Narrate or Edit narration, the Narrated chip and the transcript', () => {
  const f = clipFixture();
  assert.equal(f.part(301, 'vp-narrate-btn').textContent, 'Edit narration');
  assert.equal(f.part(301, 'vp-narrate-btn').hidden, false);
  assert.equal(f.part(301, 'vp-narrated-chip').hidden, false);
  const host = f.part(301, 'vp-clip-transcript-host');
  assert.equal(host.hidden, false);
  assert.equal(host.dataset.clipStart, '20');
  assert.equal(host.querySelector('summary').textContent, 'Transcript (1 line)');
  assert.equal(f.part(302, 'vp-narrate-btn').textContent, 'Narrate');
  assert.equal(f.part(302, 'vp-narrated-chip').hidden, true);
  assert.equal(f.part(302, 'vp-clip-transcript-host').hidden, true);
  assert.equal(f.part(302, 'vp-clip-transcript-host').dataset.clipStart, '45');
  const preview = clipFixture({ narrationOk: false });
  assert.equal(preview.part(301, 'vp-narrate-btn').hidden, true, 'isolated previews cannot save narration');
});

test('Narrate opens the studio for that clip without jumping, pausing the VOD and reloading on change', async () => {
  const f = clipFixture();
  f.transport.paused = false; f.$('vp-video').paused = false;
  const button = f.part(302, 'vp-narrate-btn');
  const event = await f.emit('click', button);
  assert.equal(event.defaultPrevented, true);
  assert.deepEqual(f.seeks, [], 'the row jump never fires');
  assert.equal(f.studios.length, 1);
  const { options } = f.studios[0];
  assert.equal(options.dialog, f.$('vp-narration'));
  assert.deepEqual(plain(options.ctx), { bookmarkId: 302, clipPath: 'clips/b.mp4', narration: null, clipStartSeconds: 45, shareUrl: 'https://revu.lol/b' });
  assert.equal(options.gameId, 42); assert.equal(options.opener, button); assert.equal(options.invoke, f.core.invoke);
  assert.equal(options.media, f.core); assert.equal(typeof options.platform.narrationAvailable, 'function');
  options.pauseMainVod();
  assert.equal(f.$('vp-video').paused, true);
  f.snapshot.bookmarks[0].narration = { ...narration, bookmarkId: 302 };
  const reads = f.reads.length;
  const fresh = await options.onChanged();
  assert.equal(f.reads.length, reads + 1);
  assert.equal(fresh.narration.bookmarkId, 302);
  await f.emit('click', f.part(301, 'vp-narrate-btn'));
  assert.equal(f.studios[0].closed, true, 'opening another clip closes the previous studio');
  assert.equal(f.studios[1].options.ctx.bookmarkId, 301);
});

test('transcript lines seek to the clip start plus the line time; Retry and Sign in reach their commands', async () => {
  const f = clipFixture();
  const line = f.part(301, 'vp-clip-transcript-line');
  await f.emit('click', line);
  assert.deepEqual(f.seeks, [21.234]);
  f.snapshot.savedClips[0].narration = { ...narration, transcriptStatus: 'failed', transcript: null };
  f.hooks.renderMoments();
  const retry = f.part(301, 'vp-clip-transcript-action');
  assert.equal(retry.textContent, 'Retry');
  const reads = f.reads.length;
  await f.emit('click', retry);
  assert.deepEqual(f.writes.at(-1), { command: 'transcribe_clip_narration', args: { payload: { gameId: 42, bookmarkId: 301 } } });
  assert.equal(f.reads.length, reads + 1);
  f.snapshot.savedClips[0].narration = { ...narration, transcriptStatus: 'needs_login', transcript: null };
  f.hooks.renderMoments();
  await f.emit('click', f.part(301, 'vp-clip-transcript-action'));
  assert.equal(f.$('vp-sharelogin').hidden, false);
  assert.equal(f.$('vp-sl-msg').textContent, 'Sign in to get a transcript of your narration.');
});

test('narration updates for this game refresh rows once, never during a studio save, and record chunk progress', async () => {
  const timers = clipboardTimers();
  const f = clipFixture({ timers });
  const updated = f.windowListener('revu:clip-narration-updated');
  const reads = f.reads.length;
  updated({ detail: { gameId: 43, bookmarkId: 301, transcriptStatus: 'processing', chunksDone: 1, chunksTotal: 4 } });
  assert.equal(timers.size, 0, 'another game never reloads');
  updated({ detail: { gameId: 42, bookmarkId: 301, transcriptStatus: 'processing', chunksDone: 2, chunksTotal: 4 } });
  updated({ detail: { gameId: 42, bookmarkId: 301, transcriptStatus: 'processing', chunksDone: 3, chunksTotal: 4 } });
  f.snapshot.savedClips[0].narration = { ...narration, transcriptStatus: 'processing', transcript: null };
  timers.advance(300);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.reads.length, reads + 1);
  assert.equal(f.part(301, 'vp-clip-transcript-status').textContent, 'Writing transcript (3 of 4)...');
  await f.emit('click', f.part(301, 'vp-narrate-btn'));
  f.studios[0].state = 'saving';
  updated({ detail: { gameId: 42, bookmarkId: 301, transcriptStatus: 'pending' } });
  timers.advance(300);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.reads.length, reads + 1, 'the studio reloads its own save');
});

test('clips over 10 minutes cannot be saved and long clips warn that saving takes a minute', async () => {
  const pending = deferredClipboard();
  const f = fixture({ invokeWrite: command => (command === 'extract_clip' ? pending.promise : { ok: true }) });
  f.transport.currentTime = 10; f.hooks.setClipIn();
  f.transport.currentTime = 611; f.hooks.setClipOut();
  assert.equal(f.$('vp-clip-save').disabled, true);
  assert.equal(f.$('vp-clip-hint').textContent, 'Clips can be up to 10 minutes.');
  await f.hooks.saveClip();
  assert.equal(f.writes.length, 0);
  f.transport.currentTime = 610; f.hooks.setClipOut();
  assert.equal(f.$('vp-clip-save').disabled, false);
  assert.equal(f.$('vp-clip-hint').textContent, '', 'the limit hint clears once the range fits');
  const saving = f.hooks.saveClip();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.$('vp-clip-hint').textContent, 'Saving clip... Long clips can take a minute to save.');
  assert.deepEqual(f.writes.map(write => [write.command, write.args.payload.startTimeS, write.args.payload.endTimeS]), [['extract_clip', 10, 610]]);
  pending.resolve({ ok: true });
  await saving;
});

test('deleting a clip confirms that its narration and transcript go too', async () => {
  const f = clipFixture();
  await f.emit('click', f.part(301, 'vp-clipdel-btn'));
  assert.deepEqual(f.confirmations, ['Delete this clip? Its narration and transcript are deleted too.']);
  assert.deepEqual(f.writes.at(-1), { command: 'delete_clip', args: { payload: { gameId: 42, bookmarkId: 301 } } });
});

function shareFixture(replies, shareWait) {
  const timers = clipboardTimers(), waits = [];
  const f = fixture({ timers, shareWait: options => { const wait = shareWait(options); waits.push({ options, ...wait }); return wait; },
    invokeWrite: command => (command === 'share_clip' ? replies.shift() : { ok: true }) });
  const controls = sharedClipControls(f, '');
  const job = f.hooks.shareJob(301);
  return { f, timers, waits, controls, job };
}
const controllable = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject, cancel() { reject(new Error('Share cancelled.')); } };
};

test('an accepted share waits for the background job, shows upload progress and finishes with the link', async () => {
  const wait = controllable();
  const s = shareFixture([{ ok: true, accepted: true, jobId: 'job-1', narrated: true }], () => wait);
  const result = s.f.hooks.uploadShareJob(s.job);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(s.waits.length, 1);
  assert.equal(s.waits[0].options.bookmarkId, 301);
  assert.equal(s.waits[0].options.invoke, s.f.core.invoke);
  s.waits[0].options.onProgress({ bookmarkId: 301, phase: 'uploading', sentBytes: 42, totalBytes: 100 });
  assert.equal(s.controls.link.textContent, 'Uploading 42%');
  assert.equal(s.f.$('vp-sharebar-txt').textContent, 'Sharing clips. Long clips can take several minutes to upload.');
  s.waits[0].options.onProgress({ bookmarkId: 301, phase: 'transcript' });
  assert.equal(s.controls.link.textContent, 'Adding transcript...');
  wait.resolve({ url: 'https://revu.lol/abc', narrated: true, transcriptAttached: true });
  assert.equal(await result, 'done');
  assert.equal(s.job.url, 'https://revu.lol/abc');
  assert.deepEqual(s.f.writes.map(write => write.command), ['share_clip', 'copy_text_to_clipboard']);
});

test('only retryable job failures retry; non-retryable answers and client timeouts never upload twice', async () => {
  const retryable = controllable(), retried = controllable();
  const pending = [retryable, retried];
  const s = shareFixture([{ ok: true, accepted: true }, { ok: true, accepted: true }], () => pending.shift());
  const result = s.f.hooks.uploadShareJob(s.job);
  await new Promise(resolve => setImmediate(resolve));
  retryable.reject(Object.assign(new Error('Upload interrupted.'), { retryable: true, needsLogin: false }));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(s.timers.size, 1, 'one backoff timer');
  assert.equal(s.job.status, 'retry_wait');
  s.timers.advance(1500);
  await new Promise(resolve => setImmediate(resolve));
  retried.reject(Object.assign(new Error('Daily share limit reached.'), { retryable: false, needsLogin: false }));
  assert.equal(await result, 'failed');
  assert.equal(s.job.message, 'Daily share limit reached.');
  assert.deepEqual(s.f.writes.map(write => write.command), ['share_clip', 'share_clip']);

  for (const reply of [{ ok: false, error: 'Share temporarily unavailable.', retryable: false },
    { ok: false, error: 'Clips can be up to 10 minutes. Trim the range and try again.' },
    { ok: false, error: 'Narration changed. Share again to update the link.' }]) {
    const once = shareFixture([reply], () => controllable());
    assert.equal(await once.f.hooks.uploadShareJob(once.job), 'failed', reply.error);
    assert.equal(once.timers.size, 0, reply.error);
  }
  const timedOut = fixture({ timers: clipboardTimers(), invokeWrite: () => { throw new Error('The operation was aborted due to timeout'); } });
  assert.equal(await timedOut.hooks.uploadShareJob(timedOut.hooks.shareJob(301)), 'failed');
  assert.deepEqual(timedOut.writes.map(write => write.command), ['share_clip']);
});

test('a job that needs sign-in pauses the queue and opens the login panel', async () => {
  const wait = controllable();
  const s = shareFixture([{ ok: true, accepted: true }], () => wait);
  const result = s.f.hooks.uploadShareJob(s.job);
  await new Promise(resolve => setImmediate(resolve));
  wait.reject(Object.assign(new Error('Log in to share clips.'), { retryable: false, needsLogin: true }));
  assert.equal(await result, 'needs_login');
  assert.equal(s.f.$('vp-sharelogin').hidden, false);
  assert.equal(s.f.hooks.pendingShareBmId, 301);
  // Signing in for a transcript meanwhile keeps the paused share, so the login still resumes it.
  const signIn = s.f.document.createElement('button');
  signIn.dataset.action = 'transcript_signin';
  await s.f.emit('click', signIn);
  assert.equal(s.f.$('vp-sl-msg').textContent, 'Sign in to get a transcript of your narration.');
  assert.equal(s.f.hooks.pendingShareBmId, 301);
});

test('a transcript sign-in with no paused share queues nothing after login', async () => {
  const f = fixture();
  const signIn = f.document.createElement('button');
  signIn.dataset.action = 'transcript_signin';
  await f.emit('click', signIn);
  assert.equal(f.$('vp-sharelogin').hidden, false);
  assert.equal(f.hooks.pendingShareBmId, 0);
});

test('deleting a clip mid-share forgets its job without reporting a failure', async () => {
  const wait = controllable();
  const s = shareFixture([{ ok: true, accepted: true }], () => wait);
  const result = s.f.hooks.uploadShareJob(s.job);
  await new Promise(resolve => setImmediate(resolve));
  s.f.hooks.cancelShareJob(301);
  assert.equal(await result, 'failed');
  assert.equal(s.f.hooks.shareJobs.has(301), false);
  assert.notEqual(s.job.status, 'error');
  assert.equal(s.f.$('vp-sharebar').hidden, true);
});

test('share errors about clip length, size, quota and narration are never retried automatically', () => {
  const f = fixture();
  for (const message of ['Clips can be up to 10 minutes.', 'Clip is too big (2 GB max).', 'This clip is still uploading.',
    'Daily share limit reached.', 'Share quota exceeded.', 'Narration changed. Share again to update the link.',
    'Only MP4 and WebM clips can be shared.', 'Clip file is missing. Save the clip again.'])
    assert.equal(f.hooks.isRetryableShareError(message, ''), false, message);
  for (const message of ['Share temporarily unavailable. Try again.', 'HTTP 503', 'Too many uploads. Wait a moment.'])
    assert.equal(f.hooks.isRetryableShareError(message, ''), true, message);
});

test('transcript progress never re-renders the rows while a moment note is being typed', async () => {
  const timers = clipboardTimers();
  const f = clipFixture({ timers });
  const updated = f.windowListener('revu:clip-narration-updated');
  const reads = f.reads.length;
  const note = f.part(301, 'vp-ev-note');
  note.focus();
  note.value = 'half typed';
  updated({ detail: { gameId: 42, bookmarkId: 301, transcriptStatus: 'processing', chunksDone: 1, chunksTotal: 4 } });
  timers.advance(300);
  timers.advance(1000);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.reads.length, reads, 'no reload while the note has focus');
  assert.equal(f.part(301, 'vp-ev-note'), note, 'the row being edited is untouched');
  assert.equal(f.document.activeElement, note);
  note.blur();
  timers.advance(1000);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.reads.length, reads + 1, 'the deferred reload runs once the field is left');
  assert.equal(timers.size, 0);
});

test('while the studio is open, keys that land on the page never reach the VOD shortcuts', async () => {
  const f = clipFixture();
  const shortcuts = [];
  f.transport.handleShortcut = event => { shortcuts.push(event.key); return false; };
  await f.emit('click', f.part(302, 'vp-narrate-btn'));
  const studio = f.studios[0];
  let closeRequests = 0;
  studio.requestClose = () => { closeRequests++; };
  const body = f.document.createElement('body');
  for (const key of [' ', 's', 'b', 'Delete']) {
    const event = await f.emit('keydown', body, { key });
    assert.equal(event.defaultPrevented, undefined, key);
  }
  assert.deepEqual(shortcuts, []);
  assert.equal(f.writes.length, 0, 'S never saves a clip behind the studio');
  assert.equal(f.$('vp-composer').hidden, true, 'B never opens the bookmark tools');
  const escape = await f.emit('keydown', body, { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true);
  assert.equal(closeRequests, 1, 'Escape asks the studio to close, which confirms during a take');
  studio.state = 'closed';
  await f.emit('keydown', body, { key: ' ' });
  assert.deepEqual(shortcuts, [' ']);
});
