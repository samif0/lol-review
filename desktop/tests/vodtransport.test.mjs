import test from 'node:test';
import assert from 'node:assert/strict';
import { createTransport, RATE_CHOICES } from '../ui/vodtransport.js';

function shortcutEvent(key, target, extra = {}) {
  return { key, target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...extra };
}

// A small DOM fixture exercises focus/inert lifecycle and keyboard ownership.
// Actual visibility and viewport geometry are checked by the Electron smoke.
function fixture(t, modalExpand = true) {
  const listeners = new Map();
  const doc = {
    activeElement: null,
    defaultView: {
      scrollX: 0, scrollY: 0, scrollCalls: [],
      scrollTo(options) { this.scrollCalls.push(options); this.scrollX = options.left; this.scrollY = options.top; },
    },
    addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, new Set()); listeners.get(type).add(fn); },
    removeEventListener(type, fn) { listeners.get(type)?.delete(fn); },
    emit(type, event) { for (const fn of listeners.get(type) || []) fn(event); },
  };
  class Element {
    constructor(tag, parent = null) {
      this.tagName = tag.toUpperCase(); this.ownerDocument = doc;
      this.parentElement = parent; this.children = []; this.attributes = new Map();
      this.listeners = new Map();
      this.disabled = false; this.isConnected = true;
      const classes = new Set();
      this.classList = { contains: value => classes.has(value), toggle(value) {
        if (classes.has(value)) { classes.delete(value); return false; }
        classes.add(value); return true;
      } };
      parent?.children.push(this);
    }
    get inert() { return this.hasAttribute('inert'); }
    set inert(value) { if (value) this.setAttribute('inert', ''); else this.removeAttribute('inert'); }
    get tabIndex() { return this.hasAttribute('tabindex') ? Number(this.getAttribute('tabindex')) : /^(BUTTON|SELECT|INPUT|TEXTAREA)$/.test(this.tagName) ? 0 : -1; }
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    hasAttribute(name) { return this.attributes.has(name); }
    removeAttribute(name) { this.attributes.delete(name); }
    addEventListener(type, fn) { if (!this.listeners.has(type)) this.listeners.set(type, new Set()); this.listeners.get(type).add(fn); }
    removeEventListener(type, fn) { this.listeners.get(type)?.delete(fn); }
    emit(type, event = {}) { for (const fn of this.listeners.get(type) || []) fn(event); }
    contains(el) { return el === this || this.children.some(child => child.contains(el)); }
    closest(selector) {
      for (let node = this; node; node = node.parentElement) {
        if (selector === 'select' || selector === 'button') {
          if (node.tagName === selector.toUpperCase()) return node;
        } else if (node.hasAttribute('hidden') || node.inert) return node;
      }
      return null;
    }
    getClientRects() { return this.closest('[hidden], [inert]') ? [] : [{}]; }
    querySelectorAll() {
      return this.children.flatMap(child => [
        ...(/^(BUTTON|INPUT|SELECT|TEXTAREA)$/.test(child.tagName) || child.hasAttribute('tabindex') || child.hasAttribute('href') ? [child] : []),
        ...child.querySelectorAll(),
      ]);
    }
    focus() { doc.activeElement = this; doc.emit('focusin', { target: this }); }
  }
  const html = new Element('html');
  const body = doc.body = new Element('body', html);
  const shell = new Element('main', body);
  const picker = new Element('button', shell);
  const main = new Element('div', shell);
  const playcol = new Element('div', main);
  const stage = new Element('div', playcol);
  const exit = new Element('button', stage);
  const surface = new Element('div', stage); surface.setAttribute('tabindex', '0');
  const video = new Element('video', surface);
  Object.assign(video, {
    src: 'test-video', currentTime: 0, duration: 60, muted: false, volume: 1, playbackRate: 1, paused: true,
    pause() { this.paused = true; this.emit('pause'); },
    async play() { this.paused = false; this.emit('play'); },
    load() {},
  });
  const bar = new Element('div', stage);
  const play = new Element('button', bar);
  const rate = new Element('select', bar);
  const mute = new Element('button', bar);
  const fs = new Element('button', bar);
  const hiddenButton = new Element('button', bar); hiddenButton.setAttribute('hidden', '');
  const disabledButton = new Element('button', bar); disabledButton.disabled = true;
  const notes = new Element('textarea', playcol);
  const rail = new Element('div', main);
  const alreadyInert = new Element('div', body); alreadyInert.inert = true;
  const previousDocument = globalThis.document;
  globalThis.document = doc;
  t.after(() => { if (previousDocument === undefined) delete globalThis.document; else globalThis.document = previousDocument; });
  const transport = createTransport({ video, playBtn: play, muteBtn: mute, fsBtn: fs, rateSel: rate, expandTarget: stage, expandClass: 'pat-expanded', modalExpand });
  const key = (value, target = doc.activeElement || body, shiftKey = false, extra = {}) => {
    const event = shortcutEvent(value, target, { shiftKey, ...extra });
    doc.emit('keydown', event);
    return event;
  };
  return { doc, listeners, transport, stage, fs, exit, play, rate, mute, video, notes, picker, rail, alreadyInert, key, Element };
}

test('modal expansion isolates the page and Escape from playback speed restores its prior state', t => {
  const f = fixture(t);
  f.stage.setAttribute('role', 'region');
  f.stage.setAttribute('aria-label', 'Moment video');
  f.fs.focus();
  f.transport.attachKeyboard();
  assert.equal(f.transport.toggleEnlarge(), true);
  assert.equal(f.stage.getAttribute('role'), 'dialog');
  assert.equal(f.stage.getAttribute('aria-modal'), 'true');
  assert.equal(f.stage.getAttribute('aria-label'), 'Moment video');
  assert.equal(f.fs.getAttribute('aria-expanded'), 'true');
  assert.equal(f.notes.inert, true);
  assert.equal(f.picker.inert, true);
  assert.equal(f.rail.inert, true);
  assert.equal(f.stage.inert, false);
  assert.equal(f.doc.activeElement, f.fs);

  f.rate.focus();
  assert.equal(f.key('ArrowUp').defaultPrevented, false, 'speed selection retains native arrow keys');
  assert.equal(f.transport.stepSeconds, 5);
  assert.equal(f.key('Escape').defaultPrevented, true);
  assert.equal(f.transport.isExpanded(), false);
  assert.equal(f.doc.activeElement, f.fs);
  assert.equal(f.stage.getAttribute('role'), 'region');
  assert.equal(f.stage.getAttribute('aria-label'), 'Moment video');
  assert.equal(f.stage.hasAttribute('aria-modal'), false);
  assert.equal(f.stage.hasAttribute('tabindex'), false);
  assert.equal(f.notes.inert, false);
  assert.equal(f.picker.inert, false);
  assert.equal(f.rail.inert, false);
  assert.equal(f.alreadyInert.inert, true);
  assert.equal(f.fs.getAttribute('aria-expanded'), 'false');
  assert.equal(f.listeners.get('focusin').size, 0);
  f.transport.detachKeyboard();
});

test('hidden players and hidden ancestors cannot create an invisible modal', t => {
  const f = fixture(t);
  f.stage.setAttribute('role', 'region');
  f.stage.setAttribute('aria-label', 'Recording');
  f.picker.focus();
  f.doc.defaultView.scrollY = 250;
  f.transport.attachKeyboard();
  for (const hidden of [f.stage, f.stage.parentElement]) {
    hidden.setAttribute('hidden', '');
    const attributes = [...f.stage.attributes], buttonAttributes = [...f.fs.attributes];
    assert.equal(f.transport.toggleEnlarge(), false);
    f.key('f', f.picker);
    assert.equal(f.transport.isExpanded(), false);
    assert.equal(f.doc.activeElement, f.picker);
    assert.equal(f.notes.inert, false);
    assert.equal(f.picker.inert, false);
    assert.equal(f.rail.inert, false);
    assert.equal(f.alreadyInert.inert, true);
    assert.deepEqual([...f.stage.attributes], attributes);
    assert.deepEqual([...f.fs.attributes], buttonAttributes);
    assert.equal(f.listeners.get('focusin')?.size || 0, 0);
    assert.equal(f.doc.defaultView.scrollY, 250);
    assert.deepEqual(f.doc.defaultView.scrollCalls, []);
    hidden.removeAttribute('hidden');
  }
  assert.equal(f.transport.toggleEnlarge(), true, 'the same player works once loading finishes');
  assert.equal(f.stage.getAttribute('aria-modal'), 'true');
  assert.equal(f.doc.activeElement, f.fs);
  f.stage.setAttribute('hidden', '');
  assert.equal(f.transport.toggleEnlarge(), false, 'a player hidden during playback can still exit');
  assert.equal(f.picker.inert, false);
  assert.equal(f.doc.activeElement, f.picker);
  assert.equal(f.stage.getAttribute('role'), 'region');
  assert.equal(f.stage.hasAttribute('aria-modal'), false);
  f.transport.detachKeyboard();
});

test('modal Tab wraps visible enabled controls and programmatic focus cannot escape', t => {
  const f = fixture(t);
  f.transport.attachKeyboard();
  f.transport.toggleEnlarge();
  assert.equal(f.key('Tab').defaultPrevented, true);
  assert.equal(f.doc.activeElement, f.exit, 'forward wrap skips hidden and disabled trailing controls');
  assert.equal(f.key('Tab', f.exit, true).defaultPrevented, true);
  assert.equal(f.doc.activeElement, f.fs);
  f.play.focus();
  assert.equal(f.key('Tab').defaultPrevented, false, 'normal internal tab movement remains native');
  f.exit.focus();
  assert.equal(f.key(' ').defaultPrevented, false, 'Space activates the focused exit button instead of toggling playback');
  f.notes.focus();
  assert.equal(f.doc.activeElement, f.fs);
  f.transport.detachKeyboard();
  assert.equal(f.notes.inert, false);
  assert.equal(f.transport.isExpanded(), false);
});

test('unloading expanded media releases modal state and restores focus for the next moment', t => {
  const f = fixture(t);
  f.picker.focus();
  f.transport.toggleEnlarge();
  f.transport.unload();
  assert.equal(f.transport.isExpanded(), false);
  assert.equal(f.doc.activeElement, f.picker);
  assert.equal(f.notes.inert, false);
  assert.equal(f.alreadyInert.inert, true);
  assert.equal(f.stage.hasAttribute('role'), false);
  assert.equal(f.stage.hasAttribute('aria-label'), false);
  assert.equal(f.listeners.get('focusin').size, 0);
  f.transport.toggleEnlarge();
  assert.equal(f.stage.getAttribute('aria-modal'), 'true');
  f.transport.toggleEnlarge();
  assert.equal(f.picker.inert, false);
});

test('normal full-VOD layout expansion remains non-modal', t => {
  const f = fixture(t, false);
  f.picker.focus();
  f.transport.toggleEnlarge();
  assert.equal(f.transport.isExpanded(), true);
  assert.equal(f.doc.activeElement, f.picker);
  assert.equal(f.notes.inert, false);
  assert.equal(f.stage.hasAttribute('aria-modal'), false);
  assert.equal(f.stage.hasAttribute('role'), false);
  f.transport.unload();
  assert.equal(f.transport.isExpanded(), true, 'legacy host still controls layout restoration');
  f.transport.toggleEnlarge();
  assert.equal(f.doc.defaultView.scrollCalls.length, 0);
});

test('modal exit restores the scroll position captured before fixed layout clamps the page', t => {
  const f = fixture(t);
  const view = f.doc.defaultView;
  const toggle = f.stage.classList.toggle;
  f.stage.classList.toggle = value => {
    const expanded = toggle(value);
    if (expanded) { view.scrollX = 0; view.scrollY = 375; }
    return expanded;
  };
  f.fs.focus();
  f.transport.attachKeyboard();
  view.scrollX = 12;
  view.scrollY = 1025;
  f.transport.toggleEnlarge();
  assert.equal(view.scrollY, 375, 'fixture reproduces the actual Electron scroll clamp');
  f.rate.focus();
  f.key('Escape');
  assert.equal(view.scrollX, 12);
  assert.equal(view.scrollY, 1025);
  assert.deepEqual(view.scrollCalls[0], { left: 12, top: 1025, behavior: 'instant' });
  assert.equal(f.doc.activeElement, f.fs);
  assert.equal(f.notes.inert, false);

  view.scrollX = 0;
  view.scrollY = 850;
  f.transport.toggleEnlarge();
  f.transport.unload();
  assert.equal(view.scrollY, 850, 'each expansion keeps its own entry position, including unload');
  f.transport.detachKeyboard();
});

test('speed shortcuts traverse supported rates, clamp at both ends and sync the selector', t => {
  const f = fixture(t, false);
  f.video.src = '';
  const press = key => {
    const event = shortcutEvent(key, f.doc.body);
    assert.equal(f.transport.handleShortcut(event), true);
    assert.equal(event.defaultPrevented, true);
    assert.equal(f.rate.value, String(f.video.playbackRate));
    return f.video.playbackRate;
  };
  assert.equal(press('-'), 0.5);
  assert.equal(press('-'), 0.25);
  assert.equal(press('-'), 0.25);
  for (const expected of RATE_CHOICES.slice(1)) assert.equal(press('+'), expected);
  assert.equal(press('='), 3);
  assert.equal(press('-'), 2);
  assert.equal(press('='), 3);
  f.transport.setRate(0.1); assert.equal(f.video.playbackRate, 0.25);
  f.transport.setRate(99); assert.equal(f.video.playbackRate, 3);
  f.transport.setRate(NaN); assert.equal(f.video.playbackRate, 1);
  f.transport.attachVideo({ clickToToggle: false });
  f.rate.value = '1.5'; f.rate.emit('change');
  assert.equal(f.video.playbackRate, 1.5);
  f.video.playbackRate = 2; f.video.emit('ratechange');
  assert.equal(f.rate.value, '2');
});

test('mute and expansion shortcuts ignore held-key repeats and preserve nested hint markup', t => {
  const f = fixture(t, false);
  const icons = new Map();
  for (const button of [f.mute, f.fs, f.play]) {
    const icon = { textContent: '' };
    icons.set(button, icon);
    button.querySelector = selector => selector === '.tbtn-icon' ? icon : null;
    Object.defineProperty(button, 'textContent', { set() { assert.fail('Replacing button text would erase its key hint'); } });
  }
  f.transport.attachVideo({ clickToToggle: false });
  f.video.src = '';
  const press = (key, extra) => f.transport.handleShortcut(shortcutEvent(key, f.doc.body, extra));
  assert.equal(press('M'), true);
  assert.equal(f.video.muted, true);
  assert.equal(icons.get(f.mute).textContent, '🔇');
  assert.equal(f.mute.title, 'Unmute (M)');
  assert.equal(f.mute.getAttribute('aria-label'), 'Unmute');
  press('m', { repeat: true }); assert.equal(f.video.muted, true);
  press('m'); assert.equal(f.video.muted, false);
  assert.equal(icons.get(f.mute).textContent, '🔊');
  assert.equal(f.mute.title, 'Mute (M)');
  press('F'); assert.equal(f.transport.isExpanded(), true);
  assert.equal(icons.get(f.fs).textContent, '🗗');
  assert.equal(f.fs.title, 'Restore layout (F or Esc)');
  assert.equal(f.fs.getAttribute('aria-keyshortcuts'), 'F Escape');
  press('f', { repeat: true }); assert.equal(f.transport.isExpanded(), true);
  press('Escape'); assert.equal(f.transport.isExpanded(), false);
  assert.equal(icons.get(f.fs).textContent, '⛶');
  f.video.emit('play'); assert.equal(icons.get(f.play).textContent, '❚❚');
  f.transport.unload(); assert.equal(icons.get(f.play).textContent, '▶');
});

test('transport shortcuts preserve consumed, chorded, composing and editable key events', t => {
  const f = fixture(t, false);
  const input = new f.Element('input');
  const option = new f.Element('option', f.rate);
  const editable = new f.Element('span'); editable.isContentEditable = true;
  for (const key of ['m', 'f', '+', '=', '-', 'ArrowUp', 'ArrowRight', ' ']) {
    for (const target of [input, f.notes, f.rate, option, editable]) {
      const event = shortcutEvent(key, target);
      assert.equal(f.transport.handleShortcut(event), false, `${key} must remain native in ${target.tagName}`);
      assert.equal(event.defaultPrevented, false);
    }
    for (const guard of ['defaultPrevented', 'ctrlKey', 'metaKey', 'altKey', 'isComposing']) {
      const event = shortcutEvent(key, f.doc.body, { [guard]: true });
      assert.equal(f.transport.handleShortcut(event), false, `${guard} must win over ${key}`);
    }
  }
  for (const key of [' ', 'Enter']) {
    const event = shortcutEvent(key, f.play);
    assert.equal(f.transport.handleShortcut(event), false);
    assert.equal(event.defaultPrevented, false, 'native button activation must remain available');
  }
  assert.equal(f.video.muted, false);
  assert.equal(f.video.playbackRate, 1);
  assert.equal(f.video.currentTime, 0);
  assert.equal(f.video.paused, true);
  assert.equal(f.transport.stepSeconds, 5);
  assert.equal(f.transport.isExpanded(), false);
});

test('attached keyboard and direct full-player shortcut API share seek, step, speed and mute behavior', t => {
  const f = fixture(t, false);
  const apply = direct => {
    f.video.currentTime = 20;
    f.video.playbackRate = 1;
    f.video.muted = false;
    f.transport.setStep(5);
    for (const key of ['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft', '=', 'm']) {
      const event = shortcutEvent(key, f.doc.body);
      if (direct) assert.equal(f.transport.handleShortcut(event), true);
      else f.doc.emit('keydown', event);
      assert.equal(event.defaultPrevented, true);
    }
    return { time: f.video.currentTime, step: f.transport.stepSeconds, rate: f.video.playbackRate, selectedRate: f.rate.value, muted: f.video.muted };
  };
  const direct = apply(true);
  f.transport.attachKeyboard();
  assert.deepEqual(apply(false), direct);
  assert.deepEqual(direct, { time: 25, step: 5, rate: 1.5, selectedRate: '1.5', muted: true });
  const guarded = f.key('m', f.doc.body, false, { ctrlKey: true });
  assert.equal(guarded.defaultPrevented, false);
  assert.equal(f.video.muted, true);
  f.transport.detachKeyboard();
});
