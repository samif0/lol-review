import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Exercise the real page renderer and write handlers; Electron covers native
// details keyboard behavior and visual layout with the production templates.
const source = (await readFile(new URL('../ui/matchups.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');
const plain = value => JSON.parse(JSON.stringify(value));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const dataKey = name => name.replace(/^data-/, '').replace(/-([a-z])/g, (_, char) => char.toUpperCase());

function fixture({ invoke, snapshot } = {}) {
  let document;
  class Element {
    constructor(tag = 'div', classes = '', dataset = {}) {
      this.tagName = tag.toUpperCase(); this.dataset = { ...dataset }; this.classes = new Set(classes.split(' ').filter(Boolean));
      this.children = []; this.parentElement = null; this.listeners = new Map(); this.attributes = new Map();
      this.hidden = false; this.open = false; this.value = ''; this.id = ''; this.textContent = ''; this.style = {};
      this.disabled = false; this.detachments = 0;
      this.classList = {
        add: (...names) => names.forEach(name => this.classes.add(name)),
        remove: (...names) => names.forEach(name => this.classes.delete(name)),
        contains: name => this.classes.has(name),
        toggle: (name, force) => {
          const next = force ?? !this.classes.has(name);
          if (next) this.classes.add(name); else this.classes.delete(name);
          return next;
        },
      };
    }
    get firstElementChild() { return this.children[0] || null; }
    get nextElementSibling() { return this.parentElement?.children[this.parentElement.children.indexOf(this) + 1] || null; }
    get scrollHeight() {
      for (let el = this; el; el = el.parentElement) if (el.hidden || (el.tagName === 'DETAILS' && !el.open)) return 0;
      return 64;
    }
    matches(selector) {
      const tag = selector.match(/^[a-z]+/i)?.[0];
      if (tag && this.tagName !== tag.toUpperCase()) return false;
      for (const [, name] of selector.matchAll(/\.([\w-]+)/g)) if (!this.classes.has(name)) return false;
      const id = selector.match(/#([\w-]+)/)?.[1];
      if (id && this.id !== id) return false;
      for (const [, name, value] of selector.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)) {
        const actual = name.startsWith('data-') ? this.dataset[dataKey(name)] : name === 'open' ? (this.open ? '' : undefined) : this.attributes.get(name);
        if (actual === undefined || (value !== undefined && String(actual) !== value)) return false;
      }
      return true;
    }
    querySelectorAll(selector) {
      const parts = selector.split(/\s+/);
      const matches = element => {
        if (!element.matches(parts.at(-1))) return false;
        let parent = element.parentElement;
        for (let index = parts.length - 2; index >= 0; index--) {
          while (parent && !parent.matches(parts[index])) parent = parent.parentElement;
          if (!parent) return false;
          parent = parent.parentElement;
        }
        return true;
      };
      return this.children.flatMap(child => [child, ...child.descendants()]).filter(matches);
    }
    descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    closest(selector) { for (let el = this; el; el = el.parentElement) if (el.matches(selector)) return el; return null; }
    appendChild(child) { return this.insertBefore(child, null); }
    insertBefore(child, next) {
      if (child === next) return child;
      child.remove();
      const index = next ? this.children.indexOf(next) : this.children.length;
      this.children.splice(index, 0, child); child.parentElement = this; return child;
    }
    remove() {
      if (!this.parentElement) return;
      const siblings = this.parentElement.children;
      siblings.splice(siblings.indexOf(this), 1); this.parentElement = null; this.detachments++;
    }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    addEventListener(type, fn) {
      if (!this.listeners.has(type)) this.listeners.set(type, []);
      this.listeners.get(type).push(fn);
    }
    async emit(type, event = {}) { for (const fn of this.listeners.get(type) || []) await fn(event); }
    focus() { document.activeElement = this; }
    scrollIntoView(options) { this.scrolled = options; }
  }
  const body = new Element('body'), ids = new Map(), timers = new Map();
  const $ = id => {
    if (!ids.has(id)) { const el = new Element(); el.id = id; ids.set(id, el); body.appendChild(el); }
    return ids.get(id);
  };
  const child = (parent, tag, classes, dataset) => parent.appendChild(new Element(tag, classes, dataset));
  const template = name => {
    if (name === 'tpl-lane') {
      const el = new Element('div', 'mj-lane', { lane: '' });
      child(el, 'span', 'mj-lane-name'); child(el, 'span', 'mj-lane-n'); child(el, 'div', 'mj-lane-groups'); return el;
    }
    if (name === 'tpl-group') {
      const el = new Element('details', 'mj-group', { groupKey: '' }), summary = child(el, 'summary', 'mj-group-head');
      for (const cls of ['mj-group-title', 'mj-group-n', 'mj-group-date']) child(summary, 'span', cls);
      child(el, 'div', 'mj-group-cards'); return el;
    }
    const el = new Element('div', 'mj-card', { cardId: '' }), context = child(el, 'div', 'mj-card-context');
    child(context, 'span', 'mj-card-date'); child(context, 'span', 'mj-card-game');
    for (const field of ['prior', 'observed']) {
      const note = child(el, 'label', 'mj-note'); child(note, 'span', 'mj-saved').hidden = true;
      child(note, 'textarea', 'mj-note-in', { field });
    }
    child(el, 'div', 'mj-card-err').hidden = true; return el;
  };
  document = new Element('document'); document.children = [body]; body.parentElement = document;
  document.readyState = 'loading'; document.activeElement = null;
  child($('statusline'), 'b'); $('mj-form').hidden = true; $('mj-lane-filter').value = 'all'; $('f-lane').value = 'top';
  let timerId = 0;
  const calls = [];
  const context = vm.createContext({
    $, show: (el, visible) => { if (el) el.hidden = !visible; }, tpl: template,
    document, window: { addEventListener() {}, confirm: () => true },
    getInvoke: async () => invoke ? async (...args) => { calls.push(plain(args)); return invoke(...args); } : null,
    getListen: async () => null, readSnapshot: async () => snapshot ? snapshot() : sample(),
    setTimeout: fn => { timers.set(++timerId, fn); return timerId; }, clearTimeout: id => timers.delete(id),
    console: { info() {}, warn() {}, error() {} },
  });
  vm.runInContext(`${source}\n globalThis.hooks = { render(data) { _lastData = data; render(data); },
    applyFilters, clearFilters, openGroup, scrollToCard, saveNote, flushPendingNotes, loadMatchups,
    submitForm, openEditForm, fromLastGame, cardElById, get notes() { return _notes; } };`, context, { filename: 'matchups.js' });
  return { $, document, hooks: context.hooks, calls,
    groups: () => document.querySelectorAll('.mj-group'),
    notes: id => context.hooks.cardElById(id).querySelectorAll('.mj-note-in'),
    async action(action, cardId) {
      const button = new Element('button', '', { action });
      if (cardId) context.hooks.cardElById(cardId).appendChild(button);
      await document.emit('click', { target: button, preventDefault() {} });
    },
  };
}

function sample() {
  const card = (id, prior, observed, createdAt, date, extra = {}) => ({
    id, prior, observed, createdAt, createdAtText: date, dateText: date, ...extra,
  });
  return { totalCount: 4, isEmpty: false, lastGame: { available: true, existingCardId: 3 }, lanes: [
    { lane: 'top', laneLabel: 'Top', groups: [
      { key: 'top|aatrox|fiora', title: 'Aatrox vs Fiora', allyChamps: ['Aatrox'], enemyChamps: ['Fiora'], cards: [
        card(1, 'Wait for parry', '', 200, 'Sep 6, 2026', { hasGame: true, gameLabel: 'Sep 6, 2026 · Loss' }),
        card(2, 'Freeze the wave', 'Parry bait worked', 100, 'Sep 1, 2026'),
      ] },
      { key: 'top|gnar|darius', title: 'Gnar vs Darius', cards: [card(3, 'Kite in mini form', 'Avoid the pull', 150, 'Sep 3, 2026')] },
    ] },
    { lane: 'bot', laneLabel: 'Bot', groups: [
      { key: 'bot|kaisa|vayne', title: "Kai'Sa vs Vayne", allyChamps: ["Kai'Sa"], enemyChamps: ['Vayne'],
        cards: [card(4, 'Play for level two', 'Held support cooldown', 175, 'Sep 5, 2026')] },
    ] },
  ] };
}

test('initial matchup list is collapsed, has meaningful newest dates and avoids duplicate linked dates', () => {
  const f = fixture(); f.hooks.render(sample());
  assert.deepEqual(f.groups().map(group => group.open), [false, false, false]);
  assert.equal(f.$('mj-results').textContent, '3 matchups');
  assert.equal(f.groups()[0].querySelector('.mj-group-date').textContent, 'Latest Sep 6, 2026');
  assert.equal(f.hooks.cardElById(1).querySelector('.mj-card-date').hidden, true);
  assert.equal(f.hooks.cardElById(1).querySelector('.mj-card-game').textContent, 'Sep 6, 2026 · Loss');
  assert.equal(f.hooks.cardElById(2).querySelector('.mj-card-date').hidden, false);
});

test('opening, collapsing and filtering keep the same raw editor nodes and one group open', async () => {
  const f = fixture(); f.hooks.render(sample());
  const [first, second] = f.groups(), note = f.notes(1)[1], card = f.hooks.cardElById(1);
  first.open = true; await first.emit('toggle');
  assert.equal(note.style.height, '64px', 'opening sizes the formerly hidden textarea');
  note.value = '  My unfinished reflection\n';
  const detached = card.detachments;
  second.open = true; await second.emit('toggle');
  assert.equal(first.open, false); assert.equal(second.open, true);
  f.$('mj-search').value = 'gnar'; f.hooks.applyFilters();
  assert.equal(first.hidden, true);
  f.hooks.clearFilters(); first.open = true; await first.emit('toggle');
  assert.equal(second.open, false);
  assert.equal(f.notes(1)[1], note); assert.equal(note.value, '  My unfinished reflection\n');
  assert.equal(card.detachments, detached);
});

test('search combines every champion, lane and live-note token with the selected lane', () => {
  const f = fixture(); f.hooks.render(sample());
  f.notes(1)[1].value = '  New dragon lesson  ';
  for (const [query, lane, expected] of [
    ['AATROX top dragon', 'all', ['top|aatrox|fiora']],
    ['parry worked', 'top', ['top|aatrox|fiora']],
    ['kaisa support', 'bot', ['bot|kaisa|vayne']],
    ['aatrox missing-token', 'all', []], ['aatrox', 'bot', []],
  ]) {
    f.$('mj-search').value = query; f.$('mj-lane-filter').value = lane; f.hooks.applyFilters();
    assert.deepEqual(f.groups().filter(group => !group.hidden).map(group => group.dataset.groupKey), expected);
    assert.equal(f.$('mj-results').textContent, `${expected.length} matchup${expected.length === 1 ? '' : 's'}`);
    assert.equal(f.$('mj-no-results').hidden, expected.length > 0);
    for (const section of f.document.querySelectorAll('.mj-lane')) {
      assert.equal(section.hidden, section.querySelectorAll('.mj-group').every(group => group.hidden));
    }
  }
});

test('clear filters retains expansion and places keyboard focus in search', async () => {
  const f = fixture(); f.hooks.render(sample()); f.hooks.openGroup(f.groups()[0]);
  f.$('mj-search').value = 'nothing matches'; f.hooks.applyFilters();
  await f.action('clear_filters');
  assert.equal(f.$('mj-search').value, ''); assert.equal(f.$('mj-lane-filter').value, 'all');
  assert.equal(f.groups()[0].open, true); assert.equal(f.document.activeElement, f.$('mj-search'));
});

test('last-match navigation reveals a filtered card, collapses others and focuses its summary', async () => {
  const f = fixture(); f.hooks.render(sample()); f.hooks.openGroup(f.groups()[0]);
  f.$('mj-search').value = 'aatrox'; f.$('mj-lane-filter').value = 'bot'; f.hooks.applyFilters();
  await f.hooks.fromLastGame(f.$('from-last'));
  assert.equal(f.$('mj-search').value, ''); assert.equal(f.$('mj-lane-filter').value, 'all');
  assert.deepEqual(f.groups().map(group => group.open), [false, true, false]);
  assert.equal(f.document.activeElement, f.groups()[1].querySelector('summary'));
  assert.equal(f.hooks.cardElById(3).scrolled.block, 'center');
  assert.equal(f.hooks.scrollToCard(4, { focusPrior: true }), true);
  assert.equal(f.document.activeElement, f.notes(4)[0]);
  assert.equal(f.notes(4)[0].style.height, '64px');
});

test('refresh preserves filters, expansion, failed raw drafts and mounted cards while updating saved values', async () => {
  const f = fixture({ invoke: () => { throw new Error('Offline'); } }); f.hooks.render(sample());
  const card = f.hooks.cardElById(1), [prior, observed] = f.notes(1);
  observed.value = '  Unsaved after failure  '; await f.hooks.saveNote(observed);
  f.hooks.openGroup(f.groups()[0]); f.$('mj-search').value = 'aatrox'; f.$('mj-lane-filter').value = 'top';
  const fresh = sample(); fresh.lanes[0].groups[0].cards[0].prior = 'New confirmed plan';
  f.hooks.render(fresh);
  assert.equal(f.hooks.cardElById(1), card); assert.equal(f.notes(1)[1], observed);
  assert.equal(observed.value, '  Unsaved after failure  '); assert.equal(prior.value, 'New confirmed plan');
  assert.equal(card.querySelector('.mj-card-err').textContent, 'Offline');
  assert.equal(f.groups()[0].open, true); assert.equal(f.$('mj-search').value, 'aatrox');
  assert.equal(f.$('mj-lane-filter').value, 'top'); assert.equal(f.groups()[1].hidden, true);
});

test('refresh waits through a pending note save and its newer edit without duplicate writes', async () => {
  const first = deferred(), second = deferred(), firstStarted = deferred(), secondStarted = deferred();
  let snapshots = 0, saved = '';
  const f = fixture({
    invoke: async (_, { payload }) => {
      if (payload.observed === 'First answer') { firstStarted.resolve(); await first.promise; }
      else { secondStarted.resolve(); await second.promise; }
      saved = payload.observed;
    },
    snapshot: () => { snapshots++; const data = sample(); data.lanes[0].groups[0].cards[0].observed = saved; return data; },
  });
  f.hooks.render(sample()); const note = f.notes(1)[1]; note.value = 'First answer';
  const saving = f.hooks.saveNote(note); await firstStarted.promise;
  note.value = 'Latest answer'; const duplicate = f.hooks.saveNote(note);
  const loading = f.hooks.loadMatchups(); first.resolve(); await secondStarted.promise;
  assert.equal(snapshots, 0); assert.equal(duplicate, saving);
  second.resolve(); await Promise.all([saving, loading]);
  assert.equal(snapshots, 1); assert.equal(f.notes(1)[1], note); assert.equal(note.value, 'Latest answer');
  assert.deepEqual(f.calls.map(([, { payload }]) => payload.observed), ['First answer', 'Latest answer']);
});

test('a fast note save during an older delayed fetch forces a fresh read before rendering', async () => {
  const response = deferred(), fetchStarted = deferred();
  let snapshots = 0, saved = '';
  const f = fixture({
    invoke: async (_, { payload }) => { saved = payload.observed; },
    snapshot: () => {
      if (++snapshots === 1) { fetchStarted.resolve(); return response.promise; }
      const fresh = sample(); fresh.lanes[0].groups[0].cards[0].observed = saved; return fresh;
    },
  });
  f.hooks.render(sample()); const note = f.notes(1)[1];
  const loading = f.hooks.loadMatchups(); await fetchStarted.promise;
  note.value = '  Confirmed while fetching  ';
  await f.hooks.saveNote(note);
  response.resolve(sample()); await loading;
  assert.equal(snapshots, 2, 'a response older than the completed save must not be rendered');
  assert.equal(f.notes(1)[1], note);
  assert.equal(note.value, '  Confirmed while fetching  ');
  assert.equal(f.hooks.notes.get(1).observed, '  Confirmed while fetching  ');
});

test('updating a note reveals its regrouped card and does not restore the old inline text', async () => {
  let fresh = sample();
  const f = fixture({
    invoke: async (command, { payload }) => {
      assert.equal(command, 'update_matchup');
      fresh = sample(); const updated = fresh.lanes[0].groups[0].cards.shift();
      Object.assign(updated, payload); fresh.lanes[1].groups[0].cards.push(updated);
      return { ok: true };
    }, snapshot: () => fresh,
  });
  f.hooks.render(sample()); f.hooks.openEditForm(1);
  f.$('f-lane').value = 'bot'; f.$('f-ally1').value = "Kai'Sa"; f.$('f-enemy1').value = 'Vayne';
  f.$('f-prior').value = 'Updated plan'; f.$('f-observed').value = 'Updated reflection';
  f.$('mj-search').value = 'aatrox'; f.$('mj-lane-filter').value = 'top'; f.hooks.applyFilters();
  await f.hooks.submitForm(f.$('form-submit'));
  assert.equal(f.notes(1)[0].value, 'Updated plan'); assert.equal(f.notes(1)[1].value, 'Updated reflection');
  assert.equal(f.hooks.cardElById(1).closest('.mj-group').open, true);
  assert.equal(f.hooks.cardElById(1).closest('.mj-lane').dataset.lane, 'bot');
  assert.equal(f.$('mj-search').value, ''); assert.equal(f.$('mj-form').hidden, true);
});
