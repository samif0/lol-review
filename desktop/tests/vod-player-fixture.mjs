import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createVodViewRestorer, createVodWriteBarrier, sameVodDraft, vodRestorePlan } from '../ui/vod-view-state.mjs';
import { objectiveTypeLabel, objectivePhaseLabel } from '../ui/objective-labels.mjs';
import { clipRowNarrationContext } from '../ui/clip-narration.mjs';
import { noteTranscriptProgress, renderClipTranscript } from '../ui/clip-transcript.mjs';
import { shareProgressLabel } from '../ui/clip-share-wait.mjs';

// Shared VOD player fixture: executes the page's real render/action handlers.
// Electron smoke covers native button activation and layout; this fixture covers
// event identity, drafts and write flows. Events follow DOM propagation (capture,
// target, bubble, stopPropagation; blur and focus do not bubble) and focus follows
// the browser: focusing moves blur to the previous element, and detaching a node
// that holds focus drops focus to the page without a blur event.
const source = (await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8'))
  .replace(/^import\s[\s\S]*?\sfrom\s+['"][^'"]+['"];\r?\n/gm, '');
if (/^import\s/m.test(source)) throw new Error('vod-player-fixture could not strip every import from vodplayer.js');
export const plain = value => JSON.parse(JSON.stringify(value));

const NON_BUBBLING = new Set(['blur', 'focus', 'load', 'loadedmetadata', 'error']);

function listenerStore() {
  const byType = new Map();
  return {
    add(type, listener, options) {
      const capture = typeof options === 'boolean' ? options : !!options?.capture;
      if (!byType.has(type)) byType.set(type, []);
      const list = byType.get(type);
      if (!list.some(entry => entry.listener === listener && entry.capture === capture)) list.push({ listener, capture });
    },
    remove(type, listener, options) {
      const capture = typeof options === 'boolean' ? options : !!options?.capture;
      byType.set(type, (byType.get(type) || []).filter(entry => entry.listener !== listener || entry.capture !== capture));
    },
    get: type => [...(byType.get(type) || [])],
  };
}

export function fixture({ events = [], objectives = [{ objectiveId: 7, title: 'Plan the fight' }, { objectiveId: 11, title: 'Keep a safe position' }], search = '', noVod = false, initialLoading = false, invokeReply, invokeWrite, timers, shareWait, narrationOk = false } = {}) {
  const studios = [];
  const ids = new Map(), listeners = listenerStore(), windowListeners = new Map(), writes = [], reads = [], seeks = [], selections = [], confirmations = [];
  let navOptions, restoreState = null, snapshot;
  // Run one event through capture, target and bubble phases. Returns the listener
  // results so the test can await the page's async handlers.
  function dispatch(target, event) {
    const path = [];
    for (let node = target; node; node = node.parent) path.push(node);
    path.push(document);
    let stopped = false, stoppedNow = false;
    Object.assign(event, { target,
      stopPropagation() { stopped = true; },
      stopImmediatePropagation() { stopped = true; stoppedNow = true; } });
    const results = [];
    const run = (node, capture) => {
      event.currentTarget = node;
      for (const entry of node.listeners.get(event.type)) {
        if (entry.capture !== capture) continue;
        results.push(entry.listener.call(node, event));
        if (stoppedNow) return;
      }
    };
    for (let index = path.length - 1; index > 0 && !stopped; index--) run(path[index], true);
    if (!stopped) { run(target, true); if (!stoppedNow) run(target, false); }
    if (event.bubbles !== false && !NON_BUBBLING.has(event.type)) {
      for (let index = 1; index < path.length && !stopped; index++) run(path[index], false);
    }
    return results;
  }
  const focusEvent = type => ({ type, bubbles: false, preventDefault() { this.defaultPrevented = true; } });
  // Chrome's focus fixup: removing the focused element (or an ancestor) moves focus
  // to the page and fires no blur, so the page never gets a chance to save.
  const dropFocusInside = node => {
    if (document.activeElement && node.contains(document.activeElement)) document.activeElement = null;
  };
  const document = { readyState: 'loading', activeElement: null, listeners,
    addEventListener(type, listener, options) { listeners.add(type, listener, options); },
    removeEventListener(type, listener, options) { listeners.remove(type, listener, options); },
    querySelectorAll(selector) {
      const [id, ...tail] = selector.split(' ');
      if (id.startsWith('#')) return tail.length ? $(id.slice(1)).querySelectorAll(tail.join(' ')) : [$(id.slice(1))];
      return [...ids.values()].flatMap(element => element.querySelectorAll(selector));
    },
    querySelector(selector) { return selector === '#statusline b' ? $('status-text') : this.querySelectorAll(selector)[0] || null; },
    createElement: tag => node(tag),
  };
  function node(tag = 'div', className = '') {
    const attributes = new Map(), events = listenerStore();
    const el = { tagName: tag.toUpperCase(), className, id: '', dataset: {}, children: [], parent: null, listeners: events,
      value: '', textContent: '', hidden: false, open: false, disabled: false, clientWidth: 600,
      style: { setProperty(name, value) { this[name] = value; } },
      classList: {
        contains: name => el.className.split(' ').includes(name),
        add(...names) { el.className = [...new Set([...el.className.split(' '), ...names])].filter(Boolean).join(' '); },
        remove(...names) { el.className = el.className.split(' ').filter(name => !names.includes(name)).join(' '); },
        toggle(name, force) { const active = force ?? !this.contains(name); if (active) this.add(name); else this.remove(name); return active; },
      },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
      removeAttribute: name => attributes.delete(name),
      appendChild(child) { child.remove(); this.children.push(child); child.parent = this; return child; },
      insertBefore(child, reference) {
        child.remove();
        const index = this.children.indexOf(reference);
        if (index < 0) return this.appendChild(child);
        this.children.splice(index, 0, child); child.parent = this; return child;
      },
      replaceChild(child, old) {
        child.remove();
        const index = this.children.indexOf(old);
        if (index < 0) return old;
        this.children[index] = child; child.parent = this; old.parent = null;
        dropFocusInside(old);
        return old;
      },
      contains(child) { return child === this || this.children.some(node => node.contains(child)); },
      get parentNode() { return this.parent; }, get parentElement() { return this.parent; },
      get nextSibling() { return this.parent?.children[this.parent.children.indexOf(this) + 1] || null; },
      querySelectorAll(selector) {
        if (selector.includes(' ')) {
          const [first, ...tail] = selector.split(' ');
          return this.querySelectorAll(first).flatMap(node => node.querySelectorAll(tail.join(' ')));
        }
        return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
      },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      matches(selector) {
        if (selector.includes(',')) return selector.split(',').some(part => this.matches(part.trim()));
        if (selector.startsWith('#')) return this.id === selector.slice(1);
        const attr = selector.match(/\[data-([\w-]+)(?:="([^"]*)")?\]/);
        const cls = selector.match(/^\.([\w-]+)/);
        if (cls && !this.classList.contains(cls[1])) return false;
        if (attr) {
          const key = attr[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase());
          return this.dataset[key] !== undefined && (attr[2] === undefined || this.dataset[key] === attr[2]);
        }
        return !!cls || this.tagName.toLowerCase() === selector.toLowerCase();
      },
      closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector) || null; },
      addEventListener(type, listener, options) { events.add(type, listener, options); },
      removeEventListener(type, listener, options) { events.remove(type, listener, options); },
      // Direct call of this element's own listeners (no propagation), for element-level wiring tests.
      emit(type, event) { for (const { listener } of events.get(type)) listener(event); },
      dispatchEvent(event) { dispatch(this, event); return !event.defaultPrevented; },
      focus() {
        if (document.activeElement === this) return;
        const previous = document.activeElement;
        document.activeElement = null;
        if (previous) dispatch(previous, focusEvent('blur'));
        document.activeElement = this;
        dispatch(this, focusEvent('focus'));
      },
      blur() {
        if (document.activeElement !== this) return;
        document.activeElement = null;
        dispatch(this, focusEvent('blur'));
      },
      scrollIntoView() { this.scrolled = true; },
      remove() {
        if (!this.parent) return;
        this.parent.children = this.parent.children.filter(child => child !== this);
        this.parent = null;
        dropFocusInside(this);
      },
      get firstChild() { return this.children[0] || null; },
      removeChild(child) { child.remove(); return child; },
      get ownerDocument() { return document; },
      get options() { return this.children.flatMap(child => child.tagName === 'OPTION' ? [child] : child.options); },
    };
    // A <select> reports its selected option, and assigning a value selects it.
    if (el.tagName === 'SELECT') {
      Object.defineProperty(el, 'value', {
        get() { const options = this.options; return (options.find(option => option.selected) || options[0])?.value ?? ''; },
        set(value) { for (const option of this.options) option.selected = option.value === String(value); },
      });
    }
    return el;
  }
  const $ = id => {
    if (!ids.has(id)) { const el = node(); el.id = id; ids.set(id, el); }
    return ids.get(id);
  };
  document.body = node('body');
  for (const filter of ['auto', 'clips', 'bm']) {
    const tab = node('button', 'tab'); tab.dataset.filter = filter; $('vp-tabs').appendChild(tab);
  }
  for (const id of ['vp-clip-tools', 'vp-bookmark-tools']) $('vp-tools').appendChild($(id));
  $('vp-clip-tools').appendChild($('vp-clip-note'));
  $('vp-bookmark-tools').appendChild($('vp-bm-note'));
  $('vp-composer').appendChild($('vp-composer-body'));
  $('vp-composer').hidden = true;
  const v = $('vp-video');
  Object.assign(v, { paused: true, currentTime: 0, duration: 200, readyState: 1, playbackRate: 1, volume: 1, muted: false,
    load() { this.loads = (this.loads || 0) + 1; this.currentTime = 0; },
    play() { this.playCalls = (this.playCalls || 0) + 1; return Promise.reject(new Error('Media unavailable in fixture')); } });
  $('vp-bm-note').tagName = 'TEXTAREA';
  $('vp-clip-note').tagName = 'INPUT';
  const transport = { currentTime: 0, duration: 200, stepSeconds: 5,
    seekTo(seconds) { seeks.push(seconds); this.currentTime = seconds; v.currentTime = seconds + 3; },
    setStep(value) { this.stepSeconds = value; }, setRate(value) { v.playbackRate = value; }, setTimeOrigin() {},
    isExpanded() { return $('vp-wrap').classList.contains('vp-expanded'); },
    toggleEnlarge() { return $('vp-wrap').classList.toggle('vp-expanded'); },
    pause() { v.paused = true; }, refreshReadout() {}, handleAction: () => false,
    handleShortcut(event) {
      if (event.key === 'Escape' && this.isExpanded()) { event.preventDefault(); this.toggleEnlarge(); return true; }
      return false;
    },
    // Mirrors the real core: one load replaces the previous load's error listener.
    load(url, { onError = null } = {}) {
      if (this.loadError) v.removeEventListener('error', this.loadError);
      this.loadError = onError;
      this.loadOptions = arguments[1];
      v.src = url;
      if (onError) v.addEventListener('error', onError, { once: true });
      v.load();
    },
  };
  const corrections = { selectEvent(value) { selections.push(value); }, renderPanel() {}, decorateBar() {}, appendGhosts() {},
    captureState: () => null, restoreState() {}, handleKey: () => false, pendingFlushes: 0,
    async flushPending() { this.pendingFlushes++; await this.heldNudge; } };
  const window = { location: { search },
    confirm(message) { confirmations.push(message); return true; },
    addEventListener(type, callback) { windowListeners.set(type, callback); }, dispatchEvent() {} };
  const context = vm.createContext({ document, $, window,
    show: (el, on) => { if (el) el.hidden = !on; }, clear: el => { for (const child of [...el.children]) el.removeChild(child); },
    tpl() {
      // Mirrors vodplayer.html tpl-bookmark: a plain row whose Watch button owns the jump.
      const row = node('div', 'moment');
      const watch = row.appendChild(node('button', 'ricon')); watch.dataset.action = 'jump';
      for (const name of ['vp-bm-src', 'vp-bm-time', 'vp-bm-note', 'vp-bm-clip']) row.appendChild(node('span', name));
      const bookmarkEdit = row.appendChild(node('div', 'vp-bm-edit'));
      bookmarkEdit.appendChild(node('input', 'vp-bm-editnote'));
      const bookmarkDelete = bookmarkEdit.appendChild(node('button', 'vp-bm-del')); bookmarkDelete.dataset.action = 'delete_bookmark';
      const share = row.appendChild(node('div', 'vp-bm-share'));
      const clipDelete = share.appendChild(node('button', 'vp-clipdel-btn')); clipDelete.dataset.action = 'delete_clip';
      const narrate = share.appendChild(node('button', 'vp-narrate-btn')); narrate.dataset.action = 'narrate_clip'; narrate.hidden = true;
      share.appendChild(node('span', 'vp-narrated-chip')).hidden = true;
      share.appendChild(node('div', 'vp-clip-transcript-host')).hidden = true;
      const edit = row.appendChild(node('div', 'vp-ev-edit'));
      edit.appendChild(node('select', 'vp-ev-obj')); edit.appendChild(node('input', 'vp-ev-note'));
      const status = row.appendChild(node('div', 'vp-moment-status')); status.hidden = true;
      status.appendChild(node('span', 'vp-moment-status-txt'));
      const retry = status.appendChild(node('button', 'key vp-moment-retry')); retry.dataset.action = 'retry_moment_write';
      return row;
    },
    createMatchNavigation: options => {
      navOptions = options;
      return { setGame() {}, restoreState: async () => {
        if (!restoreState) return;
        await options.restore(restoreState);
        $('vp-clip-tools').open = false; // shared disclosure restoration runs after provider state
        $('vp-saved-moments').open = false;
      } };
    },
    createVodViewRestorer, createVodWriteBarrier, sameVodDraft, vodRestorePlan,
    objectiveTypeLabel, objectivePhaseLabel, URLSearchParams,
    setTimeout: timers?.setTimeout || (() => 0), clearTimeout: timers?.clearTimeout || (() => {}),
    resolveAssetUrl: (_core, path) => `revu-media://fixture/${path}`,
    console: { error() {}, warn() {} }, CustomEvent: class {},
    clipRowNarrationContext, renderClipTranscript, noteTranscriptProgress, shareProgressLabel,
    openNarrationStudio(options) {
      const studio = { options, state: 'setup', closed: false, close() { this.closed = true; } };
      studios.push(studio);
      return studio;
    },
    waitForShareJob: options => shareWait ? shareWait(options) : { promise: new Promise(() => {}), cancel() {} },
    platform: { narrationAvailable: async () => true },
  });
  vm.runInContext(`${source}\n globalThis.hooks = { markersForObjective, reviewEvents, renderReviewEvents, renderMoments, renderObjBar,
    setFocusedObjective, wireFraming, watchReviewEvent, momentLanes, setClipIn, setClipOut, openShareLogin,
    copyToClipboard, copyClipLink, uploadShareJob, shareJob, setCore(core) { _core = core; },
    captureVodViewState, restoreVodViewState, restoreMatchView, applyClipDeepLink, render, reloadBookmarks, placeMarkers,
    setInitialLoading(value) { _vodInitialLoading = value; }, refreshLinkedRecording,
    async flush() { await _viewWrites.flush(); },
    configure(vod, objectives, transport, corrections, core) { _vod = vod; _gameId = vod.gameId; _objectives = objectives;
      _T = transport; _fx = corrections; _core = core; _framed = objectives.length > 0; _focusedObjId = objectives[0]?.objectiveId ?? null; },
    get selected() { return _selectedReviewEvent; }, get focused() { return _focusedObjId; },
    get filter() { return _bmFilter; }, setFilter(filter) { _bmFilter = filter; },
    setOverrides() { _bmObjUserSet = true; _clipObjUserSet = true; },
    renderClipState, saveClip, deleteClip, cancelShareJob, processShareQueue, enqueueShare, isRetryableShareError,
    setNarrationOk(value) { _narrationOk = value; }, get studio() { return _studio; },
    get shareJobs() { return _shareJobs; }, get pendingShareBmId() { return _pendingShareBmId; } };`, context);
  context.hooks.setNarrationOk(narrationOk);
  snapshot = { gameId: 42, filePath: noVod ? '' : 'match.mp4', hasVod: !noVod, gameDurationSeconds: 200,
    gameEvents: events, bookmarks: [], autoMoments: [], savedClips: [], eventTypeCatalog: [{ type: 'FOG_DEATH', label: 'Death without vision' }] };
  const core = { async invoke(command, args) {
    if (command === 'get_vod') { reads.push(args); return invokeReply ? invokeReply(command, args) : snapshot; }
    writes.push({ command, args: plain(args) });
    if (invokeWrite) return invokeWrite(command, args);
    if (command === 'add_bookmark') snapshot.bookmarks.push({ id: 99, gameTimeSeconds: args.payload.timeS,
      note: args.payload.note, objectiveId: args.payload.objectiveId });
    return { ok: true };
  } };
  context.hooks.configure(snapshot, objectives, transport, corrections, core);
  context.hooks.setInitialLoading(initialLoading);
  $('vp-novod').hidden = !noVod;
  return { $, document, snapshot, core, hooks: context.hooks, transport, corrections, window, seeks, selections, writes, reads, confirmations, studios,
    windowListener: type => windowListeners.get(type),
    linked: gameId => windowListeners.get('revu:vod-linked')({ detail: { gameId } }),
    setRestore(value) { restoreState = value; },
    get nav() { return navOptions; },
    // Dispatch through the target's ancestors and the document, then wait for the
    // page's async handlers. Blur and focus do not bubble.
    async emit(type, target, extra = {}) {
      const event = { type, preventDefault() { this.defaultPrevented = true; }, ...extra };
      for (const result of dispatch(target, event)) await result;
      return event;
    },
    // A user typing: each key goes to whatever holds focus at that moment (the page
    // body when nothing does) and only lands in a text field that kept focus.
    async type(text) {
      for (const key of text) {
        const target = document.activeElement || document.body;
        const event = await this.emit('keydown', target, { key });
        const editable = target.tagName === 'INPUT' || target.tagName === 'TEXTAREA';
        if (editable && document.activeElement === target && !event.defaultPrevented) target.value += key;
      }
    },
  };
}

export function deferredClipboard() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

export function clipboardTimers() {
  let now = 0, nextId = 0;
  const pending = new Map();
  return {
    setTimeout(callback, delay) { const id = ++nextId; pending.set(id, { callback, at: now + delay }); return id; },
    clearTimeout(id) { pending.delete(id); },
    advance(ms) {
      now += ms;
      for (const [id, timer] of [...pending]) {
        if (timer.at <= now) { pending.delete(id); timer.callback(); }
      }
    },
    get size() { return pending.size; },
  };
}

export function sharedClipControls(f, url = 'https://revu.lol/clip-42?view=full#moment') {
  const wrap = f.document.createElement('div'); wrap.className = 'vp-bm-share';
  const share = f.document.createElement('button'); share.className = 'vp-share-btn';
  share.dataset.action = 'share_clip'; share.dataset.shareBmId = '301';
  const shareLabel = f.document.createElement('span'); shareLabel.className = 'vp-share-lbl'; shareLabel.textContent = 'Share';
  share.appendChild(shareLabel); wrap.appendChild(share);
  const copy = f.document.createElement('button'); copy.className = 'vp-copy-btn';
  copy.dataset.action = 'copy_clip_link'; copy.dataset.shareUrl = url; copy.title = 'Copy link';
  const label = f.document.createElement('span'); label.textContent = 'Copy'; copy.appendChild(label); wrap.appendChild(copy);
  const link = f.document.createElement('span'); link.className = 'vp-share-url'; wrap.appendChild(link);
  f.$('vp-bookmarks').appendChild(wrap);
  return { copy, label, share, shareLabel, link };
}
