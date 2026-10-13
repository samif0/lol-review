import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Exercise the actual subscription callbacks without loading window chrome.
async function subscribe(file, page) {
  const source = await readFile(new URL(`../ui/${file}`, import.meta.url), 'utf8');
  const end = source.indexOf('\nwireLiveAutoShow();');
  const code = source.slice(source.indexOf('async function wireLiveAutoShow()'), end);
  const events = [], navigations = [], calls = [];
  let callback, subscriptions = 0;
  const scope = { location: { pathname: `/${page}`, href: page, reload: () => navigations.push('reload') },
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
  return { events, navigations, calls, subscriptions, scope,
    recover: () => callback({ payload: { type: 'gamesRecovered', payload: { gameIds: [42] } } }) };
}

test('recovery refreshes listing pages after leaving Settings but preserves editors and the library search', async () => {
  for (const shell of ['shell-outer.js']) {
  for (const page of ['dashboard.html', 'games.html', 'settings.html', 'review.html', 'vodplayer.html']) {
    const f = await subscribe(shell, page);
    const linkedEvents = f.events.length;
    f.recover();
    assert.deepEqual(f.navigations, page === 'dashboard.html' ? ['reload'] : [], page);
    const recovered = f.events.slice(linkedEvents);
    assert.deepEqual(recovered.map(event => event.type), page === 'games.html' ? ['revu:games-recovered'] : [], page);
    if (recovered.length) assert.deepEqual(recovered[0].detail, { gameIds: [42] });
  }
  const editing = await subscribe(shell, 'dashboard.html');
  editing.scope.__revuActiveWork = true;
  editing.recover();
  assert.deepEqual(editing.navigations, []);
  // The library refreshes in place, so it never reloads, even with work in progress.
  const searching = await subscribe(shell, 'games.html');
  searching.scope.__revuActiveWork = true;
  searching.recover();
  assert.deepEqual(searching.navigations, []);
  assert.equal(searching.events.at(-1).type, 'revu:games-recovered');
  }
});

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
