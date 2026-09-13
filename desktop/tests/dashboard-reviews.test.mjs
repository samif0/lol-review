import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = (await readFile(new URL('../ui/app.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');

function fixture(reply = async () => ({})) {
  const ids = new Map(), listeners = new Map(), requests = [], writes = [];
  function element(tagName = 'DIV') {
    const names = new Set(), attributes = new Map();
    return {
      tagName, children: [], selectors: new Map(), dataset: {}, textContent: '', hidden: false,
      get lastChild() { return this.children.at(-1) || null; },
      set innerHTML(_) { throw new Error('Dashboard data must remain plain text'); },
      classList: {
        contains: name => names.has(name), add: (...values) => values.forEach(value => names.add(value)),
        toggle(name, force) { const active = force ?? !names.has(name); if (active) names.add(name); else names.delete(name); return active; },
      },
      appendChild(child) { this.children.push(child); child.parent = this; },
      removeChild(child) { this.children.splice(this.children.indexOf(child), 1); },
      querySelector(selector) { return this.selectors.get(selector) || null; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      closest(selector) {
        if (selector === 'button' && this.tagName === 'BUTTON') return this;
        if (selector === '[data-action]' && this.dataset.action) return this;
        return this.parent?.closest(selector) || null;
      },
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  const page = element();
  $('statusline').appendChild($('status-text'));
  const document = {
    readyState: 'loading',
    querySelector: selector => selector === '.home-page' ? page : selector === '#statusline b' ? $('status-text') : null,
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
  };
  async function emit(type, target, extra = {}) {
    const event = { target, preventDefault() { this.defaultPrevented = true; }, ...extra };
    for (const handler of listeners.get(type) || []) await handler(event);
    return event;
  }
  const scope = { location: { href: 'dashboard.html', search: '' } };
  const context = vm.createContext({ document, window: scope, $, URLSearchParams, encodeURIComponent,
    console: { error() {}, info() {} },
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    tpl(name) {
      const node = element();
      const selectors = name === 'tpl-gamerow'
        ? ['.vline', '.vsmall', '.home-review-result', '.home-review-open', '.home-review-skip'] : ['.k', '.v', '.s'];
      for (const selector of selectors) {
        const child = element(selector === '.home-review-open' ? 'A' : selector === '.home-review-skip' ? 'BUTTON' : 'DIV');
        if (selector === '.home-review-skip') { child.dataset.action = 'skip_review'; child.disabled = false; }
        node.selectors.set(selector, child); node.appendChild(child);
      }
      return node;
    },
    readSnapshot: async (command, sample) => { requests.push({ command, sample }); return reply(); },
    getInvoke: async () => async (command, args) => { writes.push({ command, args: JSON.parse(JSON.stringify(args)) }); },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, renderUnreviewed, loadDashboard };`, context);
  return { $, page, scope, hooks: context.hooks, requests, writes, emit };
}

const pendingMatch = { gameId: 42, championName: 'Qiyana', enemyChampion: 'Kindred', win: false,
  gameMode: 'Ranked solo/duo', datePlayed: 'Sep 13, 12:04', duration: '27:47', kdaText: '3/9/8', hasReview: false };
const snapshot = (items, count = items.length, intent = {}) => ({ unreviewed: { count, allReviewed: count === 0, items }, intent });

test('pending review uses the authoritative queue count even when returned matches are capped', () => {
  const f = fixture();
  const items = Array.from({ length: 8 }, (_, index) => ({ ...pendingMatch, gameId: 42 - index }));
  f.hooks.render(snapshot(items, 12));
  assert.equal(f.$('review-inbox').hidden, false);
  assert.equal(f.page.classList.contains('has-pending-review'), true);
  assert.equal(f.$('unreviewed-label').textContent, '12 matches ready to review');
  assert.equal(f.$('unreviewed').children.length, 1, 'Home leads with one match instead of reproducing the whole queue');
  assert.equal(f.$('review-queue-footer').hidden, false);
  assert.equal(f.$('review-queue-more').textContent, '11 more recent matches waiting');
});

test('latest pending match has a direct review link and safely rendered matchup text', () => {
  const f = fixture(), championName = '<img src=x onerror=alert(1)>';
  f.hooks.render(snapshot([{ ...pendingMatch, gameId: '42&other=7', championName }, { ...pendingMatch, gameId: 41 }]));
  const row = f.$('unreviewed').children[0], open = row.querySelector('.home-review-open');
  assert.equal(open.tagName, 'A');
  assert.equal(open.href, 'review.html?gameId=42%26other%3D7');
  assert.equal(open.getAttribute('aria-label'), `Review ${championName} vs Kindred`);
  assert.equal(row.querySelector('.vline').textContent, `${championName} vs Kindred`);
  assert.equal(row.querySelector('.home-review-result').textContent, 'Defeat');
  assert.equal(row.querySelector('.home-review-skip').dataset.gameId, '42&other=7');
});

test('skipping the final pending match posts its id and refreshes the count, banner and empty state', async () => {
  let current = snapshot([pendingMatch]);
  const f = fixture(async () => current);
  await f.hooks.loadDashboard();
  assert.equal(f.$('unreviewed-label').textContent, '1 match ready to review');
  assert.equal(f.$('review-queue-footer').hidden, true);
  const skip = f.$('unreviewed').children[0].querySelector('.home-review-skip');
  current = snapshot([]);
  const event = await f.emit('click', skip);
  assert.equal(event.defaultPrevented, true);
  assert.deepEqual(f.writes, [{ command: 'skip_review', args: { payload: { gameId: 42 } } }]);
  assert.equal(f.requests.length, 2, 'Skip loads a fresh server snapshot');
  assert.equal(f.$('review-inbox').hidden, true);
  assert.equal(f.$('unreviewed-empty').hidden, false);
  assert.equal(f.$('unreviewed').children.length, 0);
  assert.equal(f.$('review-queue-footer').hidden, true);
  assert.equal(f.page.classList.contains('has-pending-review'), false);
  assert.equal(f.scope.location.href, 'dashboard.html', 'Skip must not also open the match');
  assert.equal(skip.disabled, false);
});

test('pending reviews preserve active and carried-over session actions and their original block dates', () => {
  const f = fixture();
  for (const carriedOver of [false, true]) {
    const blockDate = carriedOver ? '2026-09-12' : '2026-09-13';
    f.hooks.render(snapshot([pendingMatch], 1, {
      sessionIntention: 'Check the map before a trade', debriefRating: null,
      carriedOver, blockDate, withCoach: true, stintBlockNumber: 2,
    }));
    assert.equal(f.$('review-inbox').hidden, false);
    assert.equal(f.$('nextstep-cta').textContent, 'Finish session');
    assert.equal(f.$('nextstep-cta').dataset.action, 'end_block');
    assert.equal(f.$('nextstep-cta').dataset.blockDate, blockDate);
    assert.equal(f.$('nextstep-k').textContent, `${carriedOver ? 'Previous session' : 'Session in progress'} · Block #2 · With coach`);
  }
  f.hooks.render(snapshot([pendingMatch], 1, { sessionIntention: 'Done', debriefRating: 8 }));
  assert.equal(f.$('nextstep-cta').dataset.action, 'start_block');
  assert.equal(f.$('nextstep-cta').dataset.blockDate, undefined, 'Completing a session removes its old write target');
  assert.equal(f.$('review-inbox').hidden, false);
});
