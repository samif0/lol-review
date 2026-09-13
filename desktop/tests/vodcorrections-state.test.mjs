import test from 'node:test';
import assert from 'node:assert/strict';
import { createCorrections } from '../ui/vodcorrections.js';

function fixture(t, events = [{ id: 7, eventKey: 'event-7', eventType: 'DEATH', gameTimeSeconds: 122 }], options = {}) {
  const originalDocument = globalThis.document;
  const originalOption = globalThis.Option;
  const listeners = new Map();
  globalThis.document = { addEventListener(type, fn) {
    if (!listeners.has(type)) listeners.set(type, []);
    listeners.get(type).push(fn);
  } };
  globalThis.Option = class { constructor(label, value) { this.label = label; this.value = value; } };
  t.after(() => { globalThis.document = originalDocument; globalThis.Option = originalOption; });
  const elements = new Map();
  const disclosure = options.disclosure || null;
  const $ = id => {
    if (!elements.has(id)) elements.set(id, {
      value: '', textContent: '', options: [], selectedIndex: 0, visible: false, listeners: new Map(),
      add(option) { this.options.push(option); },
      addEventListener(type, fn) { this.listeners.set(type, fn); }, querySelectorAll() { return []; },
      closest: selector => selector === 'details' ? disclosure : null,
      focus() { if (options.onFocus) options.onFocus(id); },
      classList: { add() {}, remove() {}, toggle() {} },
    });
    return elements.get(id);
  };
  const writes = [];
  const controller = createCorrections({
    $, show: (el, value) => { el.visible = value; options.onShow?.(el, value); }, clear: el => { el.options = []; },
    clock: seconds => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`,
    video: () => null, gameId: 42,
    currentMarker: options.currentMarker,
    onRevealEditor: options.onRevealEditor,
    vod: { gameEvents: events, eventTypeCatalog: [{ type: 'DEATH', label: 'Death', kind: 'point', attrs: [] }] },
    reloadBookmarks: options.reloadBookmarks || (() => {}),
    core: { invoke: (...args) => {
      writes.push(args);
      if (options.invoke) return options.invoke(...args);
      throw new Error('Restore must not write');
    } },
  });
  const action = name => Promise.all((listeners.get('click') || []).map(fn => fn({
      target: { closest: selector => selector === '[data-action]' ? { dataset: { action: name } } : null },
      preventDefault() {},
    })));
  return { controller, $, writes, action, save: () => action('save_correction') };
}

const draft = (overrides = {}) => ({
  schema: 1, gameId: 42, selection: { eventKey: 'event-7', id: 7 },
  form: { correctionId: 'pending-correction', op: 'retime', attrs: {}, type: 'DEATH',
    start: ' 2:0', end: '', reason: '  unfinished thought\n' }, ...overrides,
});

test('correction draft restores raw fields on the same fresh event without sending a write', t => {
  const { controller, $, writes } = fixture(t);
  const saved = draft();
  assert.equal(controller.restoreState(saved), true);
  assert.deepEqual(controller.captureState(), saved);
  assert.equal($('vp-fix-form').visible, true);
  assert.equal($('vp-fix-sel').textContent, 'Selected: 2:02 Death');
  assert.deepEqual(writes, []);
});

test('correction draft cannot retarget a missing stable event key to a reused row id', t => {
  const { controller, $, writes } = fixture(t, [{ id: 7, eventKey: 'replacement', eventType: 'DEATH', gameTimeSeconds: 300 }]);
  assert.equal(controller.restoreState(draft()), false);
  assert.equal(controller.captureState().selection, null);
  assert.equal(controller.captureState().form, null);
  assert.equal($('vp-fix-form').visible, false);
  assert.deepEqual(writes, []);
});

test('correction draft rejects a different match and removed subject', t => {
  const { controller, writes } = fixture(t, [{ id: 7, eventKey: 'event-7', eventType: 'DEATH', gameTimeSeconds: 122, removed: true }]);
  assert.equal(controller.restoreState(draft({ gameId: 43 })), false);
  assert.equal(controller.captureState().selection, null);
  assert.equal(controller.restoreState(draft()), false);
  assert.equal(controller.captureState().form, null);
  assert.deepEqual(writes, []);
});

test('new-event draft survives without a selected subject and preserves the retry id', t => {
  const { controller, writes } = fixture(t);
  const saved = draft({ selection: null, form: { ...draft().form, op: 'add', start: ' 0:1', reason: 'New event draft ' } });
  assert.equal(controller.restoreState(saved), true);
  assert.deepEqual(controller.captureState(), saved);
  assert.deepEqual(writes, []);
});

test('edit and add shortcuts reveal collapsed event tools before focusing the form', t => {
  const disclosure = { open: false }, focus = [];
  const f = fixture(t, undefined, { disclosure, onFocus: id => focus.push({ id, open: disclosure.open }) });
  f.controller.restoreState(draft({ form: null }));
  f.controller.renderPanel();
  assert.equal(disclosure.open, false, 'selection and normal panel rendering remain collapsed');
  assert.equal(f.controller.handleKey({ key: 'e' }), true);
  assert.equal(disclosure.open, true);
  assert.equal(f.$('vp-fix-form').visible, true);
  disclosure.open = false;
  assert.equal(f.controller.handleKey({ key: 'n' }), true);
  assert.equal(disclosure.open, true);
  assert.equal(f.controller.captureState().form.op, 'add');
  assert.deepEqual(focus, [{ id: 'vp-fix-op', open: true }, { id: 'vp-fix-type', open: true }]);
  assert.deepEqual(f.writes, []);
});

test('restoring an open correction draft and fixing the current marker reveal the editor', async t => {
  const disclosure = { open: false };
  const f = fixture(t, undefined, { disclosure, currentMarker: () => ({ key: 'event-7', id: 7 }) });
  assert.equal(f.controller.restoreState(draft()), true);
  assert.equal(disclosure.open, true);
  assert.equal(f.$('vp-fix-reason').value, draft().form.reason);
  disclosure.open = false;
  await f.action('fix_event');
  assert.equal(disclosure.open, true);
  assert.equal(f.$('vp-fix-form').visible, true);
  assert.deepEqual(f.writes, []);
});

test('selecting another event rebinds a draft without reopening tools the user collapsed', t => {
  const disclosure = { open: false }, focus = [];
  const f = fixture(t, undefined, { disclosure, onFocus: id => focus.push(id) });
  f.controller.restoreState(draft());
  assert.deepEqual(focus, [], 'draft restoration leaves focus to shared match navigation');
  disclosure.open = false;
  f.controller.select({ key: 'event-8', eventKey: 'event-8', id: 8, type: 'DEATH', timeS: 140, endS: null });
  assert.equal(disclosure.open, false);
  assert.equal(f.controller.captureState().selection.eventKey, 'event-8');
  f.controller.renderPanel();
  assert.equal(disclosure.open, false);
  assert.deepEqual(focus, [], 'passive selection and rerendering do not move focus');
});

test('explicit edit paths leave cinema before opening or focusing their editor', async t => {
  const transitions = [];
  let cinema = true, isOpen = false;
  const disclosure = {
    get open() { return isOpen; },
    set open(value) { if (value) transitions.push('disclosure'); isOpen = value; },
  };
  const f = fixture(t, undefined, {
    disclosure, currentMarker: () => ({ key: 'event-7', id: 7 }),
    onRevealEditor() { transitions.push('leave-cinema'); cinema = false; },
    onShow(el, visible) {
      if (el === f.$('vp-fix-form') && visible) {
        transitions.push('form');
        assert.equal(cinema, false, 'a visible editor must no longer be hidden by Cinema');
      }
    },
    onFocus(id) {
      transitions.push(`focus:${id}`);
      assert.equal(cinema, false, 'the Cinema focus trap must be gone before editor focus');
      assert.equal(disclosure.open, true);
    },
  });
  const bar = { dataset: { eventKey: 'event-7', eventId: '7', eventType: 'DEATH', anchor: '122', label: 'Death' },
    classList: { contains: () => false } };
  const actions = [
    ['E', () => f.controller.handleKey({ key: 'e' }), 'vp-fix-op'],
    ['N', () => f.controller.handleKey({ key: 'n' }), 'vp-fix-type'],
    ['context menu', () => f.$('vp-markers').listeners.get('contextmenu')({
      target: { closest: selector => selector === '.evbar' ? bar : null }, preventDefault() {},
    }), 'vp-fix-op'],
    ['Fix button', () => f.action('fix_event'), 'vp-fix-op'],
  ];
  for (const [label, open, focusId] of actions) {
    f.controller.handleKey({ key: 'Escape' });
    f.controller.restoreState(draft({ form: null }));
    disclosure.open = false; cinema = true; transitions.length = 0;
    await open();
    assert.equal(transitions[0], 'leave-cinema', `${label}: host transition precedes disclosure`);
    assert.ok(transitions.includes('form'), `${label}: opens the correction form`);
    assert.equal(transitions.at(-1), `focus:${focusId}`, `${label}: ends with visible editor focus`);
  }
  assert.deepEqual(f.writes, [], 'revealing tools never submits a correction');
});

test('passive subject refresh does not request a cinema transition or reopen tools', t => {
  const disclosure = { open: false }, reveals = [], focus = [];
  const f = fixture(t, undefined, { disclosure,
    onRevealEditor: () => reveals.push('leave-cinema'), onFocus: id => focus.push(id),
  });
  const saved = draft();
  assert.equal(f.controller.restoreState(saved), true);
  assert.deepEqual(f.controller.captureState(), saved, 'raw draft content is retained');
  assert.equal(disclosure.open, true, 'returning to an open draft restores its disclosure');
  assert.deepEqual(focus, [], 'draft restoration leaves focus to shared match navigation');
  reveals.length = 0;
  disclosure.open = false;
  f.controller.select({ key: 'event-8', eventKey: 'event-8', id: 8, type: 'DEATH', timeS: 140, endS: null });
  f.controller.renderPanel();
  assert.equal(disclosure.open, false, 'passive rebind honors a collapsed disclosure');
  assert.deepEqual(reveals, []);
  assert.deepEqual(focus, []);
  assert.deepEqual(f.writes, []);
});

test('event-list selection resolves fresh stable identity without opening tools or accepting reused and removed rows', t => {
  const disclosure = { open: false };
  const f = fixture(t, [
    { id: 7, eventKey: 'replacement-7', eventType: 'DEATH', gameTimeSeconds: 300 },
    { id: 8, eventKey: 'removed-8', eventType: 'DEATH', gameTimeSeconds: 400, removed: true },
  ], { disclosure });
  assert.equal(f.controller.selectEvent({ key: 'missing-original-7', id: 7 }), false);
  assert.equal(f.controller.selectEvent({ key: 'removed-8', id: 8 }), false);
  assert.equal(f.controller.selectEvent({ id: 8 }), false);
  assert.equal(f.controller.selectEvent({ id: 0 }), false);
  assert.equal(f.controller.captureState().selection, null);
  assert.equal(f.controller.selectEvent({ key: 'replacement-7', id: 99 }), true);
  assert.deepEqual(f.controller.captureState().selection, { eventKey: 'replacement-7', id: 7 });
  assert.equal(f.$('vp-fix-sel').textContent, 'Selected: 5:00 Death');
  f.controller.clearSelection();
  assert.equal(f.controller.selectEvent({ id: 7 }), true);
  assert.equal(disclosure.open, false);
  assert.equal(f.controller.captureState().form, null);
  assert.deepEqual(f.writes, []);
});

const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

test('leaving after the nudge debounce fired waits for its write and marker reload', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const write = deferred(), reload = deferred(), reloadStarted = deferred();
  const f = fixture(t, undefined, {
    invoke: () => write.promise,
    reloadBookmarks: () => { reloadStarted.resolve(); return reload.promise; },
  });
  f.controller.restoreState(draft({ form: null }));
  f.controller.handleKey({ key: ']', code: 'BracketRight' });
  t.mock.timers.tick(600);
  assert.equal(f.writes.length, 1, 'the timer has already consumed the queued nudge');
  assert.equal(f.writes[0][1].payload.patch.gameTimeS, 123);
  let finished = false;
  const leaving = f.controller.flushPending().then(() => { finished = true; });
  await Promise.resolve();
  assert.equal(finished, false);
  write.resolve({});
  await reloadStarted.promise;
  assert.equal(finished, false, 'navigation also waits for the corrected marker state');
  reload.resolve(); await leaving;
  assert.match(f.$('vp-fix-hint').textContent, /^Moved \+1s/);
});

test('form save blocks departure through cleanup and Enter cannot submit the busy form twice', async t => {
  const write = deferred(), reload = deferred(), reloadStarted = deferred();
  const f = fixture(t, undefined, {
    invoke: () => write.promise,
    reloadBookmarks: () => { reloadStarted.resolve(); return reload.promise; },
  });
  f.controller.restoreState(draft({ form: { ...draft().form, start: '2:05' } }));
  const saving = f.save();
  f.$('vp-fix-form').listeners.get('keydown')({ key: 'Enter', target: { tagName: 'INPUT' }, preventDefault() {} });
  assert.equal(f.writes.length, 1);
  assert.equal(f.$('vp-fix-save').disabled, true);
  let finished = false;
  const leaving = f.controller.flushPending().then(() => { finished = true; });
  write.resolve({}); await reloadStarted.promise;
  assert.equal(finished, false);
  assert.equal(f.controller.captureState().form, null, 'confirmed old form is cleared before capture');
  reload.resolve(); await Promise.all([saving, leaving]);
  assert.equal(f.$('vp-fix-save').disabled, false);
});

test('failed correction keeps raw fields and its retry identity after the departure barrier', async t => {
  const write = deferred();
  const f = fixture(t, undefined, { invoke: () => write.promise });
  const saved = draft({ form: { ...draft().form, start: '2:05' } });
  f.controller.restoreState(saved);
  const saving = f.save(), leaving = f.controller.flushPending();
  write.reject(new Error('Backend unavailable'));
  await Promise.all([saving, leaving]);
  assert.deepEqual(f.controller.captureState(), saved);
  assert.equal(f.$('vp-fix-save').disabled, false);
  assert.equal(f.$('vp-fix-hint').textContent, 'Backend unavailable');
});

test('finishing an older correction preserves newer form edits as a new save', async t => {
  const write = deferred();
  const f = fixture(t, undefined, { invoke: () => write.promise });
  f.controller.restoreState(draft({ form: { ...draft().form, start: '2:05' } }));
  const saving = f.save();
  f.$('vp-fix-reason').value = '  A newer explanation\n';
  write.resolve({});
  await Promise.all([saving, f.controller.flushPending()]);
  const state = f.controller.captureState();
  assert.equal(state.form.reason, '  A newer explanation\n');
  assert.equal(state.form.correctionId, null, 'the committed request ID cannot be reused for different content');
  assert.equal(f.$('vp-fix-form').visible, true);
});
