import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createCorrections } from '../ui/vodcorrections.js';
import { createTimelineHover, ZOOM_LEARNED_KEY } from '../ui/timeline-hover.mjs';

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

function fixture(t, { saved = {} } = {}) {
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
  const chip = seek.appendChild(node('span', 'vp-tl-hover', 'vp-tl-hover'));
  const chipText = chip.appendChild(node('span', 'vp-tl-hover-text'));
  const chipHint = chip.appendChild(node('span', 'vp-tl-hover-hint', 'vp-tl-hover-hint'));
  chip.hidden = true; chipHint.hidden = true; chip.offsetWidth = 80;
  const items = new Map(Object.entries(saved));
  const storage = { items, getItem: key => items.get(key) ?? null, setItem: (key, value) => items.set(key, String(value)) };
  const clock = seconds => { const s = Math.max(0, Math.floor(seconds || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
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
  ghost.dataset = { action: 'jump', seconds: '260', eventKey: 'removed-8', eventId: '8', eventType: 'DEATH', anchor: '260', ghost: '1', correctionId: 'fix-8', label: 'Death' };
  const bookmark = markers.appendChild(node('span', '', 'ev ev-bm', { left: 296, top: 195, width: 8, height: 8 }));
  bookmark.dataset = { action: 'jump', seconds: '127' };
  const media = { paused: true, play() { plays.push(true); this.paused = false; } };
  const transport = { duration: 600, handleAction: () => false };
  const scope = { addEventListener() {}, getComputedStyle: element => ({ display: element.hidden ? 'none' : 'block', visibility: 'visible', opacity: '1' }) };
  document.defaultView = scope;
  // The topmost element under a point, skipping the chip (it never takes the pointer).
  document.elementFromPoint = (x, y) => {
    let hit = null;
    const walk = el => { for (const child of el.children) {
      if (child === chip || child.hidden) continue;
      const r = child.getBoundingClientRect();
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) hit = child;
      walk(child);
    } };
    walk(document);
    return hit;
  };
  // A stand-in MutationObserver: tests fire it after changing marker geometry.
  const observers = [];
  class Observer {
    constructor(callback) { this.callback = callback; this.watched = []; observers.push(this); }
    observe(target, options) { this.watched.push([target, options]); }
  }
  const context = vm.createContext({ $, document, window: scope, MouseEvent: InputEvent, clock,
    createTimelineHover: options => createTimelineHover({ ...options, storage, requestFrame: fn => { fn(); return 0; }, Observer }),
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
  const chipState = () => ({ hidden: chip.hidden, text: chipText.textContent, hint: !chipHint.hidden, x: chip.style['--hover-x'] });
  // Move everything drawn in the timeline, as a page scroll does under a resting pointer.
  const moveTimeline = (dx, dy) => {
    const walk = el => { el.rect.left += dx; el.rect.top += dy; el.children.forEach(walk); };
    walk(seek);
  };
  return { $, seek, content, markers, bar, label, ghost, bookmark, badge, details, controller, seeks, plays, focus,
    chip, chipState, storage, hooks: context.hooks, dispatch, make: node, document, observers, moveTimeline,
    scroll: () => document.dispatchEvent(new InputEvent('scroll', { bubbles: false, cancelable: false })),
    mutate: () => observers.forEach(observer => observer.callback([], observer)),
    flushTimers: () => { while (timers.length) timers.shift()(); } };
}

test('a click within three pixels of an event selects its real identity before exactly one timestamp jump', t => {
  const f = fixture(t);
  const event = f.dispatch(f.content, 'click', { clientX: 303, clientY: 160 });
  assert.equal(event.defaultPrevented, true);
  assert.deepEqual(f.seeks, [{ seconds: 123, selected: 'death-7' }]);
  assert.equal(f.plays.length, 1);
  assert.equal(f.hooks.queueSelection, '', 'Fix must not prefer the previous queue event');
  assert.equal(f.details.open, false, 'selecting a timeline event does not open the editor');
});

test('a click five pixels or more from any mark seeks the exact pointer time and selects nothing', t => {
  const f = fixture(t);
  const near = f.dispatch(f.content, 'click', { clientX: 306, clientY: 160 });
  assert.equal(near.defaultPrevented, false);
  f.dispatch(f.content, 'click', { clientX: 310.6, clientY: 175 });
  f.dispatch(f.content, 'click', { clientX: 290, clientY: 150 });
  assert.deepEqual(f.seeks.map(item => item.selected), [null, null, null]);
  assert.deepEqual(f.seeks.map(item => Math.round(item.seconds * 10) / 10), [206, 210.6, 190]);
  assert.equal(f.controller.captureState().selection, null);
  assert.equal(f.plays.length, 0, 'a scrub does not start playback');
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

test('a right-click within three pixels delegates to the real event editor without seeking or playing', t => {
  const f = fixture(t);
  const event = f.dispatch(f.content, 'contextmenu', { button: 2, clientX: 303, clientY: 160 });
  assert.equal(event.defaultPrevented, true);
  assert.equal(f.controller.captureState().selection.eventKey, 'death-7');
  assert.equal(f.controller.captureState().form.start, '2:03');
  assert.equal(f.details.open, true);
  assert.equal(f.focus.at(-1), 'vp-fix-op');
  assert.deepEqual(f.seeks, []);
  assert.equal(f.plays.length, 0);
});

test('a right-click on empty space opens no editor and selects nothing', t => {
  const f = fixture(t);
  const event = f.dispatch(f.content, 'contextmenu', { button: 2, clientX: 310, clientY: 160 });
  assert.equal(event.defaultPrevented, false);
  assert.equal(f.controller.captureState().selection, null);
  assert.equal(f.controller.captureState().form, null);
  assert.equal(f.details.open, false);
  assert.deepEqual(f.seeks, []);
});

test('removed event hit keeps ghost identity and does not open an editable live subject', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'click', { clientX: 461, clientY: 177 });
  assert.deepEqual(f.seeks, [{ seconds: 361, selected: null }], 'ten pixels away is a time');
  f.seeks.length = 0;
  f.dispatch(f.content, 'click', { clientX: 453, clientY: 177 });
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

test('hover rings exactly what a click selects and the chip names it', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'pointermove', { clientX: 303, clientY: 160 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), true);
  assert.deepEqual(f.chipState(), { hidden: false, text: 'Death at 2:03', hint: false, x: '160px' }, 'centered on the stem');
  f.dispatch(f.content, 'pointermove', { clientX: 306, clientY: 160 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), false, 'no ring where a click would seek');
  assert.equal(f.chipState().text, '3:26');
  f.dispatch(f.label, 'pointermove', { clientX: 283, clientY: 129 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), true);
  assert.equal(f.chipState().text, 'Death at 2:03');
  f.dispatch(f.ghost, 'pointermove', { clientX: 450, clientY: 177 });
  assert.equal(f.ghost.classList.contains('evbar-hit-hover'), true);
  assert.equal(f.chipState().text, 'Removed Death at 4:20');
  f.dispatch(f.seek, 'pointerleave', { bubbles: false });
  assert.equal(f.ghost.classList.contains('evbar-hit-hover'), false);
  assert.equal(f.chipState().hidden, true);
  assert.equal(f.controller.captureState().selection, null);
  assert.deepEqual(f.seeks, []);
});

test('the chip time is the time a click seeks to, and the chip stays inside the track', t => {
  const f = fixture(t);
  for (const x of [101, 310.6, 512.9, 699]) {
    f.dispatch(f.content, 'pointermove', { clientX: x, clientY: 175 });
    const shown = f.chipState().text;
    f.dispatch(f.content, 'click', { clientX: x, clientY: 175 });
    const seconds = f.seeks.at(-1).seconds;
    assert.equal(shown, `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`);
  }
  f.dispatch(f.content, 'pointermove', { clientX: 101, clientY: 175 });
  assert.equal(f.chipState().x, '0px', 'clamped at the left edge');
  f.dispatch(f.content, 'pointermove', { clientX: 699, clientY: 175 });
  assert.equal(f.chipState().x, '520px', 'clamped at the right edge (600 - 80)');
  f.hooks.setZoom(4);
  f.dispatch(f.content, 'pointermove', { clientX: 400, clientY: 175 });
  f.dispatch(f.content, 'click', { clientX: 400, clientY: 175 });
  assert.equal(f.chipState().text, '1:15', 'zoomed 4x, 300px into a 2400px track');
  assert.equal(Math.floor(f.seeks.at(-1).seconds), 75);
});

test('a click reported in whole pixels acts at the fractional pointer position the chip showed', t => {
  const f = fixture(t);
  // A scaled display: the pointer is at 310.8, the click arrives floored to 310.
  f.dispatch(f.content, 'pointermove', { clientX: 310.8, clientY: 175 });
  f.dispatch(f.content, 'pointerup', { clientX: 310.8, clientY: 175 });
  f.dispatch(f.content, 'click', { clientX: 310, clientY: 175 });
  assert.equal(f.chipState().text, '3:30');
  assert.equal(Math.round(f.seeks.at(-1).seconds * 10) / 10, 210.8, 'the precise time, not the floored pixel');
  // At the 3px edge the floored pixel would leave the stem's reach (the stem starts at
  // 299.5, so 296 is 3.5px away); the click still selects the event the ring showed.
  f.bar.rect.left = 299.5;
  f.dispatch(f.content, 'pointermove', { clientX: 296.6, clientY: 160 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), true);
  f.dispatch(f.content, 'pointerup', { clientX: 296.6, clientY: 160 });
  f.dispatch(f.content, 'click', { clientX: 296, clientY: 160 });
  assert.equal(f.seeks.at(-1).selected, 'death-7');
  assert.equal(f.seeks.at(-1).seconds, 123);
  // A click that is not where the pointer was keeps its own coordinates.
  f.dispatch(f.content, 'click', { clientX: 500, clientY: 175 });
  assert.equal(f.seeks.at(-1).seconds, 400);
});

test('bookmarks name themselves, and the chip hides over the zoom badge and while panning', t => {
  const f = fixture(t);
  f.dispatch(f.bookmark, 'pointermove', { clientX: 300, clientY: 198 });
  assert.equal(f.chipState().text, 'Bookmark at 2:07');
  f.bookmark.dataset.tone = 'clip';
  f.dispatch(f.bookmark, 'pointermove', { clientX: 300, clientY: 198 });
  assert.equal(f.chipState().text, 'Clip at 2:07');
  f.hooks.setZoom(2);
  f.dispatch(f.badge, 'pointermove', { clientX: 667, clientY: 114 });
  assert.equal(f.chipState().hidden, true);
  f.dispatch(f.content, 'pointermove', { clientX: 400, clientY: 175 });
  assert.equal(f.chipState().hidden, false);
  f.dispatch(f.content, 'pointerdown', { clientX: 400, clientY: 175 });
  f.dispatch(f.content, 'pointermove', { clientX: 370, clientY: 175 });
  assert.equal(f.seek.classList.contains('is-panning'), true);
  assert.equal(f.chipState().hidden, true);
  f.dispatch(f.content, 'pointerup', { clientX: 370, clientY: 175 });
});

test('the zoom hint shows near events below 4x and never again after the first zoom', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.deepEqual([f.chipState().text, f.chipState().hint], ['3:30', true], '9px from a stem');
  f.dispatch(f.content, 'pointermove', { clientX: 400, clientY: 160 });
  assert.equal(f.chipState().hint, false, 'far from every event');
  f.dispatch(f.content, 'pointermove', { clientX: 303, clientY: 160 });
  assert.equal(f.chipState().hint, false, 'never over an event');
  f.hooks.setZoom(4);
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.equal(f.chipState().hint, false, 'not at 4x and above');
  f.hooks.setZoom(1);
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.equal(f.chipState().hint, true);
  f.dispatch(f.seek, 'wheel', { ctrlKey: true, clientX: 310, clientY: 160, deltaY: -1 });
  assert.equal(f.storage.items.get(ZOOM_LEARNED_KEY), '1');
  assert.equal(f.chipState().hint, false);
  f.dispatch(f.seek, 'wheel', { ctrlKey: true, clientX: 310, clientY: 160, deltaY: 1 });
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.equal(f.hooks.zoom, 1);
  assert.equal(f.chipState().hint, false, 'back at 1x the hint stays retired');
});

test('a viewer who zoomed before never sees the hint', t => {
  const f = fixture(t, { saved: { [ZOOM_LEARNED_KEY]: '1' } });
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160 });
  assert.deepEqual([f.chipState().hidden, f.chipState().hint], [false, false]);
});

test('a scroll that moves the timeline under a resting pointer moves the preview with it', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'pointermove', { clientX: 303, clientY: 160 });
  assert.equal(f.chipState().text, 'Death at 2:03');
  // The page scrolls 32px: the stem now ends 6px above the pointer, which did not move.
  f.moveTimeline(0, -32);
  f.scroll();
  assert.deepEqual([f.bar.classList.contains('evbar-hit-hover'), f.chipState().text], [false, '3:23']);
  f.dispatch(f.content, 'click', { clientX: 303, clientY: 160 });
  assert.deepEqual(f.seeks, [{ seconds: 203, selected: null }], 'the click does what the chip said');
  // Scrolled back, the stem is under the pointer again: ringed, named, and what a click selects.
  f.moveTimeline(0, 32);
  f.scroll();
  assert.deepEqual([f.bar.classList.contains('evbar-hit-hover'), f.chipState().text], [true, 'Death at 2:03']);
  f.dispatch(f.content, 'click', { clientX: 303, clientY: 160 });
  assert.deepEqual(f.seeks.at(-1), { seconds: 123, selected: 'death-7' });
  // Something else scrolled over the pointer: nothing is previewed.
  const header = f.document.appendChild(f.make('div', '', 'sticky', { left: 0, top: 150, width: 800, height: 20 }));
  f.scroll();
  assert.deepEqual([f.chipState().hidden, f.bar.classList.contains('evbar-hit-hover')], [true, false]);
  header.hidden = true;
  // The timeline scrolled out from under the pointer entirely.
  f.moveTimeline(0, 200);
  f.scroll();
  assert.equal(f.chipState().hidden, true);
  // Once the pointer has left, a scroll previews nothing.
  f.moveTimeline(0, -200);
  f.dispatch(f.seek, 'pointerleave', { bubbles: false });
  f.scroll();
  assert.equal(f.chipState().hidden, true);
});

test('markers nudged or rebuilt under a resting pointer keep the chip and ring on what a click does', t => {
  const f = fixture(t);
  const [watch] = f.observers;
  assert.ok(watch.watched.some(([target, o]) => target === f.markers && o.childList && o.subtree && o.attributeFilter.includes('style')));
  assert.ok(watch.watched.some(([target, o]) => target === f.content && o.attributeFilter.includes('style')), 'zoom and pan');
  f.dispatch(f.content, 'pointermove', { clientX: 303, clientY: 160 });
  assert.equal(f.chipState().text, 'Death at 2:03');
  // A nudge moves the bar 8px right, away from the pointer.
  f.bar.rect.left = 307;
  f.mutate();
  assert.deepEqual([f.bar.classList.contains('evbar-hit-hover'), f.chipState().text], [false, '3:23']);
  // A rebuild (the event was removed): the new ghost under the pointer is ringed and named.
  f.markers.children = f.markers.children.filter(child => child !== f.bar);
  const ghost = f.markers.appendChild(f.make('span', '', 'evbar evbar-ghost', { left: 302, top: 148, width: 2, height: 38 }));
  ghost.dataset = { action: 'jump', seconds: '123', eventKey: 'death-7', eventId: '7', eventType: 'DEATH', anchor: '123', ghost: '1', label: 'Death' };
  f.mutate();
  assert.deepEqual([ghost.classList.contains('evbar-hit-hover'), f.chipState().text], [true, 'Removed Death at 2:03']);
  f.dispatch(f.seek, 'pointerleave', { bubbles: false });
  f.mutate();
  assert.equal(f.chipState().hidden, true, 'no preview once the pointer has left');
});

test('a press released outside the timeline neither freezes the preview nor pans on a plain move', t => {
  const f = fixture(t);
  // At 1x: pressed on the timeline, dragged off and released elsewhere.
  f.dispatch(f.content, 'pointerdown', { clientX: 400, clientY: 175, buttons: 1 });
  f.dispatch(f.seek, 'pointerleave', { bubbles: false, buttons: 1 });
  f.dispatch(f.content, 'pointermove', { clientX: 310, clientY: 160, buttons: 0 });
  assert.deepEqual([f.chipState().hidden, f.chipState().text, f.chipState().hint], [false, '3:30', true]);
  f.dispatch(f.content, 'pointermove', { clientX: 303, clientY: 160, buttons: 0 });
  assert.equal(f.bar.classList.contains('evbar-hit-hover'), true);
  // The same, then a zoom before the pointer returns: a move with no button never pans.
  f.dispatch(f.content, 'pointerdown', { clientX: 400, clientY: 175, buttons: 1 });
  f.dispatch(f.seek, 'pointerleave', { bubbles: false, buttons: 1 });
  f.hooks.setZoom(2);
  f.dispatch(f.content, 'pointermove', { clientX: 330, clientY: 175, buttons: 0 });
  assert.equal(f.hooks.pan, 0);
  assert.equal(f.seek.classList.contains('is-panning'), false);
  assert.deepEqual([f.chipState().hidden, f.chipState().text], [false, '1:55']);
});

test('while the button is held at 1x the chip follows the pointer to where the release acts', t => {
  const f = fixture(t);
  f.dispatch(f.content, 'pointermove', { clientX: 400, clientY: 175 });
  f.dispatch(f.content, 'pointerdown', { clientX: 400, clientY: 175, buttons: 1 });
  f.dispatch(f.content, 'pointermove', { clientX: 420, clientY: 175, buttons: 1 });
  assert.equal(f.chipState().text, '5:20');
  f.dispatch(f.content, 'pointerup', { clientX: 420, clientY: 175, buttons: 0 });
  f.dispatch(f.content, 'click', { clientX: 420, clientY: 175 });
  assert.deepEqual(f.seeks, [{ seconds: 320, selected: null }]);
});
