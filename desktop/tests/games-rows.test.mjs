import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = (await readFile(new URL('../ui/games.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');
const plain = value => JSON.parse(JSON.stringify(value));

function fixture(reply = async () => ({ items: [] })) {
  const ids = new Map(), listeners = new Map(), windowListeners = new Map(), requests = [];
  const scope = { location: { href: 'games.html' }, scrollX: 0, scrollY: 310,
    addEventListener(type, callback) { windowListeners.set(type, callback); },
    scrollTo({ left, top }) { this.scrollX = left; this.scrollY = top; } };
  function element(classes = []) {
    const names = new Set(classes), attributes = new Map();
    const node = { children: [], dataset: {}, style: {}, textContent: '', hidden: false, disabled: false, selectors: new Map(),
      get firstChild() { return this.children[0] || null; },
      set innerHTML(_) { throw new Error('Game data must never be interpreted as HTML'); },
      classList: {
        contains: name => names.has(name), add: (...values) => values.forEach(name => names.add(name)),
        remove: (...values) => values.forEach(name => names.delete(name)),
        toggle(name, force) { const active = force ?? !names.has(name); if (active) names.add(name); else names.delete(name); return active; },
      },
      appendChild(child) { this.children.push(child); child.parent = this; },
      querySelector(selector) { return this.selectors.get(selector)?.[0] || this.children.flatMap(child => child.querySelectorAll(selector))[0] || null; },
      querySelectorAll(selector) { return this.selectors.get(selector) || this.children.flatMap(child => child.querySelectorAll(selector)); },
      setAttribute(name, value) { attributes.set(name, String(value)); }, getAttribute: name => attributes.get(name) ?? null,
      closest(selector) {
        const needsRole = selector.includes('[role="button"]');
        if (selector.startsWith('[data-action]') && this.dataset.action && (!needsRole || this.getAttribute('role') === 'button')) return this;
        return this.parent?.closest(selector) || null;
      },
      click() { return emit('click', this); },
    };
    return node;
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  const document = { readyState: 'loading',
    querySelector: selector => selector === '#statusline b' ? $('status-text') : null,
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
  };
  async function emit(type, target, extra = {}) {
    const event = { target, preventDefault() { this.defaultPrevented = true; }, ...extra };
    for (const handler of listeners.get(type) || []) await handler(event);
    return event;
  }
  const segments = ['queue', 'today', 'history', 'vod'].map(view => {
    const button = element(); button.dataset = { action: 'view', view }; return button;
  });
  $('seg').selectors.set('button', segments);
  const context = vm.createContext({ document, window: scope, $, console: { error() {} },
    show: (node, on) => { if (node) node.hidden = !on; }, clear: node => { node.children = []; },
    tpl(name) {
      const node = element();
      if (name === 'tpl-gamerow') {
        node.dataset.action = 'open_review'; node.setAttribute('role', 'button');
        for (const selector of ['.grow-wl', '.grow-champ', '.grow-meta', '.grow-tokens', '.grow-kda-n', '.grow-kda-r', '.gamerow-cue']) {
          const child = element(); node.selectors.set(selector, [child]); node.appendChild(child);
        }
        const cue = node.querySelector('.gamerow-cue');
        cue.appendChild(element()); const arrow = element(); arrow.textContent = '→'; cue.appendChild(arrow);
      } else if (name === 'tpl-stat') {
        for (const selector of ['.k', '.v', '.s']) node.selectors.set(selector, [element()]);
      }
      return node;
    },
    readSnapshot: async (command, sample, args) => { requests.push({ command, sample, args: plain(args) }); return reply(args); },
  });
  vm.runInContext(`${source}\n globalThis.hooks = { buildRow, loadView, loadMore,
    get view() { return _view; }, get rows() { return _rows; } };`, context);
  return { $, hooks: context.hooks, scope, requests, segments, emit,
    linked: gameId => windowListeners.get('revu:vod-linked')({ detail: { gameId } }) };
}

test('match rows format structured metadata and KDA and show concise confirmed statuses', () => {
  const f = fixture();
  const row = f.hooks.buildRow({ gameId: 42, championName: 'Jinx', enemyChampion: 'Caitlyn', win: true, winLossText: 'W',
    gameMode: 'RANKED SOLO/DUO', datePlayed: 'SEP 12, 14:08', duration: '32:11', metaLine: 'Stale metadata',
    kills: 4, deaths: 3, assists: 7, kdaText: 'Old KDA', kdaRatio: 11 / 3, kdaRatioText: '(3.7)',
    hasReview: false, reviewStateText: 'Reviewed', hasVod: true, hasNotes: false, vodStateText: 'VOD linked - no notes',
    objectivePracticed: false, hasObjectiveEvidence: false, objectiveStateText: 'No objective tag', action: 'watch_vod', primaryAction: 'Watch VOD' });
  assert.equal(row.querySelector('.grow-champ').textContent, 'Jinx vs Caitlyn');
  assert.equal(row.querySelector('.grow-meta').textContent, 'Ranked solo/duo · Sep 12, 14:08 · 32:11');
  assert.equal(row.querySelector('.grow-kda-n').textContent, '4 / 3 / 7');
  assert.equal(row.querySelector('.grow-kda-r').textContent, '3.67 KDA');
  assert.equal(row.querySelector('.grow-wl').title, 'Win'); assert.equal(row.querySelector('.grow-wl').getAttribute('aria-label'), 'Win');
  assert.equal(row.dataset.result, 'win');
  const statuses = row.querySelector('.grow-tokens').children;
  assert.deepEqual(statuses.map(status => status.textContent), ['To review', 'Recording']);
  assert.deepEqual(statuses.map(status => status.dataset.status), ['review', 'recording']);
  assert.match(statuses[1].title, /No timestamped bookmarks/);
  assert.equal(row.querySelector('.gamerow-cue').firstChild.textContent, 'Watch VOD ');
  assert.equal(row.querySelector('.gamerow-cue').children[1].textContent, '→');
});

test('recording notes and meaningful objective state remain distinct from written review, with conservative legacy fallbacks', () => {
  const f = fixture();
  const statuses = game => f.hooks.buildRow(game).querySelector('.grow-tokens').children;
  assert.deepEqual(statuses({ hasReview: true, hasVod: true, hasNotes: true, hasObjectiveEvidence: true }).map(s => s.textContent),
    ['Reviewed', 'Recording with notes', 'Evidence tagged']);
  assert.deepEqual(statuses({ hasReview: true, hasVod: true, hasNotes: false, objectivePracticed: true }).map(s => s.textContent),
    ['Reviewed', 'Recording', 'Objective practiced']);
  assert.deepEqual(statuses({ hasVod: false, hasNotes: true, vodStateText: 'VOD linked' }).map(s => s.textContent), ['No recording']);
  assert.match(statuses({ hasVod: false })[0].title, /No linked recording was available/);
  assert.deepEqual(statuses({ reviewStateText: 'Unreviewed', vodStateText: 'VOD linked', objectiveStateText: 'No objective tag' }).map(s => s.textContent),
    ['To review', 'Recording']);
  assert.doesNotMatch(statuses({ vodStateText: 'VOD linked' })[0].title, /No timestamped|has timestamped/,
    'legacy availability alone does not establish whether bookmarks exist');
  assert.deepEqual(statuses({}), [], 'missing flags must not be presented as known absence');
  const unknown = f.hooks.buildRow({ winLossText: 'Remake', kdaText: ' 1/0/9 ', kdaRatioText: '(10.0)', statsLine: 'CS 150 · 12k dmg' });
  assert.equal(unknown.querySelector('.grow-wl').textContent, 'Remake'); assert.equal(unknown.querySelector('.grow-wl').title, '');
  assert.equal(unknown.dataset.result, 'unknown'); assert.equal(unknown.querySelector('.grow-kda-n').textContent, '1 / 0 / 9');
  assert.equal(unknown.querySelector('.grow-kda-r').textContent, '10 KDA');
  assert.equal(unknown.querySelector('.grow-meta').textContent, 'CS 150 · 12k dmg');
});

test('untrusted row strings remain plain text and cannot invent state or alter action destinations', async () => {
  const f = fixture(), text = '<img src=x onerror=alert(1)>';
  const row = f.hooks.buildRow({ gameId: '42&other=7', championName: text, enemyChampion: '<script>bad()</script>',
    metaLine: text, kdaText: text, kdaRatioText: text, reviewStateText: text, vodStateText: text,
    objectiveStateText: text, winLossText: '?', action: 'open_review', primaryAction: text });
  assert.equal(row.querySelector('.grow-champ').textContent, `${text} vs <script>bad()</script>`);
  assert.equal(row.querySelector('.grow-meta').textContent, text); assert.equal(row.querySelector('.grow-kda-n').textContent, text);
  assert.equal(row.querySelector('.grow-kda-r').textContent, text);
  assert.equal(row.querySelector('.grow-tokens').children.length, 0);
  await f.emit('click', row);
  assert.equal(f.scope.location.href, 'review.html?gameId=42%26other%3D7');
});

test('row cues match supplied actions and Enter/Space preserve whole-row destinations', async () => {
  const f = fixture();
  for (const [game, cue, destination, key] of [
    [{ gameId: 1, action: 'watch_vod', primaryAction: 'Review', hasVod: true }, 'Watch VOD ', 'vodplayer.html?gameId=1', 'Enter'],
    [{ gameId: 2, action: 'open_review', primaryAction: 'Watch VOD', hasReview: false, hasVod: true }, 'Review ', 'review.html?gameId=2', ' '],
    [{ gameId: 3, action: 'open_review', primaryAction: 'Watch VOD', hasReview: true }, 'Open ', 'review.html?gameId=3', 'Enter'],
    [{ gameId: 4, primaryAction: 'Review', winLossText: 'L' }, 'Review ', 'review.html?gameId=4', ' '],
  ]) {
    const row = f.hooks.buildRow(game);
    assert.equal(row.querySelector('.gamerow-cue').firstChild.textContent, cue);
    const event = await f.emit('keydown', row.querySelector('.grow-champ'), { key });
    assert.equal(event.defaultPrevented, true); assert.equal(f.scope.location.href, destination);
  }
  const loss = f.hooks.buildRow({ winLossText: 'L' });
  assert.equal(loss.querySelector('.grow-wl').title, 'Loss');
});

test('native snapshot views and History pagination retain filters, row order and shown counts', async () => {
  const replies = {
    'history:0': { view: 'history', items: [{ gameId: 1, championName: 'Jinx', hasReview: true }], totalCount: 2, hasMore: true },
    'history:1': { view: 'history', items: [{ gameId: 2, championName: 'Ahri', hasReview: false }], totalCount: 2, hasMore: false },
    'today:0': { view: 'today', items: [{ gameId: 3, championName: 'Ashe', hasReview: false }], hasMore: false },
  };
  const f = fixture(async ({ view, page }) => replies[`${view}:${page}`]);
  await f.hooks.loadView('history');
  assert.equal(f.$('games-sub').textContent, '1 shown'); assert.equal(f.$('loadwrap').hidden, false);
  await f.hooks.loadMore();
  assert.deepEqual(plain(f.hooks.rows).map(game => game.gameId), [1, 2]);
  assert.equal(f.$('games-sub').textContent, '2 shown'); assert.equal(f.$('loadwrap').hidden, true);
  await f.emit('click', f.segments.find(segment => segment.dataset.view === 'today'));
  assert.equal(f.hooks.view, 'today'); assert.deepEqual(plain(f.hooks.rows).map(game => game.gameId), [3]);
  assert.equal(f.$('games-sub').textContent, '1 shown');
  assert.equal(f.segments.find(segment => segment.dataset.view === 'today').getAttribute('aria-selected'), 'true');
  assert.deepEqual(f.requests.map(request => request.args), [{ view: 'history', page: 0 }, { view: 'history', page: 1 }, { view: 'today', page: 0 }]);
  assert.equal(replies['history:0'].items[0].hasReview, true, 'presentation does not mutate server-owned status fields');
});

test('a linked recording patches only its mounted History row and keeps pagination, scroll and navigation intact', async () => {
  const f = fixture(async ({ page }) => ({ view: 'history', hasMore: page < 2, items: [
    { gameId: page + 1, championName: 'Jinx', hasVod: false, hasReview: false, action: 'open_review' },
  ] }));
  await f.hooks.loadView('history'); await f.hooks.loadMore();
  const rows = [...f.$('games-list').children];
  await f.linked(2);
  assert.equal(f.requests.length, 2, 'matching-row update does not refetch the library');
  assert.deepEqual(f.$('games-list').children, rows, 'focused row DOM stays mounted');
  assert.equal(rows[0].dataset.action, 'open_review');
  assert.equal(rows[1].dataset.action, 'watch_vod');
  assert.equal(rows[1].querySelector('.gamerow-cue').firstChild.textContent, 'Watch VOD ');
  assert.equal(rows[1].querySelector('.grow-tokens').children.find(token => token.dataset.status === 'recording').textContent, 'Recording');
  assert.equal(f.scope.scrollY, 310);
  assert.equal(f.hooks.view, 'history');
  await f.emit('click', rows[1]);
  assert.equal(f.scope.location.href, 'vodplayer.html?gameId=2');
  await f.hooks.loadMore();
  assert.deepEqual(f.requests.at(-1).args, { view: 'history', page: 2 });
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [1, 2, 3]);
});

test('VOD filter refresh reveals a newly linked row, but a late result cannot overwrite a user-selected view', async () => {
  let linked = false, resolveRefresh;
  const f = fixture(async ({ view }) => {
    if (view !== 'vod') return { view, items: [{ gameId: 90, action: 'open_review' }] };
    if (!linked) return { view, items: [] };
    if (linked === 'pending') return new Promise(resolve => { resolveRefresh = resolve; });
    return { view, items: [{ gameId: 42, hasVod: true, action: 'watch_vod' }] };
  });
  await f.hooks.loadView('vod');
  linked = true; await f.linked(42);
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [42]);
  assert.equal(f.$('games-empty').hidden, true); assert.equal(f.scope.scrollY, 310);
  linked = 'pending';
  const pending = f.linked(43);
  await f.hooks.loadView('today');
  resolveRefresh({ view: 'vod', items: [{ gameId: 43, hasVod: true }] });
  await pending;
  assert.equal(f.hooks.view, 'today');
  assert.deepEqual(plain(f.hooks.rows).map(row => row.gameId), [90]);
});

test('a link arriving during an older History response remains applied when that response renders', async () => {
  let resolve;
  const f = fixture(() => new Promise(done => { resolve = done; }));
  const pending = f.hooks.loadView('history');
  await f.linked(42); await f.linked(0); await f.linked('invalid');
  resolve({ view: 'history', items: [{ gameId: 42, hasVod: false, action: 'open_review' }] });
  await pending;
  assert.equal(f.$('games-list').children[0].dataset.action, 'watch_vod');
  assert.equal(f.hooks.rows[0].hasVod, true);
  assert.equal(f.requests.length, 1);
});
