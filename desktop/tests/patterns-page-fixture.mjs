// The Patterns page's real renderer and action handler (patterns.js) in a VM with a
// synthetic DOM, transport and host. Shared by the review queue and back-to-back tests.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { polarityTone } from '../ui/meaning-tone.mjs';
import { createBulkReview } from '../ui/pattern-bulk-review.mjs';

const source = (await readFile(new URL('../ui/patterns.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');

export function fixture(reply = async () => ({ ok: true }), { search = '' } = {}) {
  const ids = new Map(), writes = [], reads = [], listeners = new Map(), timers = [], announced = [];
  let serverSnapshot = null;
  const clone = value => JSON.parse(JSON.stringify(value));
  function element() {
    const classes = new Set(), attributes = new Map();
    return {
      children: [], selectors: new Map(), dataset: {}, textContent: '', value: '', hidden: false, disabled: false,
      get firstChild() { return this.children[0] || null; },
      set innerHTML(_) { throw new Error('Pattern content must remain plain text'); },
      // Meaning colors travel as data-tone; any inline style write is recorded and fails the tone test.
      style: { writes: [], setProperty(name, value) { this.writes.push([name, value]); } },
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
      removeAttribute(name) { attributes.delete(name); },
      focus() { this.focused = true; },
      closest() { return null; },
      scrollIntoView() {},
      addEventListener() {},
    };
  }
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  $('m-video').readyState = 1;
  const document = {
    readyState: 'loading',
    querySelector: selector => $(selector),
    addEventListener(type, listener) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(listener);
    },
  };
  // The run's one-second beat is driven by the test (beat()); announcements to the shell are recorded.
  const reviews = page => createBulkReview({ ...page,
    schedule: (fn, ms) => timers.push({ fn, ms }), cancel: id => { if (timers[id - 1]) timers[id - 1].fn = null; } });
  const top = { dispatchEvent: event => { announced.push(event.type); return true; } };
  const context = vm.createContext({ document, window: { addEventListener() {}, top }, $, setTimeout, clearTimeout, polarityTone,
    createBulkReview: reviews, URLSearchParams, location: { search },
    CustomEvent: class { constructor(type) { this.type = type; } },
    console: { error() {} }, matchMedia: () => ({ matches: true }),
    show: (node, on) => { if (node) node.hidden = !on; },
    clear: node => { node.children = []; },
    resolveAssetUrl: (_media, path) => path ? `synthetic-media:${path}` : null,
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
      return clone(serverSnapshot);
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
  vm.runInContext(`${source}\nglobalThis.hooks = { render, markReviewed, selectPattern, selectView,
    activePattern, activeMoment, gotoMoment, saveMomentNote, commitPendingNote, setMomentQuality,
    playableSource, playMoment, constrainPlayback, loadPatterns, renderClosure,
    onVideoPlay, onVideoPause, onVideoEnded, review: bulk,
    setPlayback: (transport, window) => { _T = transport; _inlineWindow = window; } };`, context);
  const hooks = { ...context.hooks, render(data) {
    serverSnapshot = clone(data);
    return context.hooks.render(data);
  } };
  const cardTitles = () => $('pat-pick').children.map(card => card.querySelector('.pat-card-title').textContent);
  const click = async (id, action) => {
    const target = $(id);
    assert.equal(target.disabled, false, 'The selected control must be enabled');
    target.dataset.action = action;
    for (const listener of listeners.get('click') || []) {
      await listener({ target: { closest: () => target }, preventDefault() {} });
    }
  };
  // Let the one pending beat elapse (the run's advance after a moment ends).
  const beat = () => {
    const due = timers.filter(t => t.fn);
    due.forEach(t => { const fn = t.fn; t.fn = null; fn(); });
    return due.length;
  };
  return { $, hooks, writes, reads, cardTitles, click, timers, beat, announced, listeners };
}

export function playback(f) {
  const loads = [], calls = [], video = f.$('m-video');
  video.duration = 1200;
  const transport = {
    currentTime: 0, duration: 1200,
    load(url, options) {
      loads.push({ url, ...options });
      video.setAttribute('src', url);
      this.currentTime = options.startSeconds;
    },
    unload() { calls.push('unload'); video.removeAttribute('src'); },
    isExpanded: () => false,
    handleAction: () => false,
    pause() { calls.push('pause'); },
    seekTo(time) { calls.push(time); this.currentTime = time; },
    toggle() { calls.push('toggle'); },
  };
  f.hooks.setPlayback(transport, null);
  return { loads, calls, transport, video };
}

export function pattern(key, isReviewed = false, reviewMode = 'trend') {
  return {
    patternKey: key, kind: 'objective', title: `Pattern ${key}`, isReviewed, reviewMode,
    severity: 'medium', moments: [{ evidenceId: `evidence-${key}`, gameId: key, title: `Moment ${key}`,
      bookmarkId: key + 100, sourceKind: 'bookmark', note: '', hasNote: false, polarity: 'bad' }],
  };
}

export const snapshot = patterns => ({ patterns, pendingCount: patterns.filter(p => !p.isReviewed && p.reviewMode === 'trend').length,
  reviewedPatternCount: patterns.filter(p => p.isReviewed && p.reviewMode === 'trend').length, emptyText: 'No patterns yet' });
