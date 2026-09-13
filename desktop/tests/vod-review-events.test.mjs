import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createVodViewRestorer, createVodWriteBarrier, sameVodDraft, vodRestorePlan } from '../ui/vod-view-state.mjs';
import { objectiveTypeLabel, objectivePhaseLabel } from '../ui/objective-labels.mjs';

// Execute the page's real render/action handlers. Electron smoke covers native
// button activation and layout; this fixture covers event identity and drafts.
const source = (await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8')).replace(/^import .*?;\r?\n/gm, '');
const plain = value => JSON.parse(JSON.stringify(value));

function fixture({ events = [], objectives = [{ objectiveId: 7, title: 'Plan the fight' }, { objectiveId: 11, title: 'Keep a safe position' }], search = '', noVod = false, initialLoading = false, invokeReply, invokeWrite } = {}) {
  const ids = new Map(), listeners = new Map(), windowListeners = new Map(), writes = [], reads = [], seeks = [], selections = [], confirmations = [];
  let navOptions, restoreState = null, snapshot;
  const document = { readyState: 'loading', activeElement: null,
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
    querySelectorAll(selector) {
      const [id, ...tail] = selector.split(' ');
      if (id.startsWith('#')) return tail.length ? $(id.slice(1)).querySelectorAll(tail.join(' ')) : [$(id.slice(1))];
      return [...ids.values()].flatMap(element => element.querySelectorAll(selector));
    },
    querySelector(selector) { return selector === '#statusline b' ? $('status-text') : this.querySelectorAll(selector)[0] || null; },
    createElement: tag => node(tag),
  };
  function node(tag = 'div', className = '') {
    const attributes = new Map(), events = new Map();
    const el = { tagName: tag.toUpperCase(), className, id: '', dataset: {}, children: [], parent: null,
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
      addEventListener(type, listener) { if (!events.has(type)) events.set(type, []); events.get(type).push(listener); },
      emit(type, event) { for (const fn of events.get(type) || []) fn(event); },
      focus() { document.activeElement = this; }, blur() { document.activeElement = null; },
      scrollIntoView() { this.scrolled = true; },
      remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); this.parent = null; },
      get options() { return this.children.flatMap(child => child.tagName === 'OPTION' ? [child] : child.options); },
    };
    return el;
  }
  const $ = id => {
    if (!ids.has(id)) { const el = node(); el.id = id; ids.set(id, el); }
    return ids.get(id);
  };
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
  };
  const corrections = { selectEvent(value) { selections.push(value); }, renderPanel() {}, decorateBar() {}, appendGhosts() {},
    captureState: () => null, restoreState() {}, handleKey: () => false };
  const context = vm.createContext({ document, $, window: { location: { search },
    confirm(message) { confirmations.push(message); return true; },
    addEventListener(type, callback) { windowListeners.set(type, callback); }, dispatchEvent() {} },
    show: (el, on) => { if (el) el.hidden = !on; }, clear: el => { el.children = []; },
    tpl() {
      const row = node('div', 'moment'); row.dataset.action = 'jump';
      for (const name of ['vp-bm-src', 'vp-bm-time', 'vp-bm-note', 'vp-bm-clip']) row.appendChild(node('span', name));
      const bookmarkEdit = row.appendChild(node('div', 'vp-bm-edit'));
      bookmarkEdit.appendChild(node('input', 'vp-bm-editnote'));
      const bookmarkDelete = bookmarkEdit.appendChild(node('button', 'vp-bm-del')); bookmarkDelete.dataset.action = 'delete_bookmark';
      const share = row.appendChild(node('div', 'vp-bm-share'));
      const clipDelete = share.appendChild(node('button', 'vp-clipdel-btn')); clipDelete.dataset.action = 'delete_clip';
      const edit = row.appendChild(node('div', 'vp-ev-edit'));
      edit.appendChild(node('select', 'vp-ev-obj')); edit.appendChild(node('input', 'vp-ev-note'));
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
    objectiveTypeLabel, objectivePhaseLabel, URLSearchParams, setTimeout: () => 0, clearTimeout() {},
    resolveAssetUrl: (_core, path) => `revu-media://fixture/${path}`,
    console: { error() {}, warn() {} }, CustomEvent: class {},
  });
  vm.runInContext(`${source}\n globalThis.hooks = { markersForObjective, reviewEvents, renderReviewEvents, renderMoments, renderObjBar,
    setFocusedObjective, wireFraming, watchReviewEvent, momentLanes, setClipIn, setClipOut, openShareLogin,
    captureVodViewState, restoreVodViewState, restoreMatchView, applyClipDeepLink, render, reloadBookmarks,
    setInitialLoading(value) { _vodInitialLoading = value; }, refreshLinkedRecording,
    async flush() { await _viewWrites.flush(); },
    configure(vod, objectives, transport, corrections, core) { _vod = vod; _gameId = vod.gameId; _objectives = objectives;
      _T = transport; _fx = corrections; _core = core; _framed = objectives.length > 0; _focusedObjId = objectives[0]?.objectiveId ?? null; },
    get selected() { return _selectedReviewEvent; }, get focused() { return _focusedObjId; },
    get filter() { return _bmFilter; }, setFilter(filter) { _bmFilter = filter; },
    setOverrides() { _bmObjUserSet = true; _clipObjUserSet = true; } };`, context);
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
  return { $, document, snapshot, core, hooks: context.hooks, transport, seeks, selections, writes, reads, confirmations,
    linked: gameId => windowListeners.get('revu:vod-linked')({ detail: { gameId } }),
    setRestore(value) { restoreState = value; },
    async emit(type, target, extra = {}) {
      const event = { target, preventDefault() { this.defaultPrevented = true; }, stopPropagation() {}, ...extra };
      for (const listener of listeners.get(type) || []) await listener(event);
      return event;
    },
  };
}

test('objective review events are chronological, shared-token aware, and exclude removed or invalid times while retaining zero', () => {
  const f = fixture({ events: [
    { id: 5, gameTimeSeconds: 45, objectiveIds: [7, 11], eventType: 'TEAMFIGHT', kind: 'teamfight-away' },
    { id: 1, gameTimeSeconds: 0, objectiveId: 7, eventType: 'DEATH' },
    ...[null, undefined, -1, NaN, Infinity, '12'].map((time, i) => ({ id: 100 + i, gameTimeSeconds: time, objectiveId: 7 })),
    { id: 9, gameTimeSeconds: 8, objectiveId: 7, removed: true },
    { id: 6, gameTimeSeconds: 20, objectiveIds: [11], objectiveId: 7 },
  ] });
  assert.deepEqual(plain(f.hooks.markersForObjective(7)).map(e => e.id), [1, 5]);
  assert.deepEqual(plain(f.hooks.markersForObjective(11)).map(e => e.id), [6, 5]);
  assert.equal(f.hooks.momentLanes().auto.length, 0, 'raw events never become writable evidence');
  f.hooks.renderMoments();
  assert.equal(f.$('vp-event-list').children.length, 2);
  assert.equal(f.$('vp-event-count').textContent, '2 events');
  assert.equal(f.$('vp-saved-moments').open, false);
});

test('unframed event rows use readable names and watching opens game-time lead-up without changing draft fields or saved editors', async () => {
  const f = fixture({ objectives: [], events: [
    { id: 1, gameTimeSeconds: 0, label: 'Spawn', eventType: 'START' },
    { id: 2, eventKey: 'death:35', gameTimeSeconds: 35.25, eventType: 'FOG_DEATH' },
    { id: 3, gameTimeSeconds: 60, eventType: 'SPELL_FLASH' },
  ] });
  f.$('vp-bm-note').value = '  Keep this\nnext time  ';
  f.$('vp-clip-note').value = 'Unfinished clip';
  f.hooks.renderMoments();
  const rows = f.$('vp-event-list').children;
  const editor = f.$('vp-bookmarks').appendChild(f.document.createElement('input')); editor.value = 'Existing inline draft';
  assert.deepEqual(rows.map(row => row.querySelector('.vp-event-title').textContent), ['Spawn', 'Death without vision', 'Spell flash']);
  assert.ok(rows.every(row => row.tagName === 'BUTTON' && row.type === 'button'));
  await f.emit('click', rows[1]);
  assert.deepEqual(f.seeks, [25.25]);
  assert.equal(f.$('vp-video').currentTime, 28.25, 'page delegates game/media offset conversion to transport');
  assert.deepEqual(plain(f.selections), [{ key: 'death:35', id: 2 }]);
  assert.equal(rows[1].getAttribute('aria-current'), 'true');
  assert.equal(f.$('vp-bookmarks').children[0], editor);
  assert.equal(editor.value, 'Existing inline draft');
  assert.equal(f.$('vp-bm-note').value, '  Keep this\nnext time  ');
  assert.equal(f.$('vp-clip-note').value, 'Unfinished clip');
  await f.emit('click', rows[0]);
  assert.equal(f.seeks.at(-1), 0);
  assert.equal(rows[1].getAttribute('aria-current'), null);
  assert.equal(f.writes.length, 0);
});

test('native objective selection preserves focus, playback and explicitly chosen draft pickers', () => {
  const f = fixture({ events: [{ id: 1, gameTimeSeconds: 10, objectiveId: 7 }, { id: 2, gameTimeSeconds: 30, objectiveId: 11 }] });
  f.hooks.renderObjBar(); f.hooks.wireFraming();
  const second = f.$('vp-objbar').children[1]; second.focus();
  f.$('vp-bm-note').value = 'Raw review draft'; f.$('vp-clip-note').value = 'Raw clip draft';
  f.$('vp-bm-obj').value = 'obj:7'; f.$('vp-clip-obj').value = 'obj:11'; f.hooks.setOverrides();
  f.$('vp-objbar').emit('keydown', { target: second, key: 'Enter', preventDefault() { throw new Error('Native button key must not be hijacked'); } });
  assert.equal(f.hooks.focused, 7, 'browser supplies the single native click');
  f.$('vp-objbar').emit('click', { target: second });
  assert.equal(f.hooks.focused, 11);
  assert.equal(f.document.activeElement.dataset.objId, '11');
  assert.equal(f.document.activeElement.getAttribute('aria-pressed'), 'true');
  assert.equal(f.document.activeElement.querySelector('.vp-objtab-count').textContent, '1 event');
  assert.equal(f.$('vp-event-list').children[0].dataset.reviewKey, 'id:2');
  assert.equal(f.$('vp-bm-obj').value, 'obj:7'); assert.equal(f.$('vp-clip-obj').value, 'obj:11');
  assert.equal(f.$('vp-bm-note').value, 'Raw review draft'); assert.equal(f.$('vp-clip-note').value, 'Raw clip draft');
  assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
});

test('saved moments retain untagged rows and user disclosure choice; an explicit clip link opens its objective and lane', async () => {
  const f = fixture({ search: '?gameId=42&clip=41&t=20' });
  f.snapshot.bookmarks = [{ id: 1, gameTimeSeconds: 4, note: 'Untagged' }, { id: 2, gameTimeSeconds: 8, objectiveId: 11, note: 'Other objective' }];
  f.snapshot.savedClips = [{ id: 41, hasClip: true, startTimeSeconds: 20, objectiveId: 11 }];
  f.hooks.renderMoments();
  assert.equal(f.$('vp-saved-moments').open, true);
  assert.deepEqual(plain(f.hooks.momentLanes().bm).map(item => item.id), [1]);
  f.$('vp-saved-moments').open = false; f.hooks.renderMoments();
  assert.equal(f.$('vp-saved-moments').open, false, 'background refresh preserves a manual collapse');
  f.hooks.applyClipDeepLink();
  assert.equal(f.hooks.focused, 11); assert.equal(f.hooks.filter, 'clips');
  assert.equal(f.$('vp-saved-moments').open, true);
  assert.equal(f.$('vp-bookmarks').querySelector('[data-ev-id="41"]').scrolled, true);
  const clipTab = f.$('vp-tabs').children.find(tab => tab.dataset.filter === 'clips');
  assert.equal(clipTab.getAttribute('aria-pressed'), 'true');
  f.setRestore({ ...plain(f.hooks.captureVodViewState()), focusedObjectiveId: 7, filter: 'bm' });
  await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-saved-moments').open, true, 'explicit clip remains visible after restoring old collapsed disclosures');
  assert.equal(f.hooks.focused, 11); assert.equal(f.hooks.filter, 'clips');
});

test('clip shortcuts reveal tools and restored raw clip drafts reopen once without forcing later disclosure choices', async () => {
  const f = fixture();
  f.transport.currentTime = 12; await f.emit('keydown', f.$('vp-video'), { key: 'i' });
  assert.equal(f.$('vp-clip-tools').open, true);
  f.$('vp-clip-tools').open = false; f.transport.currentTime = 28;
  await f.emit('keydown', f.$('vp-video'), { key: 'o' });
  assert.equal(f.$('vp-clip-tools').open, true);
  f.$('vp-clip-note').value = '  Finish this clip\n';
  const state = plain(f.hooks.captureVodViewState());
  f.setRestore(state); f.$('vp-clip-note').value = ''; f.$('vp-clip-tools').open = false;
  await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-clip-tools').open, true);
  assert.equal(f.$('vp-clip-note').value, '  Finish this clip\n');
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, state.clip);
  f.$('vp-clip-tools').open = false; await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-clip-tools').open, false, 'metadata refresh must not reopen a user-collapsed tool');
  f.hooks.openShareLogin(5, '');
  assert.equal(f.$('vp-clip-tools').open, true); assert.equal(f.$('vp-sharelogin').hidden, false);
  assert.equal(f.writes.length, 0);
});

test('clip shortcuts keep cinema open, reveal the real editor, and focus the note only after the out point', async () => {
  const f = fixture();
  const tools = f.$('vp-clip-tools'), note = f.$('vp-clip-note'), video = f.$('vp-video');
  f.transport.toggleEnlarge(); video.focus(); video.paused = false;
  for (const [key, seconds] of [['i', 12], ['o', 28]]) {
    tools.open = false;
    f.transport.currentTime = seconds;
    const event = await f.emit('keydown', video, { key });
    assert.equal(event.defaultPrevented, true);
    assert.equal(f.transport.isExpanded(), true);
    assert.equal(f.$('vp-composer').hidden, false);
    assert.equal(f.$('vp-composer').contains(tools), true, 'uses the existing draft fields inside the visible composer');
    assert.equal(tools.open, true);
    if (key === 'i') {
      assert.notEqual(f.document.activeElement, note, 'starting the range leaves playback shortcuts available');
      assert.equal(video.paused, false);
    }
  }
  assert.equal(f.document.activeElement, note);
  assert.equal(video.paused, true);
  const state = plain(f.hooks.captureVodViewState());
  assert.equal(state.clip.start, 12);
  assert.equal(state.clip.end, 28);
  assert.equal(f.writes.length, 0);
});

test('B opens a bookmark draft in cinema without saving and preserves its captured time while typing', async () => {
  const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-bm-note');
  f.transport.toggleEnlarge(); f.transport.currentTime = 42; video.paused = false;
  const opened = await f.emit('keydown', video, { key: 'b' });
  assert.equal(opened.defaultPrevented, true);
  assert.equal(f.transport.isExpanded(), true); assert.equal(video.paused, true);
  assert.equal(f.$('vp-composer').hidden, false);
  assert.equal(f.$('vp-composer').contains(note), true);
  assert.equal(f.document.activeElement, note);
  assert.equal(f.writes.length, 0, 'opening a bookmark is not a backend write');
  f.transport.currentTime = 75;
  note.value = '  Space before the fight\nKeep an exit  ';
  const letter = await f.emit('keydown', note, { key: 's' });
  const newline = await f.emit('keydown', note, { key: 'Enter' });
  assert.equal(letter.defaultPrevented, undefined); assert.equal(newline.defaultPrevented, undefined);
  await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.writes[0], { command: 'add_bookmark', args: { payload: {
    gameId: 42, timeS: 42, note: 'Space before the fight\nKeep an exit', objectiveId: 7,
  } } });
  assert.equal(note.value, '');
  assert.equal(f.transport.isExpanded(), true);
  assert.match(f.$('vp-bm-hint').textContent, /saved/i);
  assert.equal(f.$('vp-bm-hint').hidden, false);
});

test('Escape closes the composer before cinema, keeps drafts, and returns the real fields to their page', async () => {
  const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-clip-note');
  const originalParent = f.$('vp-clip-tools').parentNode;
  f.transport.toggleEnlarge(); f.transport.currentTime = 12;
  await f.emit('keydown', video, { key: 'i' });
  f.transport.currentTime = 28; await f.emit('keydown', video, { key: 'o' });
  note.value = 'Keep this unsaved clip';
  const before = plain(f.hooks.captureVodViewState()).clip;
  const escape = await f.emit('keydown', note, { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true);
  assert.equal(f.$('vp-composer').hidden, true);
  assert.equal(f.transport.isExpanded(), true);
  assert.equal(f.$('vp-clip-tools').parentNode, originalParent);
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, before);
  await f.emit('keydown', video, { key: 'Escape' });
  assert.equal(f.transport.isExpanded(), false);
  assert.equal(f.writes.length, 0);
});

test('clip field shortcuts save in cinema and ordinary S remains available for note text', async () => {
  for (const shortcut of [{ key: 'Enter' }, { key: 's', ctrlKey: true }, { key: 'Enter', ctrlKey: true }]) {
    const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-clip-note');
    f.transport.toggleEnlarge(); f.transport.currentTime = 12;
    await f.emit('keydown', video, { key: 'i' });
    f.transport.currentTime = 28; await f.emit('keydown', video, { key: 'o' });
    note.value = '  Safer spacing  ';
    const letter = await f.emit('keydown', note, { key: 's' });
    assert.equal(letter.defaultPrevented, undefined); assert.equal(f.writes.length, 0);
    await f.emit('keydown', note, { ...shortcut, repeat: true });
    await f.emit('keydown', note, { ...shortcut, isComposing: true });
    await f.hooks.flush(); assert.equal(f.writes.length, 0);
    const save = await f.emit('keydown', note, shortcut); await f.hooks.flush();
    assert.equal(save.defaultPrevented, true); assert.equal(f.writes.length, 1);
    assert.equal(f.writes[0].command, 'extract_clip');
    assert.equal(f.writes[0].args.payload.startTimeS, 12); assert.equal(f.writes[0].args.payload.endTimeS, 28);
    assert.equal(f.writes[0].args.payload.note, 'Safer spacing');
    assert.equal(note.value, ''); assert.equal(f.transport.isExpanded(), true);
    assert.match(f.$('vp-clip-hint').textContent, /saved/i, 'clearing the saved range must not clear its confirmation');
    assert.equal(f.$('vp-clip-hint').hidden, false);
  }
});

test('S outside text fields saves the active bookmark composer and held shortcut keys do not recapture drafts', async () => {
  const f = fixture(); const video = f.$('vp-video');
  f.transport.toggleEnlarge(); f.transport.currentTime = 35;
  await f.emit('keydown', video, { key: 'b' });
  f.$('vp-bm-note').value = 'Captured once';
  f.transport.currentTime = 80;
  await f.emit('keydown', video, { key: 'b', repeat: true });
  await f.emit('keydown', video, { key: 'b', isComposing: true });
  await f.emit('keydown', video, { key: 's', repeat: true });
  assert.equal(f.writes.length, 0);
  await f.emit('keydown', video, { key: 's' }); await f.hooks.flush();
  assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, 'add_bookmark');
  assert.equal(f.writes[0].args.payload.timeS, 35);
  assert.equal(f.writes[0].args.payload.note, 'Captured once');
});

test('failed composer saves preserve notes and ranges for retry without leaving cinema', async () => {
  for (const mode of ['clip', 'bookmark']) {
    let fail = true;
    const f = fixture({ invokeWrite: () => {
      if (fail) throw new Error('Temporary write failure');
      return { ok: true };
    } });
    const video = f.$('vp-video'); f.transport.toggleEnlarge(); f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note');
    const hint = f.$(mode === 'clip' ? 'vp-clip-hint' : 'vp-bm-hint');
    note.value = '  Keep the failed draft  ';
    const before = plain(f.hooks.captureVodViewState())[mode];
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 1);
    assert.equal(f.transport.isExpanded(), true); assert.equal(f.$('vp-composer').hidden, false);
    assert.deepEqual(plain(f.hooks.captureVodViewState())[mode], before);
    assert.equal(hint.hidden, false); assert.equal(hint.classList.contains('err'), true);
    fail = false;
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 2); assert.equal(note.value, ''); assert.match(hint.textContent, /saved/i);
  }
});

test('pending composer saves deduplicate submissions and preserve edits made while the backend writes', async () => {
  for (const mode of ['clip', 'bookmark']) {
    let resolveWrite;
    const f = fixture({ invokeWrite: () => new Promise(resolve => { resolveWrite = resolve; }) });
    const video = f.$('vp-video'); f.transport.toggleEnlarge(); f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note');
    note.value = 'Submitted note';
    await f.emit('keydown', note, { key: 's', ctrlKey: true });
    await f.emit('keydown', note, { key: 's', ctrlKey: true });
    assert.equal(f.writes.length, 1, 'two submissions share the active write');
    note.value = '  New note typed while saving  ';
    f.$(mode === 'clip' ? 'vp-clip-obj' : 'vp-bm-obj').value = 'obj:11';
    f.hooks.setOverrides();
    if (mode === 'clip') { f.transport.currentTime = 55; await f.emit('keydown', video, { key: 'o' }); }
    const newerDraft = plain(f.hooks.captureVodViewState())[mode];
    resolveWrite({ ok: true }); await f.hooks.flush();
    assert.deepEqual(plain(f.hooks.captureVodViewState())[mode], newerDraft);
    assert.equal(f.writes[0].args.payload.note, 'Submitted note');
    assert.equal(f.transport.isExpanded(), true);
  }
});

test('Make clip uses its bookmark card note and 15-second context, preserves an explicit no-objective tag, and saves in cinema', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Original bookmark' }];
  f.hooks.setFilter('bm'); f.hooks.renderMoments(); f.transport.toggleEnlarge();
  const row = f.$('vp-bookmarks').children[0], make = row.querySelector('.vp-bm-makeclip');
  row.querySelector('.vp-bm-editnote').value = '  Updated before blur  ';
  assert.equal(make.textContent, 'Make clip'); assert.equal(make.disabled, false);
  await f.emit('click', make);
  assert.deepEqual(f.seeks, [25], 'preparing the clip seeks to its lead-up rather than the bookmark itself');
  const draft = plain(f.hooks.captureVodViewState()).clip;
  assert.equal(draft.start, 25); assert.equal(draft.end, 55);
  assert.equal(draft.note, '  Updated before blur  '); assert.equal(draft.picker, ''); assert.equal(draft.userSet, true);
  assert.equal(f.document.activeElement, f.$('vp-clip-note'));
  assert.equal(f.transport.isExpanded(), true); assert.equal(f.$('vp-video').paused, true);
  assert.equal(f.writes.length, 0); assert.equal(f.snapshot.bookmarks.length, 1);
  await f.emit('keydown', f.$('vp-clip-note'), { key: 's', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, 'extract_clip');
  const payload = f.writes[0].args.payload;
  assert.equal(payload.startTimeS, 25); assert.equal(payload.endTimeS, 55); assert.equal(payload.note, 'Updated before blur');
  assert.equal('objectiveId' in payload, false, 'an untagged source must not inherit the currently focused objective');
  assert.equal('promptId' in payload, false);
  assert.deepEqual(f.snapshot.bookmarks, [{ id: 9, gameTimeSeconds: 40, note: 'Original bookmark' }]);
});

test('Make clip preserves an unfinished clip and reveals it for saving instead of replacing its draft', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Bookmark to convert' }];
  f.hooks.setFilter('bm'); f.hooks.renderMoments();
  f.transport.toggleEnlarge(); f.transport.currentTime = 8;
  await f.emit('keydown', f.$('vp-video'), { key: 'i' });
  f.transport.currentTime = 18; await f.emit('keydown', f.$('vp-video'), { key: 'o' });
  f.$('vp-clip-note').value = '  My unfinished clip  ';
  const before = plain(f.hooks.captureVodViewState()).clip;
  await f.emit('keydown', f.$('vp-clip-note'), { key: 'Escape' });
  await f.emit('click', f.$('vp-bookmarks').querySelector('.vp-bm-makeclip'));
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, before);
  assert.equal(f.$('vp-composer').hidden, false); assert.equal(f.transport.isExpanded(), true);
  assert.equal(f.document.activeElement, f.$('vp-clip-note'));
  assert.match(f.$('vp-clip-hint').textContent, /unfinished clip/i);
  assert.equal(f.$('vp-clip-hint').classList.contains('err'), true);
  assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
});

test('native card buttons keep Enter and Space activation instead of having the enclosing moment seek', async () => {
  for (const key of ['Enter', ' ']) {
    for (const action of ['make_clip_from_bookmark', 'delete_bookmark', 'delete_clip']) {
      const f = fixture();
      f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Native action', hasClip: action === 'delete_clip' }];
      f.hooks.setFilter(action === 'delete_clip' ? 'clips' : 'bm'); f.hooks.renderMoments();
      const button = f.$('vp-bookmarks').querySelector(`[data-action="${action}"]`);
      const icon = button.appendChild(f.document.createElement('span'));
      const activation = await f.emit('keydown', icon, { key });
      assert.equal(activation.defaultPrevented, undefined, 'browser retains native button activation');
      assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
      await f.emit('click', icon); // the single click supplied by native activation
      if (action === 'make_clip_from_bookmark') {
        assert.deepEqual(f.seeks, [25]); assert.equal(f.writes.length, 0);
      } else {
        assert.equal(f.seeks.length, 0); assert.equal(f.confirmations.length, 1);
        assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, action);
        assert.equal(f.writes[0].args.payload.bookmarkId, 9);
      }
    }
  }
});

test('explicit No objective choices on clip and bookmark fields remain untagged when another objective is focused', async () => {
  for (const mode of ['clip', 'bookmark']) {
    const f = fixture(); const video = f.$('vp-video');
    f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const picker = f.$(mode === 'clip' ? 'vp-clip-obj' : 'vp-bm-obj');
    picker.value = ''; await f.emit('change', picker);
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note'); note.value = 'General lesson';
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 1);
    assert.equal('objectiveId' in f.writes[0].args.payload, false);
    assert.equal('promptId' in f.writes[0].args.payload, false);
  }
});

test('review note Enter inserts a newline; Ctrl/Command+Enter saves once and reveals Notes without changing picker semantics', async () => {
  const f = fixture(); const note = f.$('vp-bm-note');
  note.value = '  First line\nSecond line  '; f.$('vp-bm-obj').value = 'obj:11'; f.hooks.setOverrides();
  const enter = await f.emit('keydown', note, { key: 'Enter' });
  assert.equal(enter.defaultPrevented, undefined); assert.equal(f.writes.length, 0);
  await f.emit('keydown', note, { key: 'Enter', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.writes[0], { command: 'add_bookmark', args: { payload: { gameId: 42, timeS: 0, note: 'First line\nSecond line', objectiveId: 11 } } });
  assert.equal(note.value, ''); assert.equal(f.$('vp-saved-moments').open, true); assert.equal(f.hooks.filter, 'bm');
  note.value = 'Command shortcut';
  await f.emit('keydown', note, { key: 'Enter', metaKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 2);
  await f.emit('keydown', note, { key: 'Enter', ctrlKey: true, repeat: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 2);
});

test('a same-game link opens the no-recording player once and preserves raw note/clip drafts and controls', async () => {
  let resolveRead;
  const f = fixture({ noVod: true, invokeReply: () => new Promise(resolve => { resolveRead = resolve; }) });
  f.$('vp-bm-note').value = '  Review note\nnot saved  ';
  f.$('vp-clip-note').value = '  Clip draft  ';
  f.$('vp-video').muted = true; f.$('vp-video').volume = 0.3; f.$('vp-video').playbackRate = 1.5;
  f.transport.setStep(10);
  await f.linked(9); await f.linked(0); await f.linked('invalid');
  assert.equal(f.reads.length, 0);
  const pending = f.linked(42);
  await new Promise(resolve => setImmediate(resolve));
  await f.linked(42);
  assert.equal(f.reads.length, 1, 'duplicate links share the pending read');
  f.$('vp-bm-note').value += ' edited during fetch';
  resolveRead({ ...f.snapshot, hasVod: true, filePath: 'new-match.mp4' });
  await pending;
  assert.equal(f.$('vp-video').loads, 1);
  assert.equal(f.$('vp-novod').hidden, true); assert.equal(f.$('vp-wrap').hidden, false);
  assert.equal(f.$('vp-bm-note').value, '  Review note\nnot saved   edited during fetch');
  assert.equal(f.$('vp-clip-note').value, '  Clip draft  ');
  assert.equal(f.$('vp-video').playbackRate, 1.5); assert.equal(f.$('vp-video').muted, true);
  assert.equal(f.$('vp-video').volume, 0.3); assert.equal(f.transport.stepSeconds, 10);
  assert.equal(f.writes.length, 0);
  await f.linked(42);
  assert.equal(f.reads.length, 1); assert.equal(f.$('vp-video').loads, 1);
});

test('recording links wait for initial hydration and an older metadata response cannot undo the link', async () => {
  const pendingReads = [];
  const f = fixture({ noVod: true, initialLoading: true,
    invokeReply: () => new Promise(resolve => pendingReads.push(resolve)) });
  await f.linked(42);
  assert.equal(f.reads.length, 0);
  f.hooks.render(f.snapshot, f.core);
  f.hooks.setInitialLoading(false);
  const oldMetadata = f.hooks.reloadBookmarks();
  const linked = f.hooks.refreshLinkedRecording();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(pendingReads.length, 2);
  pendingReads[1]({ ...f.snapshot, hasVod: true, filePath: 'hydrated.mp4' });
  await linked;
  pendingReads[0]({ ...f.snapshot, hasVod: false, filePath: '' });
  await oldMetadata;
  assert.equal(f.hooks.captureVodViewState().filePath, 'hydrated.mp4');
  assert.equal(f.$('vp-video').loads, 1); assert.equal(f.$('vp-novod').hidden, true);
});

test('a link never resets active playback and a mismatched response cannot open another game', async () => {
  const active = fixture();
  const v = active.$('vp-video'); Object.assign(v, { currentSrc: 'revu-media://playing', paused: false, currentTime: 57.25, playbackRate: 2, muted: true });
  await active.linked(42);
  assert.equal(active.reads.length, 0); assert.equal(v.loads, undefined);
  assert.equal(v.currentTime, 57.25); assert.equal(v.playbackRate, 2); assert.equal(v.paused, false);
  const missing = fixture({ noVod: true, invokeReply: async () => ({ gameId: 99, hasVod: true, filePath: 'other.mp4' }) });
  await missing.linked(42);
  assert.equal(missing.$('vp-video').loads, undefined);
  assert.equal(missing.$('vp-novod').hidden, false);
  assert.equal(missing.hooks.captureVodViewState().gameId, 42);
});
