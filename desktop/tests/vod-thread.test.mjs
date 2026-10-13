import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';
import { buildThread, clock, moveNow, NOW_TOLERANCE, renderThread, threadCountText } from '../ui/vod-thread.mjs';

// A small DOM: enough for the thread's createElement, append, insertBefore and focus
// calls, and it counts every structural change so tests can prove moveNow is one move.
function fakeDocument() {
  const doc = { activeElement: null, mutations: [] };
  doc.createElement = tag => node(doc, tag);
  return doc;
}

function node(doc, tag) {
  const attributes = new Map(), listeners = [];
  const el = {
    tagName: tag.toUpperCase(), className: '', dataset: {}, children: [], parentNode: null, textContent: '', ownerDocument: doc,
    classList: {
      contains: name => el.className.split(/\s+/).includes(name),
      add: (...names) => { el.className = [...new Set([...el.className.split(/\s+/), ...names])].filter(Boolean).join(' '); },
    },
    setAttribute(name, value) { attributes.set(name, String(value)); },
    getAttribute: name => (attributes.has(name) ? attributes.get(name) : null),
    get firstChild() { return el.children[0] || null; },
    detach(child) {
      const parent = child.parentNode;
      if (!parent) return;
      parent.children = parent.children.filter(entry => entry !== child);
      child.parentNode = null;
    },
    appendChild(child) { el.detach(child); el.children.push(child); child.parentNode = el; doc.mutations.push(['append', child]); return child; },
    insertBefore(child, reference) {
      el.detach(child);
      const index = el.children.indexOf(reference);
      if (index < 0) el.children.push(child); else el.children.splice(index, 0, child);
      child.parentNode = el;
      doc.mutations.push(['insertBefore', child, reference]);
      return child;
    },
    removeChild(child) {
      el.detach(child);
      if (child.contains(doc.activeElement)) doc.activeElement = null;
      doc.mutations.push(['remove', child]);
      return child;
    },
    contains: other => other === el || el.children.some(child => child.contains(other)),
    matches(selector) {
      if (selector.startsWith('.')) return el.classList.contains(selector.slice(1));
      const data = selector.match(/^\[data-([\w-]+)\]$/);
      if (data) return data[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase()) in el.dataset;
      return el.tagName === selector.toUpperCase();
    },
    querySelectorAll: selector => el.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]),
    querySelector: selector => el.querySelectorAll(selector)[0] || null,
    closest: selector => (el.matches(selector) ? el : el.parentNode?.closest?.(selector) || null),
    focus(options) { doc.activeElement = el; el.focusOptions = options; },
    addEventListener(type, listener) { listeners.push({ type, listener }); },
    click() { for (const entry of listeners) if (entry.type === 'click') entry.listener({ type: 'click', target: el }); },
    // Visible text, the way textContent reads a real subtree.
    get text() { return el.children.length ? el.children.map(child => child.text).join(' ') : el.textContent; },
  };
  return el;
}

const kinds = items => items.map(item => item.kind);
const rowKinds = list => list.children.map(li => li.dataset.kind || 'empty');

const sample = () => ({
  events: [
    { t: 765, text: 'Death to Graves', key: 'key:a', tone: 'death' },
    { t: 1315, text: 'Lost teamfight, 2v3', key: 'key:b', tone: 'fight' },
    { t: 1322, text: 'Death to Syndra', key: 'key:c', tone: 'death' },
  ],
  notes: [
    { t: 323, text: 'Good trade at level 3', key: 'bm:1' },
    { t: 1360, text: 'Went straight back in after dying. Reset first.', key: 'bm:2' },
  ],
  clips: [{ t: 750, end: 775, text: 'Caught without vision', key: 'clip:9', narrated: true }],
  takeaway: "Respect Caitlyn's range until Nautilus can engage.",
  now: 755,
});

test('entries sort by game time and a tie reads event, then clip, then note, keeping input order', () => {
  const { items } = buildThread({
    events: [{ t: 60, text: 'First death', key: 'e1' }, { t: 60, text: 'Second death', key: 'e2' }, { t: 10, text: 'Kill', key: 'e0' }],
    notes: [{ t: 60, text: 'note a', key: 'n1' }, { t: 5, text: 'early note', key: 'n0' }, { t: 60, text: 'note b', key: 'n2' }],
    clips: [{ t: 60, text: 'clip a', key: 'c1' }],
    now: 0,
  });
  assert.deepEqual(items.map(item => item.key || item.kind), ['now', 'n0', 'e0', 'e1', 'e2', 'c1', 'n1', 'n2']);
});

test('the now item sits before the first entry later than now plus the stepper tolerance', () => {
  const events = [{ t: 100, text: 'A', key: 'a' }, { t: 200, text: 'B', key: 'b' }];
  const order = now => buildThread({ events, now }).items.map(item => item.key || 'now');
  assert.equal(NOW_TOLERANCE, 0.25);
  assert.deepEqual(order(0), ['now', 'a', 'b']);
  assert.deepEqual(order(99.75), ['a', 'now', 'b'], 'an entry within the tolerance counts as reached');
  assert.deepEqual(order(99.7), ['now', 'a', 'b']);
  assert.deepEqual(order(150), ['a', 'now', 'b']);
  assert.deepEqual(order(500), ['a', 'b', 'now']);
  assert.deepEqual(order(undefined), ['now', 'a', 'b'], 'an unknown time starts at the top');
  assert.deepEqual(order(Number.NaN), ['now', 'a', 'b']);
  const nowItem = buildThread({ events, now: -4 }).items[0];
  assert.deepEqual(nowItem, { kind: 'now', t: 0, text: 'You are here' });
});

test('the takeaway is always last with the label End, after the now item', () => {
  const { items } = buildThread(sample());
  assert.deepEqual(kinds(items), ['note', 'clip', 'now', 'event', 'event', 'event', 'note', 'takeaway']);
  assert.deepEqual(items.at(-1), { kind: 'takeaway', t: null, text: "Respect Caitlyn's range until Nautilus can engage.", label: 'End' });
  const late = buildThread({ ...sample(), now: 99999 }).items;
  assert.deepEqual(kinds(late).slice(-2), ['now', 'takeaway']);
  assert.equal(buildThread({ takeaway: { text: '  Ward river first  ' } }).items.at(-1).text, 'Ward river first');
  assert.deepEqual(kinds(buildThread({ takeaway: 'Only this' }).items), ['now', 'takeaway']);
  assert.equal(buildThread({ ...sample(), takeaway: '   ' }).items.some(item => item.kind === 'takeaway'), false);
});

test('an empty thread has no items, and bad rows are dropped instead of misplaced', () => {
  assert.deepEqual(buildThread(), { items: [], countText: 'No notes yet' });
  assert.deepEqual(buildThread({ events: null, notes: 'x', clips: [null, 3], takeaway: null, now: 30 }), { items: [], countText: 'No notes yet' });
  const { items } = buildThread({ notes: [{ t: 'soon', text: 'bad' }, { t: null, text: 'none' }, { seconds: 42, text: 'from seconds' }] });
  assert.deepEqual(items.map(item => item.text), ['You are here', 'from seconds']);
});

test('count text names notes and clips and leaves out zero parts', () => {
  assert.equal(buildThread(sample()).countText, '2 notes · 1 clip');
  assert.equal(threadCountText(1, 0), '1 note');
  assert.equal(threadCountText(0, 3), '3 clips');
  assert.equal(threadCountText(0, 0), 'No notes yet');
});

test('items carry tone, clip range, narration and whose words they are', () => {
  const { items } = buildThread({
    events: [{ t: 5, text: '', key: 'k', tone: 'not-a-tone' }, { t: 6, text: 'Kill', key: 'k2', tone: 'kill' }],
    clips: [{ t: 10, end: 8, text: 'Backwards' }, { t: 20, end: 45, text: 'Clip @ 0:20', own: false, narrated: 1 }],
    notes: [{ t: 30, text: '' }],
  });
  const [now, ev, ev2, clipA, clipB, note] = items;
  assert.equal(now.kind, 'now');
  assert.deepEqual(ev, { kind: 'event', t: 5, text: 'Game event', key: 'k', tone: 'neutral' });
  assert.equal(ev2.tone, 'kill');
  assert.equal('end' in clipA, false, 'a range that ends before it starts is dropped');
  assert.deepEqual(clipB, { kind: 'clip', t: 20, text: 'Clip @ 0:20', own: false, end: 45, narrated: true });
  assert.deepEqual(note, { kind: 'note', t: 30, text: '', own: false });
});

test('renderThread builds jump and review rows with the page actions and no write classes', () => {
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  const { items } = buildThread(sample());
  renderThread(list, items, { formatTime: clock, doc, selectedKey: 'key:b' });
  assert.deepEqual(rowKinds(list), ['note', 'clip', 'now', 'event', 'event', 'event', 'note', 'takeaway']);

  const events = list.querySelectorAll('.vp-th-event');
  const death = events[0].querySelector('.vp-th-row');
  assert.equal(death.tagName, 'BUTTON');
  assert.equal(death.type, 'button');
  assert.equal(death.dataset.action, 'review_event');
  assert.equal(death.dataset.reviewKey, 'key:a');
  assert.equal(death.getAttribute('aria-label'), 'Watch Death to Graves at 12:45');
  assert.equal(events[0].dataset.tone, 'death');
  assert.equal(events[0].querySelector('.own-words'), null, "Revu's event names are not the player's words");
  assert.equal(events[1].querySelector('.vp-th-row').classList.contains('is-on'), true);
  assert.equal(events[1].querySelector('.vp-th-row').getAttribute('aria-current'), 'true');
  assert.equal(death.classList.contains('is-on'), false);

  const [note, clip] = list.children;
  const noteRow = note.querySelector('.vp-th-row');
  assert.equal(noteRow.dataset.action, 'jump');
  assert.equal(noteRow.dataset.seconds, '323');
  assert.equal(note.dataset.tone, 'note');
  assert.equal(note.querySelector('.vp-th-kind').textContent, 'Note');
  assert.equal(note.querySelector('.vp-th-text').className, 'vp-th-text own-words');
  assert.equal(clip.querySelector('.vp-th-row').dataset.seconds, '750');
  assert.equal(clip.dataset.tone, 'clip');
  assert.equal(clip.querySelector('.vp-th-kind').textContent, 'Clip · 12:30 to 12:55 · Narrated');

  const now = list.children[2];
  assert.equal(now.classList.contains('vp-th-now'), true);
  assert.equal(now.getAttribute('aria-hidden'), 'true', 'the moving marker is not announced');
  assert.equal(now.querySelector('[data-action]'), null);
  assert.equal(now.querySelector('.vp-th-t').textContent, '12:35');
  assert.equal(now.querySelector('.vp-th-nowline').textContent, 'You are here');

  const take = list.children.at(-1);
  assert.equal(take.querySelector('.vp-th-row').tagName, 'DIV', 'the takeaway is read only');
  assert.equal(take.querySelector('.vp-th-t').textContent, 'End');
  assert.equal(take.querySelector('.vp-th-kind').textContent, 'Takeaway');
  assert.equal(take.querySelector('.vp-th-text').classList.contains('own-words'), true);

  const buttons = list.querySelectorAll('button');
  assert.equal(buttons.length, 6);
  assert.deepEqual(buttons.filter(button => !/(^|\s)ui-row(\s|$)/.test(button.className)), [],
    'each button carries a ui- class so the styles.css button reset skips it');
  for (const forbidden of ['.vp-share-btn', '.vp-bm-editnote', '.vp-ev-note', '[data-share-bm-id]', '[data-moment-key]']) {
    assert.deepEqual(list.querySelectorAll(forbidden), [], `${forbidden} belongs to Saved moments`);
  }
  assert.equal(list.querySelectorAll('.vp-th-now').length, 1);
});

test('generated clip titles and empty notes are not styled as the player\'s words', () => {
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, buildThread({ clips: [{ t: 20, text: 'Clip @ 0:20', own: false }], notes: [{ t: 30, text: '' }] }).items, { doc });
  assert.deepEqual(list.querySelectorAll('.own-words'), []);
  const [clip, note] = list.children.filter(li => li.dataset.kind !== 'now');
  assert.equal(clip.querySelector('.vp-th-text').textContent, 'Clip @ 0:20');
  assert.equal(clip.querySelector('.vp-th-kind').textContent, 'Clip');
  assert.equal(note.querySelector('.vp-th-text'), null);
  assert.equal(note.querySelector('.vp-th-row').getAttribute('aria-label'), 'Watch your note at 0:30');
});

test('an empty thread renders quiet copy and an optional way to the review', () => {
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, [], { doc });
  assert.equal(list.children.length, 1);
  assert.equal(list.children[0].className, 'vp-th-item vp-th-empty');
  assert.equal(list.children[0].textContent, 'Notes and clips you save will show here, in game time.');

  const opened = [];
  renderThread(list, buildThread({ events: [{ t: 9, text: 'Kill', key: 'k' }] }).items, { doc, onReview: event => opened.push(event.type) });
  assert.deepEqual(rowKinds(list), ['now', 'event', 'takeaway']);
  const missing = list.children.at(-1);
  assert.equal(missing.classList.contains('is-missing'), true);
  const link = missing.querySelector('.vp-th-link');
  assert.equal(link.textContent, 'Write your takeaway in the review');
  assert.equal(link.className, 'ui-link vp-th-link');
  assert.equal(link.dataset.action, undefined, 'the page click delegation must not see it');
  link.click();
  assert.deepEqual(opened, ['click']);

  renderThread(list, buildThread(sample()).items, { doc, onReview: () => {} });
  assert.equal(list.querySelectorAll('.is-missing').length, 0, 'a written takeaway needs no prompt');
});

test('rebuilding keeps keyboard focus on the same entry without scrolling', () => {
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, buildThread(sample()).items, { doc });
  list.children[4].querySelector('.vp-th-row').focus();
  renderThread(list, buildThread({ ...sample(), now: 0, notes: [{ t: 1, text: 'new note', key: 'bm:3' }, ...sample().notes] }).items, { doc });
  assert.equal(doc.activeElement?.dataset.reviewKey, 'key:b');
  assert.deepEqual(doc.activeElement.focusOptions, { preventScroll: true });

  const other = node(doc, 'button');
  other.focus();
  renderThread(list, buildThread(sample()).items, { doc });
  assert.equal(doc.activeElement, other, 'focus outside the thread is left alone');
});

test('moveNow moves the single now row with one insertBefore and keeps every other row', () => {
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, buildThread(sample()).items, { formatTime: seconds => `t${Math.floor(seconds)}`, doc });
  const before = list.children.filter(li => li.dataset.kind !== 'now');
  const nowRow = list.children[2];
  doc.mutations.length = 0;

  assert.equal(moveNow(list, 1320), true);
  assert.deepEqual(rowKinds(list), ['note', 'clip', 'event', 'event', 'now', 'event', 'note', 'takeaway']);
  assert.equal(doc.mutations.length, 1);
  assert.equal(doc.mutations[0][0], 'insertBefore');
  assert.equal(list.children[4], nowRow, 'the same row moves; nothing is rebuilt');
  assert.deepEqual(list.children.filter(li => li.dataset.kind !== 'now'), before);
  assert.equal(nowRow.querySelector('.vp-th-t').textContent, 't1320', 'the format from renderThread is reused');

  doc.mutations.length = 0;
  assert.equal(moveNow(list, 1321), false, 'no move when the place is unchanged');
  assert.equal(doc.mutations.length, 0);
  assert.equal(nowRow.querySelector('.vp-th-t').textContent, 't1321');

  assert.equal(moveNow(list, 99999), true);
  assert.deepEqual(rowKinds(list).slice(-2), ['now', 'takeaway'], 'the takeaway stays at the end');
  assert.equal(moveNow(list, 0, { formatTime: clock }), true);
  assert.equal(list.children[0], nowRow);
  assert.equal(nowRow.querySelector('.vp-th-t').textContent, '0:00');
});

test('moveNow and buildThread agree on the place for any time, and moveNow ignores lists without a marker', () => {
  const input = sample();
  delete input.takeaway;
  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, buildThread(input).items, { doc });
  for (const now of [0, 323, 323.25, 323.3, 750, 765.2, 1000, 1360, 5000]) {
    moveNow(list, now);
    assert.deepEqual(rowKinds(list), kinds(buildThread({ ...input, now }).items), `now = ${now}`);
  }
  assert.equal(moveNow(list, 4000), false, 'already last');
  assert.equal(list.children.at(-1).dataset.kind, 'now');

  const empty = node(doc, 'ol');
  renderThread(empty, [], { doc });
  assert.equal(moveNow(empty, 30), false);
  assert.equal(moveNow(null, 30), false);
});

test('thread copy never uses an em or en dash', async () => {
  const source = await readFile(new URL('../ui/vod-thread.mjs', import.meta.url), 'utf8');
  const file = ts.createSourceFile('vod-thread.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const literals = [];
  const visit = current => {
    if (ts.isStringLiteral(current) || ts.isNoSubstitutionTemplateLiteral(current) || ts.isTemplateHead(current)
      || ts.isTemplateMiddle(current) || ts.isTemplateTail(current)) literals.push(current.text);
    ts.forEachChild(current, visit);
  };
  visit(file);
  assert.ok(literals.length > 10);
  assert.deepEqual(literals.filter(text => /[–—]/.test(text)), []);

  const doc = fakeDocument();
  const list = node(doc, 'ol');
  renderThread(list, buildThread({ ...sample(), takeaway: '' }).items, { doc, onReview: () => {} });
  const labels = list.querySelectorAll('[data-thread-key]').map(el => el.getAttribute('aria-label') || '');
  assert.doesNotMatch(`${list.text} ${labels.join(' ')} ${buildThread(sample()).countText}`, /[–—]/);
});
