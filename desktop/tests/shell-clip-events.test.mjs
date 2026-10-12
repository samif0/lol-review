import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
class CustomEvent { constructor(type, options) { this.type = type; this.detail = options.detail; } }

// Run the real subscription callback of the persistent shell against one framed page.
async function shellFor(page) {
  const end = outer.indexOf('\nwireLiveAutoShow();');
  const code = outer.slice(outer.indexOf('async function wireLiveAutoShow()'), end);
  const events = [], navigations = [];
  let callback;
  const scope = { location: { pathname: `/${page}`, href: page }, dispatchEvent: event => events.push(event) };
  const context = vm.createContext({
    window: scope, frame: { contentWindow: scope },
    frameHas: name => name === page, frameGoto: target => navigations.push(target),
    getInvoke: async () => async () => {}, getListen: async () => async (_name, listener) => { callback = listener; },
    CustomEvent, console: { warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.ready = wireLiveAutoShow();`, context);
  await context.ready;
  return { events, navigations, emit: (type, payload) => callback({ payload: { type, payload } }) };
}

test('narration and share progress events reach only the VOD player frame, unchanged and without navigation', async () => {
  const narration = { gameId: 42, bookmarkId: 7, transcriptStatus: 'processing', chunksDone: 3, chunksTotal: 10 };
  const share = { gameId: 42, bookmarkId: 7, phase: 'uploading', sentBytes: 42, totalBytes: 100 };
  for (const page of ['vodplayer.html', 'review.html', 'games.html', 'patterns.html', 'dashboard.html']) {
    const shell = await shellFor(page);
    shell.emit('clipNarrationUpdated', narration);
    shell.emit('clipShareProgress', share);
    assert.deepEqual(shell.navigations, [], page);
    if (page !== 'vodplayer.html') { assert.equal(shell.events.length, 0, page); continue; }
    assert.deepEqual(shell.events.map(event => event.type), ['revu:clip-narration-updated', 'revu:clip-share-progress']);
    assert.deepEqual(shell.events.map(event => event.detail), [narration, share]);
  }
});

function updater(revuBeforeNavigate) {
  const code = outer.slice(outer.indexOf('async function runUpdateAndRestart()'), outer.indexOf('async function checkForUpdateOnLaunch()'));
  const calls = [], elements = { 'updbar-btn': { disabled: false }, 'updbar-txt': { textContent: '' } };
  const context = vm.createContext({
    frame: { contentWindow: revuBeforeNavigate === undefined ? {} : { revuBeforeNavigate } },
    getInvoke: async () => async command => { calls.push(command); return { ok: true }; },
    document: { getElementById: id => elements[id] || null }, console: { error() {}, warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.run = runUpdateAndRestart;`, context);
  return { run: () => context.run(), calls, elements };
}

test('an update restart waits for the page and aborts when it refuses to leave', async () => {
  const refused = updater(async () => false);
  await refused.run();
  assert.deepEqual(refused.calls, []);
  assert.equal(refused.elements['updbar-btn'].disabled, false);
  const failing = updater(async () => { throw new Error('save failed'); });
  await failing.run();
  assert.deepEqual(failing.calls, []);
  let resolved = false;
  const allowed = updater(async () => { await new Promise(resolve => setTimeout(resolve, 5)); resolved = true; return true; });
  await allowed.run();
  assert.equal(resolved, true);
  assert.deepEqual(allowed.calls, ['download_update', 'apply_update']);
  const plainPage = updater(undefined);
  await plainPage.run();
  assert.deepEqual(plainPage.calls, ['download_update', 'apply_update']);
});
