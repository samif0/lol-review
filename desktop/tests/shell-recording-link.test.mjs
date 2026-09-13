import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Exercise the actual subscription callbacks without loading window chrome.
async function subscribe(file, page) {
  const source = await readFile(new URL(`../ui/${file}`, import.meta.url), 'utf8');
  const outer = file === 'shell-outer.js';
  const end = source.indexOf(outer ? '\nwireLiveAutoShow();' : '\nif (!FRAMED) wireLiveAutoShow();');
  const code = source.slice(source.indexOf('async function wireLiveAutoShow()'), end);
  const events = [], navigations = [], calls = [];
  let callback, subscriptions = 0;
  const scope = { location: { pathname: `/${page}`, href: page },
    dispatchEvent: event => events.push(event) };
  const context = vm.createContext({
    window: scope, frame: { contentWindow: scope },
    frameHas: name => name === page, frameGoto: target => navigations.push(target),
    getInvoke: async () => async command => calls.push(command),
    getListen: async () => async (name, listener) => {
      assert.equal(name, 'lcu-event'); callback = listener; subscriptions++;
    },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    console: { warn() {} },
  });
  vm.runInContext(`${code}\nglobalThis.ready = wireLiveAutoShow();`, context);
  await context.ready;
  callback({ payload: { type: 'vodLinked', payload: { gameId: 42 } } });
  return { events, navigations, calls, subscriptions, scope };
}

test('persistent shell forwards a recording link only to relevant current pages without navigation', async () => {
  for (const page of ['review.html', 'vodplayer.html', 'games.html', 'settings.html']) {
    const f = await subscribe('shell-outer.js', page);
    assert.equal(f.subscriptions, 1);
    assert.deepEqual(f.calls, ['start_lcu_events']);
    assert.deepEqual(f.navigations, []);
    assert.equal(f.scope.location.href, page);
    assert.equal(f.events.length, page === 'settings.html' ? 0 : 1);
    if (f.events.length) {
      assert.equal(f.events[0].type, 'revu:vod-linked');
      assert.deepEqual(f.events[0].detail, { gameId: 42 });
    }
  }
});

test('standalone shell forwards the same narrow recording event without reloading the page', async () => {
  const f = await subscribe('shell.js', 'vodplayer.html');
  assert.equal(f.subscriptions, 1);
  assert.equal(f.events.length, 1);
  assert.equal(f.events[0].type, 'revu:vod-linked');
  assert.deepEqual(f.events[0].detail, { gameId: 42 });
  assert.equal(f.scope.location.href, 'vodplayer.html');
});
