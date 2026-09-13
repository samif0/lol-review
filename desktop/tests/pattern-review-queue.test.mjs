import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = (await readFile(new URL('../ui/patterns.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');

function fixture(reply = async () => ({ ok: true })) {
  const ids = new Map(), writes = [], reads = [], listeners = new Map();
  let serverSnapshot = null;
  const clone = value => JSON.parse(JSON.stringify(value));
  function element() {
    const classes = new Set(), attributes = new Map();
    return {
      children: [], selectors: new Map(), dataset: {}, textContent: '', value: '', hidden: false, disabled: false,
      get firstChild() { return this.children[0] || null; },
      set innerHTML(_) { throw new Error('Pattern content must remain plain text'); },
      style: { setProperty() {} },
      classList: {
        contains: name => classes.has(name),
        add: (...names) => names.forEach(name => classes.add(name)),
        remove: (...names) => names.forEach(name => classes.delete(name)),
        toggle(name, force) {
          const active = force ?? !classes.has(name);
          if (active) classes.add(name); else classes.delete(name);
          return active;
        },
      },
      appendChild(child) { this.children.push(child); child.parent = this; return child; },
      querySelector(selector) { return this.selectors.get(selector) || null; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      focus() { this.focused = true; },
      closest() { return null; },
      scrollIntoView() {},
      addEventListener() {},
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  const document = {
    readyState: 'loading',
    querySelector: selector => $(selector),
    addEventListener(type, listener) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(listener);
    },
  };
  const context = vm.createContext({ document, window: { addEventListener() {} }, $, setTimeout, clearTimeout,
    console: { error() {} }, matchMedia: () => ({ matches: true }),
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    tpl(name) {
      const node = element();
      const selectors = name === 'tpl-patcard'
        ? ['.pat-sev', '.pat-card-title', '.pat-card-detail', '.pat-card-sub', '.pat-card-state', '.gamerow-cue']
        : ['.pat-rg', '.pat-rclip', '.pat-rpol', '.pat-rtitle', '.pat-rnote', '.pat-moment-number'];
      for (const selector of selectors) {
        const child = element();
        if (selector === '.gamerow-cue') child.appendChild(element());
        node.selectors.set(selector, child); node.appendChild(child);
      }
      return node;
    },
    readSnapshot: async (command, sample) => {
      reads.push({ command, sample });
      assert.ok(serverSnapshot, 'Only the synthetic snapshot seeded by this test may be fetched');
      return clone({ ...serverSnapshot, patterns: serverSnapshot.patterns.filter(p => !p.isReviewed) });
    },
    getInvoke: async () => async (command, args) => {
      writes.push({ command, args: clone(args) });
      const result = await reply(command, args);
      if (result?.ok !== false && command === 'mark_pattern_reviewed') {
        const saved = serverSnapshot.patterns.find(p => p.patternKey === args.payload.patternKey);
        if (saved && !saved.isReviewed) {
          saved.isReviewed = true;
          serverSnapshot.pendingCount -= 1;
          serverSnapshot.reviewedPatternCount += 1;
        }
      }
      return result;
    },
  });
  vm.runInContext(`${source}\nglobalThis.hooks = { render, markReviewed, selectPattern, activePattern };`, context);
  const hooks = { ...context.hooks, render(data) {
    serverSnapshot = clone(data);
    return context.hooks.render(data);
  } };
  const cardTitles = () => $('pat-pick').children.map(card => card.querySelector('.pat-card-title').textContent);
  return { $, hooks, writes, reads, cardTitles };
}

function pattern(key, isReviewed = false) {
  return {
    patternKey: key, kind: 'objective', title: `Pattern ${key}`, isReviewed,
    severity: 'medium', moments: [{ evidenceId: `evidence-${key}`, gameId: key, title: `Moment ${key}`,
      sourceKind: 'bookmark', note: '', hasNote: false, polarity: 'good' }],
  };
}

const snapshot = patterns => ({ patterns, pendingCount: patterns.filter(p => !p.isReviewed).length,
  reviewedPatternCount: patterns.filter(p => p.isReviewed).length, emptyText: 'No patterns yet' });

test('reviewed patterns stay out of the picker when loading a mixed server snapshot', () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(1, true), pattern(2), pattern(3, true), pattern(4)]));
  assert.deepEqual(f.cardTitles(), ['Pattern 2', 'Pattern 4']);
  assert.equal(f.hooks.activePattern().patternKey, 2);
  assert.equal(f.$('pat-main').hidden, false);
  assert.equal(f.$('pat-review-title').textContent, 'Pattern 2');
});

test('finishing a pattern removes its card and opens the next pending review', async () => {
  const f = fixture(), completed = pattern(1);
  f.hooks.render(snapshot([completed, pattern(2)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes, [{ command: 'mark_pattern_reviewed', args: {
    payload: { patternKey: 1, kind: 'objective', momentCount: 1 },
  } }]);
  assert.deepEqual(f.cardTitles(), ['Pattern 2']);
  assert.equal(f.hooks.activePattern().patternKey, 2);
  assert.equal(f.$('pat-review-title').textContent, 'Pattern 2');
  assert.equal(f.$('pat-markrev').disabled, false, 'The next review remains actionable');
  assert.equal(completed.moments.length, 1, 'Finishing a review does not delete its saved moments');
  assert.equal(f.reads.length, 1, 'A successful finish refreshes the queue and authoritative counts');
});

test('finishing the final pattern shows a caught-up state and hides its player', async () => {
  const f = fixture();
  f.hooks.render(snapshot([pattern(1)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), []);
  assert.equal(f.hooks.activePattern(), null);
  assert.equal(f.$('pat-main').hidden, true);
  assert.equal(f.$('pat-empty').hidden, false);
  assert.match(f.$('pat-empty-h').textContent, /caught up|reviewed/i);
  assert.doesNotMatch(f.$('pat-empty-h').textContent, /no patterns yet/i,
    'Completed work should not look like the user has never had patterns');
});

test('a failed finish keeps the pattern visible and allows retry', async () => {
  let fail = true;
  const f = fixture(async () => {
    if (fail) throw new Error('Synthetic network failure');
    return { ok: true };
  });
  f.hooks.render(snapshot([pattern(1)]));
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.hooks.activePattern().isReviewed, false);
  assert.equal(f.$('pat-main').hidden, false);
  assert.equal(f.$('pat-markrev').disabled, false);
  assert.equal(f.$('pat-review-status').hidden, false);
  assert.equal(f.reads.length, 0, 'Failed saves must not refresh away the current review');
  fail = false;
  await f.hooks.markReviewed();
  assert.deepEqual(f.cardTitles(), []);
  assert.equal(f.writes.length, 2);
});

test('repeated finish activation writes once while the review is being saved', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const f = fixture(async () => pending);
  f.hooks.render(snapshot([pattern(1), pattern(2)]));
  const first = f.hooks.markReviewed();
  const duplicate = f.hooks.markReviewed();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.writes.length, 1);
  assert.equal(f.$('pat-markrev').disabled, true);
  assert.equal(f.$('m-note').readOnly, true, 'The finishing note cannot acquire an unsaved edit');
  f.hooks.selectPattern(1);
  assert.equal(f.hooks.activePattern().patternKey, 1, 'The active write keeps its selected pattern');
  release({ ok: true });
  await Promise.all([first, duplicate]);
  assert.deepEqual(f.cardTitles(), ['Pattern 2']);
  assert.equal(f.writes.length, 1, 'The duplicate must not finish the next pattern');
  assert.equal(f.$('m-note').readOnly, false);
});

test('a pending note must save successfully before its pattern can disappear', async () => {
  let failNote = true;
  const f = fixture(async command => {
    if (command === 'save_pattern_moment_note' && failNote) throw new Error('Synthetic note write failure');
    return { ok: true };
  });
  f.hooks.render(snapshot([pattern(1)]));
  f.$('m-note').value = 'Remember to check the map before trading.';
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes.map(write => write.command), ['save_pattern_moment_note']);
  assert.deepEqual(f.cardTitles(), ['Pattern 1']);
  assert.equal(f.$('m-note').value, 'Remember to check the map before trading.');
  assert.equal(f.$('pat-markrev').disabled, false);
  failNote = false;
  await f.hooks.markReviewed();
  assert.deepEqual(f.writes.map(write => write.command), [
    'save_pattern_moment_note', 'save_pattern_moment_note', 'mark_pattern_reviewed',
  ]);
  assert.deepEqual(f.cardTitles(), []);
});
