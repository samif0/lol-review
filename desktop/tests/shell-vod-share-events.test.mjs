import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// 4.1: full-game share progress reaches only the VOD player frame, unchanged, and
// never navigates or reloads whatever page is open.
const outer = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
class CustomEvent { constructor(type, options) { this.type = type; this.detail = options.detail; } }

// Run the real subscription callback of the persistent shell against one framed page.
async function shellFor(page, { frameThrows = false } = {}) {
  const code = outer.slice(outer.indexOf('async function wireLiveAutoShow()'), outer.indexOf('\nwireLiveAutoShow();'));
  const events = [], navigations = [];
  let callback;
  const scope = { location: { pathname: `/${page}`, href: page },
    dispatchEvent: event => { if (frameThrows) throw new Error('frame is navigating'); events.push(event); } };
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

test('vodShareProgress reaches only the VOD player frame, unchanged and without navigation', async () => {
  const progress = { gameId: 42, state: 'running', phase: 'uploading', percent: 61, quality: '1080p', qualityReason: null,
    url: null, expiresAt: null, markersAttached: null, errorCode: null, error: null, retryAt: null, needsLogin: false };
  const done = { ...progress, state: 'done', phase: 'done', percent: 100, url: 'https://revu.lol/v/Abc1234',
    expiresAt: 1760659200, markersAttached: true };
  for (const page of ['vodplayer.html', 'review.html', 'games.html', 'patterns.html', 'dashboard.html', 'settings.html']) {
    const shell = await shellFor(page);
    shell.emit('vodShareProgress', progress);
    shell.emit('vodShareProgress', done);
    assert.deepEqual(shell.navigations, [], page);
    if (page !== 'vodplayer.html') { assert.equal(shell.events.length, 0, page); continue; }
    assert.deepEqual(shell.events.map(event => event.type), ['revu:vod-share-progress', 'revu:vod-share-progress']);
    assert.deepEqual(shell.events.map(event => event.detail), [progress, done]);
  }
});

test('a frame that cannot take the event is skipped quietly', async () => {
  const shell = await shellFor('vodplayer.html', { frameThrows: true });
  assert.doesNotThrow(() => shell.emit('vodShareProgress', { gameId: 42, state: 'running', phase: 'preparing', percent: 3 }));
  assert.deepEqual(shell.navigations, []);
});
