import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { relativeDay } from '../ui/journal-takeaway.mjs';

// Home's real renderer and action handler: the greeting, the composer (Start session,
// Finish session, the coach tag and the objective pill), the stint exit, the last 30
// days and the load error path.
const source = (await readFile(new URL('../ui/app.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');
const html = await readFile(new URL('../ui/dashboard.html', import.meta.url), 'utf8');

function fixture(reply, { confirm = () => true, search = '' } = {}) {
  const ids = new Map(), listeners = new Map(), requests = [], writes = [], questions = [], events = [], queries = new Map();
  let focused = null;
  function element(tagName = 'DIV') {
    const names = new Set(), attributes = new Map();
    return {
      tagName, children: [], selectors: new Map(), dataset: {}, textContent: '', hidden: false, value: '', disabled: false,
      get lastChild() { return this.children.at(-1) || null; },
      set innerHTML(_) { throw new Error('Dashboard data must remain plain text'); },
      classList: {
        contains: name => names.has(name), add: (...values) => values.forEach(value => names.add(value)),
        remove: (...values) => values.forEach(value => names.delete(value)),
        toggle(name, force) { const active = force ?? !names.has(name); if (active) names.add(name); else names.delete(name); return active; },
      },
      appendChild(child) { this.children.push(child); child.parent = this; },
      removeChild(child) { this.children.splice(this.children.indexOf(child), 1); },
      querySelector(selector) { return this.selectors.get(selector) || null; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      addEventListener() {},
      focus() { focused = this; },
      closest(selector) {
        if (selector === 'button' && this.tagName === 'BUTTON') return this;
        if (selector === '[data-action]' && this.dataset.action) return this;
        return this.parent?.closest(selector) || null;
      },
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element(id === 'nextstep-input' ? 'INPUT' : 'DIV')); return ids.get(id); };
  $('statusline').appendChild($('status-text'));
  const end = element('BUTTON'); end.dataset.action = 'end_stint'; ids.set('nextstep-stint-end', end);
  $('nextstep-stint').appendChild($('nextstep-stint-text')); $('nextstep-stint').appendChild(end);
  const page = element();
  const document = {
    readyState: 'loading',
    querySelector: selector => selector === '#statusline b' ? $('status-text') : selector === '.home-page' ? page : queries.get(selector) ?? null,
    querySelectorAll: selector => queries.get(`all:${selector}`) ?? [],
    createElement: tag => element(tag.toUpperCase()),
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
  };
  const scope = { location: { href: 'dashboard.html', search }, addEventListener() {},
    dispatchEvent(event) { events.push(event); return true; },
    confirm: message => { questions.push(message); return confirm(); } };
  class CustomEvent { constructor(type, init = {}) { this.type = type; this.detail = init.detail; } }
  const context = vm.createContext({ document, window: scope, $, URLSearchParams, encodeURIComponent, CustomEvent, relativeDay,
    console: { error() {}, info() {}, warn() {} },
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    tpl() { const node = element(); for (const s of ['.k', '.v', '.s']) node.selectors.set(s, element()); return node; },
    objectiveMetaText: () => '',
    registerLeaveGuard: () => ({ beforeNavigate: async () => true }),
    mountJournalTakeaway: () => null,
    mountMissedGames: async () => null,
    readSnapshot: async () => { requests.push(1); return reply(); },
    getInvoke: async () => async (command, args) => { writes.push({ command, args: JSON.parse(JSON.stringify(args)) }); },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, loadDashboard, submitIntent, toggleCoach, chooseObjective,
    openFinishEditor, submitFinish, hasActiveWork, composerDirty };`, context);
  return { $, page, scope, hooks: context.hooks, requests, writes, questions, events, queries, end, get focused() { return focused; },
    click: async target => { for (const handler of listeners.get('click') || []) await handler({ target, preventDefault() {} }); } };
}

const stint = { name: 'Violet', blocksTotal: 12, blocksWithCoach: 5, blocksSolo: 7 };
const running = { sessionIntention: 'Check the map before a trade', debriefRating: null, blockDate: '2026-09-12', carriedOver: true };

test('an old coaching stint shows on Home with a way to end it, and ending it refreshes Home', async () => {
  let active = true;
  const f = fixture(() => ({ stint: active ? stint : null }));
  await f.hooks.loadDashboard();
  assert.equal(f.$('nextstep-stint').hidden, false);
  assert.equal(f.$('nextstep-stint-text').textContent, 'Coaching stint · Violet · 12 blocks · 5 with coach · 7 solo');
  assert.match(html, /<button class="nextstep-stint-end" id="nextstep-stint-end" type="button" data-action="end_stint">End stint<\/button>/);

  active = false;
  await f.click(f.end);
  assert.equal(f.questions.length, 1);
  assert.doesNotMatch(f.questions[0], /—/);
  assert.deepEqual(f.writes, [{ command: 'end_stint', args: { payload: {} } }]);
  assert.equal(f.requests.length, 2, 'Home reloads after the stint ends');
  assert.equal(f.$('nextstep-stint').hidden, true);
});

test('declining the confirmation keeps the stint running', async () => {
  const f = fixture(() => ({ stint }), { confirm: () => false });
  await f.hooks.loadDashboard();
  await f.click(f.end);
  assert.deepEqual(f.writes, []);
  assert.equal(f.requests.length, 1);
});

test('a failed Home load shows its error at once instead of retrying for ten seconds', async () => {
  const f = fixture(() => { throw new Error('Failed to fetch'); });
  const started = Date.now();
  await f.hooks.loadDashboard();
  assert.ok(Date.now() - started < 1000);
  assert.equal(f.requests.length, 1);
  assert.equal(f.$('errpanel').hidden, false);
  assert.equal(f.$('err-detail').textContent, 'Failed to fetch');
});

test('the greeting reads as a salutation, and an unlinked account becomes a link under it', () => {
  const f = fixture(() => ({}));
  for (const [greeting, shown, unlinked] of [
    ['Good evening, bye.', 'Good evening, bye', false],
    ['Good morning. Lock in.', 'Good morning', false],
    ['Good afternoon. Link your Riot account.', 'Good afternoon', true],
  ]) {
    f.hooks.render({ greeting });
    assert.equal(f.$('hero-title').textContent, shown);
    assert.equal(f.$('hero-account').hidden, !unlinked, greeting);
  }
  assert.match(html, /<h1 class="ui-display home-greet"><svg class="home-mark" data-theme-hide="neutral"[^>]*>[\s\S]*?<span id="hero-title">/);
  assert.match(html, /<div class="home-aura" data-theme-only="aura" aria-hidden="true"><\/div>/);
  // Every icon carries its own size, so a page whose stylesheet is late or missing never
  // draws the mark as a full-width glyph.
  const icons = html.match(/<svg\b[^>]*>/g);
  assert.ok(icons.length >= 5);
  for (const icon of icons) assert.match(icon, /\swidth="\d+" height="\d+"/, icon);
});

test('the last 30 days come from the baseline strings, and the adherence streak stays gold', () => {
  const f = fixture(() => ({}));
  f.hooks.render({ stats: { totalGames: 3, winRateSub: '2W // 1L  ·  30-day 56%', avgMentalSub: '30-day avg 7,3', adherenceStreak: 53, reviewedPatternCount: 2 } });
  const cells = f.$('strip').children;
  assert.deepEqual(cells.map(c => [c.querySelector('.v').textContent, c.querySelector('.s').textContent, c.querySelector('.k').textContent]), [
    ['56', '%', 'Win rate'], ['7,3', '', 'Average mental'], ['2', '', 'Patterns reviewed, all time'], ['53', 'days', 'Rules kept']]);
  assert.equal(cells[3].classList.contains('is-streak'), true, 'the streak is a retention mechanic and keeps its gold');
  assert.equal(f.$('status-text').textContent, '3 games today.');

  f.hooks.render({ stats: { winRateSub: '1W // 0L', avgMentalSub: '', adherenceStreak: 1 } });
  const empty = f.$('strip').children;
  assert.deepEqual(empty.map(c => c.querySelector('.v').textContent), ['Not yet', 'Not yet', '0', '1']);
  assert.equal(empty[0].classList.contains('is-empty'), true);
  assert.equal(empty[3].querySelector('.s').textContent, 'day');
});

test('the packaged smoke waits for exactly the stat cells Home renders', async () => {
  const smoke = await readFile(new URL('../electron/smoke.mjs', import.meta.url), 'utf8');
  const literal = /export const HOME_STAT_LABELS = Object\.freeze\((\[[^\]]*\])\);/.exec(smoke);
  assert.ok(literal, 'electron/smoke.mjs names the Home stat labels it waits for');
  const expected = JSON.parse(literal[1].replace(/'/g, '"'));
  const f = fixture(() => ({}));
  f.hooks.render({ stats: {} });
  assert.deepEqual(f.$('strip').children.map(cell => cell.querySelector('.k').textContent), expected,
    'change electron/smoke.mjs HOME_STAT_LABELS with renderStats, or the packaged CI and release smoke times out');
  assert.match(smoke, /querySelectorAll\('#strip \.home-stat \.k'\)/);
  assert.match(smoke, /labels\.length === \$\{HOME_STAT_LABELS\.length\}/);
  assert.match(html, /<template id="tpl-stat">\s*<div class="home-stat">[\s\S]*?<span class="k"><\/span><\/div>/);
});

test('Start session sends the typed focus and coach tag, announces it, and reloads into the running session', async () => {
  let current = { intent: { withCoach: false } };
  const f = fixture(() => current);
  await f.hooks.loadDashboard();
  assert.equal(f.$('nextstep-cta').textContent, 'Start session');
  assert.equal(f.$('nextstep-cta').dataset.action, 'start_block');

  await f.hooks.submitIntent();
  assert.deepEqual(f.writes, [], 'an empty composer starts nothing');
  assert.equal(f.focused, f.$('nextstep-input'));

  f.$('nextstep-input').value = '  Check the map before a trade  ';
  f.hooks.toggleCoach();
  assert.equal(f.$('nextstep-coach').getAttribute('aria-checked'), 'true');
  assert.equal(f.$('nextstep-coach-text').textContent, 'With coach');
  current = { intent: { sessionIntention: 'Check the map before a trade', debriefRating: null, withCoach: true } };
  await f.click(f.$('nextstep-cta'));
  assert.deepEqual(f.writes, [{ command: 'start_block', args: { payload: { intention: 'Check the map before a trade', withCoach: true } } }]);
  assert.deepEqual(JSON.parse(JSON.stringify(f.events.map(e => [e.type, e.detail]))),
    [['revu:first-review-block-started', { intention: 'Check the map before a trade' }]]);
  assert.equal(f.requests.length, 2);
  assert.equal(f.$('nextstep-input').value, '');
  assert.equal(f.scope.__revuActiveWork, false);
  assert.equal(f.$('nextstep').classList.contains('is-active'), true);
  assert.equal(f.$('nextstep-h').textContent, 'Check the map before a trade');
  assert.equal(f.$('nextstep-cta').textContent, 'Finish session');
  assert.equal(f.focused, f.$('nextstep-cta'), 'focus stays in the composer once the field hides');
});

test('the composer is active work while it holds words, a changed coach tag or an open finish', () => {
  const f = fixture(() => ({}));
  f.hooks.render({ intent: { withCoach: true } });
  assert.equal(f.hooks.hasActiveWork(), false);
  assert.equal(f.$('nextstep-coach').getAttribute('aria-checked'), 'true', 'the day\'s coach tag is the starting point');
  f.hooks.toggleCoach();
  assert.equal(f.scope.__revuActiveWork, true);
  f.hooks.toggleCoach();
  assert.equal(f.scope.__revuActiveWork, false, 'back to the day\'s tag is no change');
  f.hooks.chooseObjective({ id: 7, title: 'Ward the river before crossing' });
  assert.equal(f.$('nextstep-input').value, 'Ward the river before crossing');
  assert.equal(f.$('nextstep-objective-label').textContent, 'Ward the river before crossing');
  assert.equal(f.scope.__revuActiveWork, true);
  assert.equal(f.hooks.composerDirty(), true);
  assert.deepEqual(f.writes, [], 'choosing an objective only fills in the intention; nothing is saved');
  f.hooks.render({ intent: { withCoach: true }, activeObjectives: [{ id: 9, title: 'Planning', isPriority: true }] });
  assert.equal(f.$('nextstep-input').value, 'Ward the river before crossing', 'a Home refresh keeps what the player typed');
});

test('Finish session asks for a rating, then sends it with the note and the session\'s own date', async () => {
  let current = { intent: running };
  const f = fixture(() => current);
  await f.hooks.loadDashboard();
  assert.equal(f.$('nextstep-k').textContent, 'Previous session');
  assert.match(f.$('nextstep-p').textContent, /^Finish your last session\./);
  await f.click(f.$('nextstep-cta'));
  assert.equal(f.$('nextstep-finish').hidden, false);
  assert.equal(f.scope.__revuActiveWork, true);
  await f.hooks.submitFinish();
  assert.equal(f.$('nextstep-rate-hint').textContent, 'Pick a rating from 1 to 10.');
  assert.deepEqual(f.writes, []);

  f.queries.set('#nextstep-rate input:checked', { value: '7' });
  f.$('nextstep-note').value = ' Stayed calm after the first death ';
  current = { intent: { ...running, debriefRating: 7 } };
  await f.hooks.submitFinish();
  assert.deepEqual(f.writes, [{ command: 'end_block', args: { payload: { rating: 7, note: 'Stayed calm after the first death', date: '2026-09-12' } } }]);
  assert.equal(f.$('nextstep-finish').hidden, true);
  assert.equal(f.scope.__revuActiveWork, false);
  assert.equal(f.$('nextstep-cta').dataset.action, 'start_block');
});

test('the sidebar\'s Start a session and ?intent=startblock focus the composer without reloading it', async () => {
  const f = fixture(() => ({}), { search: '?intent=startblock' });
  assert.equal(typeof f.scope.__revuStartSession, 'function');
  await f.hooks.loadDashboard();
  assert.equal(f.focused, f.$('nextstep-input'), 'the deep link lands in the composer');
  f.$('nextstep-input').value = 'Half typed';
  assert.equal(f.scope.__revuStartSession(), true);
  assert.equal(f.$('nextstep-input').value, 'Half typed', 'nothing typed is lost');
  assert.equal(f.requests.length, 1, 'no reload');
  f.hooks.render({ intent: running });
  f.scope.__revuStartSession();
  assert.equal(f.focused, f.$('nextstep-cta'), 'during a session it points at Finish session');

  // With the Finish editor open, Finish session is hidden, so the rating takes focus.
  const first = f.$('rate-1'), seventh = f.$('rate-7');
  f.queries.set('#nextstep-rate input', first);
  f.queries.set('all:#nextstep-rate input', [first, seventh]);
  f.hooks.openFinishEditor();
  assert.equal(f.$('nextstep').classList.contains('is-finishing'), true);
  f.$('nextstep-note').focus();
  f.scope.__revuStartSession();
  assert.equal(f.focused, first, 'no rating chosen: the first rating');
  f.queries.set('#nextstep-rate input:checked', seventh);
  f.scope.__revuStartSession();
  assert.equal(f.focused, seventh, 'a chosen rating keeps focus on that rating');
  f.queries.delete('#nextstep-rate input:checked'); f.queries.delete('#nextstep-rate input');
  f.scope.__revuStartSession();
  assert.equal(f.focused, f.$('nextstep-save'), 'without rating inputs: Save reflection');
  assert.notEqual(f.focused, f.$('nextstep-cta'), 'never the hidden Finish session button');

  const busy = fixture(() => ({ intent: running }), { search: '?intent=startblock' });
  await busy.hooks.loadDashboard();
  assert.equal(busy.focused, null, 'never while a session runs');
});

test('Home keeps the guards, ids and hooks other surfaces rely on', () => {
  for (const id of ['errpanel', 'err-line', 'err-detail', 'nextstep', 'nextstep-k', 'nextstep-cta', 'nextstep-stint', 'review-inbox',
    'unreviewed', 'unreviewed-empty', 'review-queue-footer', 'missed-games', 'missed-games-list', 'missed-games-label', 'missed-games-status']) {
    assert.match(html, new RegExp(`id="${id}"`), id);
  }
  assert.match(html, /<div class="home-orientation frt-launch">[\s\S]*?data-action="start_first_review_tutorial"/);
  assert.match(html, /<main class="shell home-page">/);
  assert.match(html, /class="ui-field intent-input home-composer-input" id="nextstep-input" type="text" maxlength="120"/);
  assert.match(html, /id="nextstep-note" type="text" maxlength="240"/);
  assert.match(html, /class="ui-pill intent-coach" id="nextstep-coach" type="button" role="switch" aria-checked="false"/);
  assert.match(source, /registerLeaveGuard\(\{ isDirty: composerDirty \}\)/);
  assert.doesNotMatch(html.replace(/<!--[\s\S]*?-->/g, ''), /[–—]/, 'no em or en dashes in Home copy');
});
