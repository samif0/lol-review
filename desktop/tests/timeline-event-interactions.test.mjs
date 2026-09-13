import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createCorrections } from '../ui/vodcorrections.js';
import { findTimelineEventAtPoint } from '../ui/timeline-event-hit.mjs';

// Exercise the real timeline wiring, delegated jump, and corrections controller
// together. The small DOM below models capture/bubble ordering; geometry is fixed
// so these tests do not require a video decoder or an Electron installation.
const source = await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8');
function section(start, end) {
  const first = source.indexOf(start), last = source.indexOf(end, first);
  assert.ok(first >= 0 && last > first, 'Expected timeline source boundaries');
  return source.slice(first, last);
}
const timelineSource = section('const TL_ZOOM_MIN =', '// Keyboard: space = play/pause');
const jumpSource = section("document.addEventListener('click', (ev) => {", '// The speed dropdown');

class InputEvent {
  constructor(type, values = {}) {
    Object.assign(this, { type, bubbles: true, cancelable: true, detail: 0, button: 0,
      clientX: 0, clientY: 0, pointerId: 1, defaultPrevented: false }, values);
  }
  preventDefault() { if (this.cancelable) this.defaultPrevented = true; }
  stopPropagation() { this.stopped = true; }
  stopImmediatePropagation() { this.immediate = true; this.stopped = true; }
}

function fixture(t) {
  const oldDocument = globalThis.document, oldOption = globalThis.Option;
  const ids = new Map(), timers = [], seeks = [], plays = [], focus = [];
  let document, controller;
  function node(tag = 'div', id = '', className = '', rect = {}) {
    const listeners = new Map();
    const el = { tagName: tag.toUpperCase(), id, className, dataset: {}, children: [], parentElement: null,
      hidden: false, open: false, value: '', options: [], selectedIndex: 0, textContent: '', clientWidth: 600,
      style: { setProperty(name, value) { this[name] = value; } },
      rect: { left: 0, top: 0, width: 600, height: 120, ...rect },
      get ownerDocument() { return document; },
      classList: {
        contains: name => el.className.split(' ').includes(name),
        add(...names) { el.className = [...new Set([...el.className.split(' '), ...names])].filter(Boolean).join(' '); },
        remove(...names) { el.className = el.className.split(' ').filter(name => !names.includes(name)).join(' '); },
        toggle(name, force) { const on = force ?? !this.contains(name); on ? this.add(name) : this.remove(name); return on; },
      },
      matches(selector) {
        return selector.split(',').some(part => {
          part = part.trim();
          if (part.startsWith('#')) return id === part.slice(1);
          if (part === '[data-action]') return this.dataset.action !== undefined;
          if (part.startsWith('.')) return this.classList.contains(part.slice(1));
          return this.tagName === part.toUpperCase();
        });
      },
      closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) || null; },
      contains(other) { return other === this || this.children.some(child => child.contains(other)); },
      appendChild(child) { child.parentElement = this; this.children.push(child); return child; },
      querySelectorAll(selector) {
        return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
      },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      getBoundingClientRect() { return { ...this.rect, right: this.rect.left + this.rect.width,
        bottom: this.rect.top + this.rect.height, x: this.rect.left, y: this.rect.top }; },
      getClientRects() { return this.hidden ? [] : [this.getBoundingClientRect()]; },
      getAttribute(name) { return name === 'aria-hidden' ? null : this[name] ?? null; },
      addEventListener(type, listener, options = false) {
        if (!listeners.has(type)) listeners.set(type, []);
        listeners.get(type).push({ listener, capture: options === true || !!options.capture });
      },
      dispatchEvent(event) {
        event.target = this;
        const path = []; for (let p = this; p; p = p.parentElement) path.push(p);
        const invoke = (target, capture) => {
          event.currentTarget = target;
          for (const entry of target.listeners.get(event.type) || []) {
            if (entry.capture !== capture) continue;
            entry.listener(event);
            if (event.immediate) break;
          }
        };
        for (const target of path.slice(1).reverse()) { invoke(target, true); if (event.stopped) return !event.defaultPrevented; }
        invoke(this, true);
        if (!event.immediate) invoke(this, false);
        if (event.bubbles && !event.stopped) {
          for (const target of path.slice(1)) { invoke(target, false); if (event.stopped) break; }
        }
        return !event.defaultPrevented;
      },
      click() { return this.dispatchEvent(new InputEvent('click')); },
      add(option) { this.options.push(option); },
      focus() { focus.push(this.id); },
      setPointerCapture() {}, releasePointerCapture() {}, listeners,
    };
    if (id) ids.set(id, el);
    return el;
  }
  document = node('document');
  document.getElementById = id => ids.get(id) || null;
  document.createElement = tag => node(tag);
  const $ = id => ids.get(id) || document.appendChild(node('div', id));
  const seek = document.appendChild(node('div', 'vp-seek', 'vp-timeline', { left: 100, top: 100 }));
  const content = seek.appendChild(node('div', 'vp-tl-content'));
  const markers = content.appendChild(node('div', 'vp-markers', 'vp-markers'));
  const badge = seek.appendChild(node('button', 'vp-tl-zoom', 'vp-tl-zoombadge', { left: 650, top: 105, width: 40, height: 25 }));
  const details = document.appendChild(node('details', 'vp-event-tools'));
  details.appendChild(node('div', 'vp-fix-form'));
  const events = [
    { id: 7, eventKey: 'death-7', eventType: 'DEATH', gameTimeSeconds: 123, label: 'Death' },
    { id: 8, eventKey: 'removed-8', eventType: 'DEATH', gameTimeSeconds: 260, label: 'Death', removed: true, correctionId: 'fix-8' },
  ];
  const bar = markers.appendChild(node('span', '', 'evbar', { left: 299, top: 148, width: 2, height: 38 }));
  bar.dataset = { action: 'jump', seconds: '123', eventKey: 'death-7', eventId: '7', eventType: 'DEATH', anchor: '123', label: 'Death' };
  const label = bar.appendChild(node('span', '', 'evbar-code', { left: 280, top: 120, width: 40, height: 23 }));
  const ghost = markers.appendChild(node('span', '', 'evbar evbar-ghost', { left: 449, top: 166, width: 2, height: 20 }));
  ghost.dataset = { action: 'jump', seconds: '260', eventKey: 'removed-8', eventId: '8', eventType: 'DEATH', anchor: '260', ghost: '1', correctionId: 'fix-8' };
  const bookmark = markers.appendChild(node('span', '', 'ev ev-bm', { left: 296, top: 195, width: 8, height: 8 }));
  bookmark.dataset = { action: 'jump', seconds: '127' };
  const media = { paused: true, play() { plays.push(true); this.paused = false; } };
  const transport = { duration: 600, handleAction: () => false };
  const scope = { addEventListener() {}, getComputedStyle: element => ({ display: element.hidden ? 'none' : 'block', visibility: 'visible', opacity: '1' }) };
  document.defaultView = scope;
  const context = vm.createContext({ $, document, window: scope, MouseEvent: InputEvent, findTimelineEventAtPoint,
    _T: transport, _vod: { gameDurationSeconds: 600 }, _selectedReviewEvent: 'previous-queue-event',
    video: () => media, seekTo: seconds => seeks.push({ seconds, selected: controller.captureState().selection?.eventKey || null }),
    highlightReviewEvent() {}, watchReviewEvent() {}, show: (element, visible) => { element.hidden = !visible; },
    setTimeout: fn => { timers.push(fn); return timers.length; },
  });
  vm.runInContext(`${jumpSource}\n${timelineSource}\nwireSeekBar();
    globalThis.hooks = { setZoom(value) { _tlZoom = value; applyTimelineZoom(); },
      get zoom() { return _tlZoom; }, get pan() { return _tlPan; }, get queueSelection() { return _selectedReviewEvent; } };`, context);
  globalThis.document = document;
  globalThis.Option = class { constructor(label, value) { this.label = label; this.value = value; } };
  t.after(() => { globalThis.document = oldDocument; globalThis.Option = oldOption; });
  controller = createCorrections({ $, show: (el, visible) => { el.hidden = !visible; },
    clear: el => { el.options = []; el.children = []; }, clock: seconds => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`,
    video: () => media, gameId: 42, vod: { gameEvents: events }, reloadBookmarks() {},
  });
  const dispatch = (target, type, values = {}) => {
    const event = new InputEvent(type, { detail: type === 'click' ? 1 : 0, ...values });
    target.dispatchEvent(event); return event;
  };
  return { $, seek, content, markers, bar, label, ghost, bookmark, badge, details, controller, seeks, plays, focus,
    hooks: context.hooks, dispatch, flushTimers: () => { while (timers.length) timers.shift()(); } };
}

test('a click beside an event selects its real identity before exactly one timestamp jump', t => {
  const f = fixture(t);
  const event = f.dispatch(f.content, 'click', { clientX: 310, clientY: 160 });
  assert.equal(event.defaultPrevented, true);
  assert.deepEqual(f.seeks, [{ seconds: 123, selected: 'death-7' }]);
  assert.equal(f.plays.length, 1);
  assert.equal(f.hooks.queueSelection, '', 'Fix must not prefer the previous queue event');
  assert.equal(f.details.open, false, 'selecting a timeline event does not open the editor');
});

test('direct line, visible label and programmatic activation never add coordinate seeks', t => {
  const f = fixture(t);
  f.dispatch(f.label, 'click', { clientX: 283, clientY: 129 });
  f.dispatch(f.bar, 'click', { clientX: 300, clientY: 165 });
  f.bar.click();
  assert.deepEqual(f.seeks.map(item => item.seconds), [123, 123, 123]);
  assert.ok(f.seeks.every(item => item.selected === 'death-7'));
});

test('bookmark diamonds retain their own jump while empty space remains coordinate scrubbing', t => {
  const f = fixture(t);
  f.dispatch(f.bookmark, 'click', { clientX: 300, clientY: 198 });
  assert.deepEqual(f.seeks, [{ seconds: 127, selected: null }]);
  assert.equal(f.plays.length, 1);
  f.dispatch(f.content, 'click', { clientX: 600, clientY: 175 });
  assert.deepEqual(f.seeks[1], { seconds: 500, selected: null });
  assert.equal(f.plays.length, 1, 'empty scrub does not start playback');
});

test('a pan trailing click is stopped before corrections capture and document jump', t => {
  const f = fixture(t);
  f.hooks.setZoom(2);
  f.dispatch(f.bar, 'pointerdown', { clientX: 300, clientY: 165 });
  f.dispatch(f.bar, 'pointermove', { clientX: 270, clientY: 165 });
  f.dispatch(f.bar, 'pointerup', { clientX: 270, clientY: 165 });
  assert.equal(f.hooks.pan, -30);
  const trailing = f.dispatch(f.bar, 'click', { clientX: 270, clientY: 165 });
  assert.equal(trailing.defaultPrevented, true);
  assert.equal(f.controller.captureState().selection, null);
  assert.deepEqual(f.seeks, []);
  assert.equal(f.plays.length, 0);
  f.flushTimers();
  f.bar.click();
  assert.deepEqual(f.seeks, [{ seconds: 123, selected: 'death-7' }]);
});

test('nearby context menu delegates to the real event editor without seeking or playing', t => {
  const f = fixture(t);
  const event = f.dispatch(f.content, 'contextmenu', { button: 2, clientX: 310, clientY: 160 });
  assert.equal(event.defaultPrevented, true);
  assert.equal(f.controller.captureState().selection.eventKey, 'death-7');
  assert.equal(f.controller.captureState().form.start, '2:03');
  assert.equal(f.details.open, true);
  assert.equal(f.focus.at(-1), 'vp-fix-op');
  assert.deepEqual(f.seeks, []);
  assert.equal(f.plays.length, 0);
});

test('removed event hit keeps ghost identity and does not open an editable live subject', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'click', { clientX: 461, clientY: 177 });
  assert.deepEqual(f.seeks, [{ seconds: 260, selected: 'removed-8' }]);
  f.dispatch(f.ghost, 'contextmenu', { button: 2, clientX: 450, clientY: 177 });
  assert.equal(f.controller.captureState().form, null);
  assert.match(f.$('vp-fix-hint').textContent, /removed.*Delete to restore/);
});

test('zoom badge and wheel retain their actions without selecting or seeking an event', t => {
  const f = fixture(t);
  f.hooks.setZoom(2);
  f.dispatch(f.badge, 'click', { clientX: 667, clientY: 114 });
  assert.equal(f.hooks.zoom, 1);
  assert.equal(f.controller.captureState().selection, null);
  assert.deepEqual(f.seeks, []);
  const plain = f.dispatch(f.seek, 'wheel', { clientX: 300, deltaY: -1 });
  assert.equal(plain.defaultPrevented, false);
  assert.equal(f.hooks.zoom, 1);
  const zoom = f.dispatch(f.seek, 'wheel', { ctrlKey: true, clientX: 300, deltaY: -1 });
  assert.equal(zoom.defaultPrevented, true);
  assert.ok(f.hooks.zoom > 1);
  assert.deepEqual(f.seeks, []);
});

test('forgiving hover highlights the same event and clears when the pointer leaves', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), true);
  f.dispatch(f.seek, 'pointerleave', { bubbles: false });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), false);
  assert.equal(f.controller.captureState().selection, null);
  assert.deepEqual(f.seeks, []);
});
