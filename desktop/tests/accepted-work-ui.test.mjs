import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Auto-clip batches and update downloads are accepted sidecar work: they continue past the
// page's request deadline. The UI treats a deadline as "still working" and picks the result
// up from SSE (auto-clip) or by asking again, which joins the same download.
const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
const player = await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8');
class CustomEvent { constructor(type, options) { this.type = type; this.detail = options.detail; } }
const timeout = () => new Error("Error invoking remote method 'revu:command': TimeoutError: The operation was aborted due to timeout");

async function shellFor(page) {
  const code = outer.slice(outer.indexOf('async function wireLiveAutoShow()'), outer.indexOf('\nwireLiveAutoShow();'));
  const events = [];
  let callback;
  const scope = { location: { pathname: `/${page}`, href: page }, dispatchEvent: event => events.push(event) };
  const context = vm.createContext({
    window: scope, frame: { contentWindow: scope }, frameHas: name => name === page, frameGoto() {},
    getInvoke: async () => async () => {}, getListen: async () => async (_name, listener) => { callback = listener; },
    CustomEvent, console: { warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.ready = wireLiveAutoShow();`, context);
  await context.ready;
  return { events, emit: (type, payload) => callback({ payload: { type, payload } }) };
}

test('auto-clip progress reaches only the VOD player frame, unchanged', async () => {
  const progress = { gameId: 42, objectiveId: 7, done: 2, total: 5, created: 2, skipped: 0, finished: false };
  for (const page of ['vodplayer.html', 'review.html', 'dashboard.html']) {
    const shell = await shellFor(page);
    shell.emit('autoClipProgress', progress);
    if (page !== 'vodplayer.html') { assert.equal(shell.events.length, 0, page); continue; }
    assert.deepEqual(shell.events.map(event => [event.type, event.detail]), [['revu:auto-clip-progress', progress]]);
  }
});

test('the update banner keeps waiting through request deadlines, then installs', async () => {
  const code = outer.slice(outer.indexOf('async function runUpdateAndRestart()'), outer.indexOf('async function checkForUpdateOnLaunch()'));
  const calls = [], texts = [], elements = { 'updbar-btn': { disabled: false }, 'updbar-txt': {} };
  let downloads = 0;
  Object.defineProperty(elements['updbar-txt'], 'textContent', { set(value) { texts.push(value); }, get() { return texts.at(-1); } });
  const context = vm.createContext({
    frame: { contentWindow: {} },
    getInvoke: async () => async command => {
      calls.push(command);
      if (command === 'download_update' && ++downloads < 3) throw timeout();
      return { ok: true };
    },
    document: { getElementById: id => elements[id] || null }, console: { error() {}, warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.run = runUpdateAndRestart;`, context);
  await context.run();
  assert.deepEqual(calls, ['download_update', 'download_update', 'download_update', 'apply_update']);
  assert.deepEqual(texts, ['Downloading update…', 'Still downloading the update…', 'Still downloading the update…',
    'Installing… the app will restart.']);
  assert.ok(!texts.includes('Update failed. Try again later.'));
});

// The page's real auto-clip handler and SSE listener, run against stubs.
function autoClip(reply) {
  const start = player.indexOf('async function runAutoClipForObjective(');
  const end = player.indexOf("window.addEventListener('revu:auto-clip-progress', onAutoClipProgress);");
  const log = { hints: [], reloads: 0, dispatched: [], invokes: 0 };
  const context = vm.createContext({
    window: { dispatchEvent: event => log.dispatched.push(event) }, CustomEvent, console: { error() {} },
    log, reply,
  });
  vm.runInContext(`
    let _autoClipBusy = false, _autoClipEnabled = true, _gameId = 42, _vod = { filePath: 'match.mp4' };
    let _core = { invoke: async () => { log.invokes++; return reply(); } };
    function openClipTools() {}
    function setAutoClipHint(message, isError) { log.hints.push(JSON.stringify([message, isError])); }
    function renderObjBar() {} function renderAutoClipPanel() {} function showSavedMoments() {}
    function objectiveTiedEventCount() { return 4; } function clippableMarkerCountForObjective() { return 4; }
    async function reloadBookmarks() { log.reloads++; }
    ${player.slice(start, end)}
    globalThis.run = runAutoClipForObjective; globalThis.progress = onAutoClipProgress;
    globalThis.busy = () => _autoClipBusy;`, context);
  return { log, run: objectiveId => context.run(objectiveId), progress: detail => context.progress({ detail }) };
}

test('an auto-clip request deadline is not a failure; the finished batch is picked up from SSE', async () => {
  const clip = autoClip(() => { throw timeout(); });
  await clip.run(null);
  assert.deepEqual(JSON.parse(clip.log.hints.at(-1)), ['Still clipping in the background. New clips appear here when they are ready.', false]);
  assert.ok(!clip.log.hints.some(hint => JSON.parse(hint)[1]));
  assert.equal(clip.log.reloads, 1); // clips saved so far show at once

  clip.progress({ gameId: 99, finished: true, ok: true, created: 5 });
  assert.equal(clip.log.reloads, 1); // another game's batch
  clip.progress({ gameId: 42, done: 3, total: 5, finished: false });
  assert.equal(clip.log.reloads, 1);
  clip.progress({ gameId: 42, objectiveId: 7, finished: true, ok: true, created: 5, skipped: 1 });
  assert.equal(clip.log.reloads, 2);
  assert.deepEqual(JSON.parse(clip.log.hints.at(-1)), ['Saved 5 clips.', false]);
  assert.deepEqual(JSON.parse(JSON.stringify(clip.log.dispatched.map(event => [event.type, event.detail]))),
    [['revu:first-review-autoclip-done', { gameId: 42, objectiveId: 7, created: 5, skipped: 1 }]]);
});

test('a definite auto-clip failure still says so and shows the clips that were saved', async () => {
  const clip = autoClip(() => { throw new Error('Auto-clip failed (is ffmpeg installed?).'); });
  await clip.run(null);
  assert.deepEqual(JSON.parse(clip.log.hints.at(-1)), ['Auto-clip failed; is ffmpeg installed?', true]);
  assert.equal(clip.log.reloads, 1);
});

test('while the page still waits, SSE shows progress and leaves the result to the request', async () => {
  let finish;
  const clip = autoClip(() => new Promise(resolve => { finish = resolve; }));
  const running = clip.run(null);
  clip.progress({ gameId: 42, done: 2, total: 5, finished: false });
  assert.deepEqual(JSON.parse(clip.log.hints.at(-1)), ['Auto-clipping… 2 of 5 done.', false]);
  clip.progress({ gameId: 42, finished: true, ok: true, created: 5 });
  assert.equal(clip.log.reloads, 0);
  finish({ ok: true, created: 5, skipped: 0 });
  await running;
  assert.deepEqual(JSON.parse(clip.log.hints.at(-1)), ['Saved 5 clips.', false]);
  assert.equal(clip.log.reloads, 1);
  assert.equal(clip.log.dispatched.length, 1);
});
