import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createMatchNavigation, createMatchViewStore } from '../ui/match-navigation.mjs';

// Execute the real page handlers with a small form fixture. Electron smoke owns
// template/layout coverage; these cases exercise raw draft and write lifecycles.
const source = (await readFile(new URL('../ui/review.js', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '');
const plain = value => JSON.parse(JSON.stringify(value));
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

function node({ id = '', classes = [], dataset = {}, value = '' } = {}) {
  const names = new Set(classes);
  return {
    id, dataset, value, style: {}, textContent: '', hidden: false, checked: false,
    disabled: false, scrollHeight: 28, children: [], listeners: new Map(), selectors: new Map(),
    get className() { return [...names].join(' '); },
    set className(value) { names.clear(); String(value).split(/\s+/).filter(Boolean).forEach(name => names.add(name)); },
    classList: {
      contains: name => names.has(name),
      add: (...values) => values.forEach(name => names.add(name)),
      remove: (...values) => values.forEach(name => names.delete(name)),
      toggle(name, force) {
        const on = force ?? !names.has(name);
        if (on) names.add(name); else names.delete(name);
        return on;
      },
    },
    addEventListener(type, fn) {
      if (!this.listeners.has(type)) this.listeners.set(type, []);
      this.listeners.get(type).push(fn);
    },
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
    querySelectorAll(selector) {
      return this.selectors.get(selector) || this.children.flatMap(child => [
        ...(child.matches(selector) ? [child] : []),
        ...child.querySelectorAll(selector),
      ]);
    },
    appendChild(child) { this.children.push(child); child.parent = this; },
    append(...children) { children.forEach(child => this.appendChild(child)); },
    get childElementCount() { return this.children.length; },
    get selectedOptions() { return this.children.filter(option => option.value === this.value); },
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); },
    matches(selector) {
      return selector.split(',').some(part => {
        part = part.trim();
        const segments = part.split(/\s+/);
        if (segments.length > 1) return this.matches(segments.pop()) && !!this.parent?.closest(segments.join(' '));
        if (part.startsWith('.')) return names.has(part.slice(1));
        if (part.startsWith('[data-') && part.endsWith(']')) {
          const key = part.slice(6, -1).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
          return this.dataset[key] != null;
        }
        return this.tagName?.toLowerCase() === part;
      });
    },
    closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector) || null; },
    setAttribute(name, value) { this[name] = String(value); },
    getAttribute(name) { return this[name] ?? null; },
    removeAttribute(name) { delete this[name]; },
  };
}

function fixture(t, invoke = null, { realNavigation = false, invokeError = null } = {}) {
  const ids = new Map(), listeners = new Map(), windowListeners = new Map(), calls = [];
  const $ = id => {
    if (id === 'rv-champ') return null; // removed from the launchpad header
    if (!ids.has(id)) ids.set(id, node({ id }));
    return ids.get(id);
  };
  const fields = ['attribution', 'reviewNotes', 'outsideControl', 'withinControl']
    .map(field => node({ classes: ['rv-field-in'], dataset: { field } }));
  const mental = $('rv-mental-input'); mental.value = '5'; mental.dataset.touched = '';
  const tag = node({ dataset: { tagId: '7', color: '#aaa' } });
  const focus = [0, 1, 2].map(value => node({ dataset: { focus: String(value) } }));
  const checkbox = node(), note = node(), prompt = node({ classes: ['rv-prompt-input'],
    dataset: { promptId: '31', savedValue: 'Confirmed answer' }, value: 'Confirmed answer' });
  const objective = node({ dataset: { objectiveId: '9' } });
  objective.selectors = new Map([
    ['.rv-practiced-cb', [checkbox]], ['.rv-objnote', [note]],
    ['.rv-switch-lbl', [node()]], ['.rv-prompt-input', [prompt]],
  ]);
  const selectors = new Map([
    ['#rv-fields .rv-field-in', fields], ['#rv-tagcat-grid .rv-tagcat-chip', [tag]],
    ['#rv-focus .rv-focus-btn', focus], ['#rv-objectives [data-objective-id]', [objective]],
    ['.rv-prompt-input', [prompt]], ['#statusline b', [node()]],
  ]);
  const document = {
    readyState: 'loading', activeElement: null,
    getElementById: id => id === 'match-navigation' ? null
      : $('rv-fields').children.find(child => child.id === id) || $(id),
    createElement: tag => Object.assign(node(), { tagName: tag.toUpperCase() }),
    addEventListener(type, fn) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(fn);
    },
    querySelectorAll(selector) {
      if (selector === '#rv-fields .rv-field-in' && $('rv-fields').children.length) return $('rv-fields').querySelectorAll('.rv-field-in');
      if (selector === 'details[id]') return $('rv-fields').children.filter(child => child.tagName === 'DETAILS');
      if (selector === '#rv-tags .rv-tag-txt') return $('rv-tags').querySelectorAll('.rv-tag-txt');
      if (selector === '#rv-tagcat-grid .rv-tagcat-chip.on') return tag.classList.contains('on') ? [tag] : [];
      if (selector === '#rv-focus .rv-focus-btn.on') return focus.filter(button => button.classList.contains('on'));
      return selectors.get(selector) || [];
    },
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
  };
  let nav = { enabled: 0, invalidations: 0, navigations: [], setGame() {}, enableCapture() { this.enabled++; },
    invalidate() { this.invalidations++; }, restoreState: async () => {},
    async navigate(target) {
      this.navigations.push(target); this.captured = this.options.capture();
      await this.options.beforeLeave?.(); return true;
    } };
  const scope = { location: { search: '?gameId=42', href: 'revu-app://ui/review.html?gameId=42' },
    addEventListener(type, callback) { windowListeners.set(type, callback); }, dispatchEvent() {}, scrollTo() {}, confirm: () => true };
  const store = createMatchViewStore();
  const context = vm.createContext({
    document, window: scope,
    $, show: (element, on) => { if (element) element.hidden = !on; }, clear: element => { element.children = []; },
    tpl: name => {
      if (name === 'tpl-field') {
        const field = node(); field.selectors = new Map([['.rv-field-in', [node({ classes: ['rv-field-in'] })]], ['.rv-field-k', [node()]]]);
        return field;
      }
      if (name === 'tpl-clip') {
        const row = node({ classes: ['rv-evid'] });
        for (const name of ['rv-evid-dot', 'rv-evid-time', 'rv-clip-note']) row.appendChild(node({ classes: [name] }));
        const actions = node({ classes: ['rv-evid-actions'] });
        row.appendChild(actions);
        for (const action of ['good', 'bad', 'dismiss', 'objective']) {
          const control = node({ classes: [`rv-evid-${action === 'objective' ? 'pick' : action}`], dataset: { evidAction: action } });
          control.tagName = action === 'objective' ? 'SELECT' : 'BUTTON';
          actions.appendChild(control);
        }
        const status = node({ classes: ['rv-evid-save-status'] }); status.hidden = true;
        row.appendChild(status);
        return row;
      }
      if (name === 'tpl-death') {
        const row = node(), number = node(), time = node();
        const jump = node({ classes: ['rv-death-jump'], dataset: { action: 'view_moment' } });
        jump.disabled = true;
        row.selectors = new Map([
          ['.rv-death-number', [number]], ['.rv-death-time', [time]], ['.rv-death-jump', [jump]],
        ]);
        return row;
      }
      const chip = node(); chip.selectors = new Map([['.rv-tag-txt', [node()]], ['.rv-tag-x', [node()]]]);
      return chip;
    },
    createMatchNavigation: options => {
      if (realNavigation) nav = createMatchNavigation({ ...options, document, scope, store });
      else nav.options = options;
      return nav;
    },
    getInvoke: async () => {
      if (invokeError) throw invokeError;
      return invoke ? async (...args) => { calls.push(plain(args)); return invoke(...args); } : null;
    },
    readSnapshot: async () => ({ subject: null }),
    setTimeout, clearTimeout, URLSearchParams, CustomEvent: class {},
    console: { info() {}, warn() {}, error() {} },
  });
  vm.runInContext(`${source}\n globalThis.hooks = { captureReviewState, restoreReviewState, savePromptAnswer, renderDeaths,
    render, renderHeader, renderUnsorted, pruneEmptyClipSections, clipCard, onEvidenceAction, flushEvidenceWrites,
    flushDraft, gatherForm, cancelDraft, markDraftDirty, setSubject(value) { _subject = value; },
    get dirty() { return _draftDirty; } };`, context, { filename: 'review.js' });
  const hooks = context.hooks;
  hooks.setSubject({ gameId: 42, header: { championName: 'Ahri', win: true }, form: { wentWell: 'Legacy saved note' } });
  if (realNavigation) nav.setGame(42);
  t.after(() => hooks.cancelDraft());
  return { $, fields, mental, tag, focus, checkbox, note, prompt, objective, nav, hooks, calls, scope, store,
    linked: gameId => windowListeners.get('revu:vod-linked')({ detail: { gameId } }),
    addTag(value) {
      const chip = node(), text = node(); text.textContent = value;
      chip.selectors.set('.rv-tag-txt', [text]); $('rv-tags').appendChild(chip);
    },
    async emit(type, target) {
      for (const fn of listeners.get(type) || []) await fn({ target, preventDefault() {} });
    },
  };
}

test('a matching recording link updates the Review launch action without writes or draft replacement', async t => {
  const f = fixture(t, () => {});
  const button = f.$('rv-open-vod'); button.firstChild = { nodeType: 3, textContent: 'Review in VOD ' };
  f.fields[1].value = '  Raw takeaway  ';
  f.prompt.value = 'Unconfirmed answer'; f.$('rv-tag-input').value = 'unfinished tag';
  const before = plain(f.hooks.captureReviewState());
  f.linked(99); f.linked(0); f.linked('invalid');
  assert.equal(button.firstChild.textContent, 'Review in VOD ');
  f.linked(42);
  assert.equal(button.firstChild.textContent, 'Review linked recording ');
  assert.equal(button.dataset.recordingLinked, 'true');
  assert.deepEqual(plain(f.hooks.captureReviewState()), before);
  assert.deepEqual(f.calls, []);
  f.hooks.renderHeader({ gameId: 99, header: { championName: 'Jinx' } });
  assert.equal(button.dataset.recordingLinked, 'false');
  assert.equal(button.firstChild.textContent, 'Review in VOD ');
});

test('Review resumes raw same-game drafts and selections without sending writes', async t => {
  const f = fixture(t, () => {});
  f.fields[1].value = '  Keep this spacing\nnext time  ';
  f.mental.value = '8'; f.mental.dataset.touched = '1';
  f.addTag('Wave control'); f.$('rv-tag-input').value = '  unfinished tag  ';
  f.tag.classList.add('on'); f.focus[1].classList.add('on');
  f.checkbox.checked = true; f.note.value = '  raw execution note  ';
  f.prompt.value = '  Unsaved answer\n';
  const saved = plain(f.hooks.captureReviewState());
  assert.doesNotThrow(() => JSON.stringify(saved));
  f.fields[1].value = ''; f.mental.value = '5'; f.mental.dataset.touched = '';
  f.$('rv-tags').children = []; f.$('rv-tag-input').value = '';
  f.tag.classList.remove('on'); f.focus[1].classList.remove('on');
  f.checkbox.checked = false; f.note.value = ''; f.prompt.value = 'Confirmed answer';
  await f.hooks.restoreReviewState(saved);
  assert.equal(f.fields[1].value, '  Keep this spacing\nnext time  ');
  assert.equal(f.mental.value, '8'); assert.equal(f.mental.dataset.touched, '1');
  assert.equal(f.$('rv-tag-input').value, '  unfinished tag  ');
  assert.equal(f.tag.classList.contains('on'), true); assert.equal(f.focus[1].classList.contains('on'), true);
  assert.equal(f.checkbox.checked, true); assert.equal(f.note.value, '  raw execution note  ');
  assert.equal(f.note.hidden, false); assert.equal(f.prompt.value, '  Unsaved answer\n');
  assert.equal(f.prompt.dataset.savedValue, 'Confirmed answer', 'raw restoration must not mark a prompt saved');
  assert.deepEqual(f.calls, []);
  assert.equal(f.hooks.dirty, true);
  const payload = plain(f.hooks.gatherForm());
  assert.equal(payload.reviewNotes, 'Keep this spacing\nnext time');
  assert.deepEqual(payload.freeTextTags, ['Wave control']);
  assert.equal(payload.wentWell, 'Legacy saved note', 'unrendered saved fields stay intact');
});

test('loaded launchpad renders one matchup title and concise context without querying recording availability', t => {
  const f = fixture(t, () => { throw new Error('Rendering must not fetch recording availability'); });
  f.hooks.render({ subject: { gameId: 42, header: {
    championName: 'Jinx', enemyChampion: 'Caitlyn', resultText: 'VICTORY', gameMode: 'Ranked Solo/Duo',
    duration: '32:11', datePlayed: 'Sep 12, 14:08', kdaText: '8 / 2 / 11', kdaRatioText: '(9.50)',
    matchupHeading: 'Jinx + Nautilus vs Caitlyn + Lux', hasReview: true, timelineFixes: 2,
    lobbyMatchups: ['TOP', 'JG', 'MID', 'BOT', 'SUP'].map(roleLabel => ({ roleLabel, own: 'Jinx', enemy: 'Caitlyn', isUserLane: true })),
  }, form: { mentalRating: 8, reviewNotes: 'Wait for the wave', attribution: 'Review my position' }, objectives: [] } });
  assert.equal(f.$('hero-title').textContent, 'Jinx vs Caitlyn');
  assert.equal(f.$('rv-res').textContent, 'Victory');
  assert.equal(f.$('rv-date').textContent, 'Sep 12, 14:08');
  assert.equal(f.$('rv-gmode').textContent, 'Ranked Solo/Duo'); assert.equal(f.$('rv-gdur').textContent, '32:11');
  assert.equal(f.$('rv-kda').textContent, '8 / 2 / 11'); assert.equal(f.$('rv-reviewed').hidden, false);
  assert.equal(f.$('rv-fixes-n').textContent, '2');
  assert.deepEqual(f.$('rv-lobby').children.map(cell => cell.children[0].textContent), ['Top', 'Jungle', 'Mid', 'Bot', 'Support']);
  assert.equal(f.$('statusline').hidden, true); assert.equal(f.$('rv-body').hidden, false);
  assert.equal(f.$('rv-open-vod').disabled, false, 'unknown recording availability does not block the existing VOD empty state');
  assert.equal(f.$('rv-objsec').hidden, false, 'mental/focus controls remain available without objective rows');
  assert.equal(f.mental.value, '8');
  assert.equal(f.$('rv-fields').children[0].querySelector('.rv-field-k').textContent, 'One thing to take into your next game');
  assert.equal(f.$('rv-fields').children[0].querySelector('.rv-field-in').value, 'Wait for the wave');
  assert.equal(f.$('rv-fields').children[1].open, false, 'saved answers do not expand the optional reflection');
  assert.equal(f.$('rv-fields').children[1].children[0].children[0].textContent, '1 answer');
  assert.equal(f.hooks.gatherForm().attribution, 'Review my position', 'collapsed fields still participate in saving');
  assert.deepEqual(f.calls, []);

  f.hooks.renderHeader({ header: { championName: "Kai'Sa", resultText: 'DEFEAT' } });
  assert.equal(f.$('hero-title').textContent, "Kai'Sa"); assert.equal(f.$('rv-res').textContent, 'Defeat');
  assert.equal(f.$('rv-date').textContent, '');
  f.hooks.renderHeader({}); assert.equal(f.$('hero-title').textContent, 'Match review');
  f.hooks.render({ subject: null });
  assert.equal(f.$('statusline').hidden, false); assert.equal(f.$('rv-body').hidden, true);
});

test('reflection counts current answers without opening or normalizing fields and restores the expanded raw draft', async t => {
  const f = fixture(t, null, { realNavigation: true });
  const snapshot = { subject: { gameId: 42, header: {}, form: {
    attribution: 'My play', outsideControl: '  ', withinControl: 'Positioning',
  } } };
  f.hooks.render(snapshot);
  let reflection = f.$('rv-fields').children[1];
  assert.equal(reflection.open, false);
  assert.equal(reflection.children[0].children[0].textContent, '2 answers');
  const gathered = plain(f.hooks.gatherForm());
  assert.equal(gathered.attribution, 'My play'); assert.equal(gathered.withinControl, 'Positioning');
  assert.equal(gathered.outsideControl, '');
  const outside = reflection.querySelectorAll('.rv-field-in').find(input => input.dataset.field === 'outsideControl');
  outside.value = '  Another answer\n  ';
  await f.emit('input', outside);
  assert.equal(reflection.children[0].children[0].textContent, '3 answers');
  assert.equal(reflection.open, false, 'count updates must not expand the optional fields');
  assert.equal(outside.value, '  Another answer\n  ');
  assert.deepEqual(f.calls, [], 'count refresh sends no immediate write');
  reflection.open = true;
  const field = reflection.querySelectorAll('.rv-field-in').find(input => input.dataset.field === 'attribution');
  field.value = '  More reflection\nwith raw spacing  ';
  f.nav.captureState();
  f.hooks.render(snapshot);
  reflection = f.$('rv-fields').children[1];
  assert.equal(reflection.open, false);
  f.scope.location.search = '?gameId=42&resume=1';
  assert.equal(await f.nav.restoreState(), true);
  assert.equal(reflection.open, true);
  assert.equal(reflection.children[0].children[0].textContent, '3 answers', 'restored raw fields replace the stale snapshot count');
  assert.equal(reflection.querySelectorAll('.rv-field-in').find(input => input.dataset.field === 'attribution').value,
    '  More reflection\nwith raw spacing  ');
  assert.deepEqual(f.calls, []);
  f.hooks.render({ subject: { gameId: 42, header: {}, form: {} } });
  reflection = f.$('rv-fields').children[1];
  assert.equal(reflection.open, false); assert.equal(reflection.children[0].children[0].textContent, 'Optional');
});

test('unsorted clip count follows rendering and dismissal without changing the user disclosure choice', t => {
  const f = fixture(t);
  f.$('rv-tosortsec').open = false;
  f.hooks.renderUnsorted({ unsortedClips: [{ evidenceId: 5, startSeconds: 0 }, { evidenceId: 6, startSeconds: 80 }] });
  assert.equal(f.$('rv-unsorted-count').textContent, '2 clips');
  assert.equal(f.$('rv-tosortsec').hidden, false); assert.equal(f.$('rv-tosortsec').open, false);
  f.$('rv-tosortsec').open = true;
  f.$('rv-tosort').children[0].remove(); f.hooks.pruneEmptyClipSections();
  assert.equal(f.$('rv-unsorted-count').textContent, '1 clip'); assert.equal(f.$('rv-tosortsec').open, true);
  f.hooks.renderUnsorted({ unsortedClips: [] });
  assert.equal(f.$('rv-unsorted-count').textContent, '0 clips'); assert.equal(f.$('rv-tosortsec').hidden, true);
});

const evidenceClip = (overrides = {}) => ({
  evidenceId: 301, startSeconds: 864, timeText: '14:24–15:04', note: 'Move after pushing the wave',
  polarity: 'neutral', objectiveId: 9, objectiveTitle: 'Tempo', ...overrides,
});
const evidenceOptions = [{ id: 9, title: 'Tempo' }, { id: 12, title: 'Wave control' }];
function evidenceControls(row) {
  return {
    good: row.querySelector('.rv-evid-good'), bad: row.querySelector('.rv-evid-bad'),
    pick: row.querySelector('.rv-evid-pick'), dot: row.querySelector('.rv-evid-dot'),
  };
}
function assertPolarity(row, polarity) {
  const { good, bad, dot } = evidenceControls(row);
  assert.equal(good.classList.contains('on'), polarity === 'good');
  assert.equal(bad.classList.contains('on'), polarity === 'bad');
  assert.equal(good.getAttribute('aria-pressed'), String(polarity === 'good'));
  assert.equal(bad.getAttribute('aria-pressed'), String(polarity === 'bad'));
  assert.match(dot.title, new RegExp(polarity, 'i'));
}

test('saved Good evidence hydrates the rating and its actual Tempo attachment without writes', t => {
  const f = fixture(t, () => { throw new Error('Hydrating evidence must not write'); });
  const row = f.hooks.clipCard(evidenceClip({ polarity: 'good' }), evidenceOptions);
  assertPolarity(row, 'good');
  const pick = evidenceControls(row).pick;
  assert.equal(pick.value, '9');
  assert.equal(pick.children.find(option => option.value === pick.value).textContent, 'Tempo');

  const archived = f.hooks.clipCard(evidenceClip({ objectiveId: 77, objectiveTitle: 'Earlier objective' }), evidenceOptions);
  const archivedPick = evidenceControls(archived).pick;
  assert.equal(archivedPick.value, '77', 'an attached inactive objective must not look unassigned');
  assert.equal(archivedPick.children.find(option => option.value === '77').textContent, 'Earlier objective');
  assert.deepEqual(f.calls, []);
});

test('evidence rating stays confirmed while saving and updates its dot and buttons together on success', async t => {
  const started = deferred(), saved = deferred();
  const f = fixture(t, async command => {
    if (command === 'set_evidence_polarity') { started.resolve(); return saved.promise; }
  });
  f.fields[1].value = 'Keep this unsaved takeaway';
  const row = f.hooks.clipCard(evidenceClip(), evidenceOptions);
  const { good, bad, pick, dot } = evidenceControls(row);
  const neutralColor = dot.style.background;
  const change = f.hooks.onEvidenceAction('good', good);
  await started.promise;
  assertPolarity(row, 'neutral');
  assert.equal(dot.style.background, neutralColor);
  assert.equal(good.disabled, true); assert.equal(bad.disabled, true); assert.equal(pick.disabled, true);
  const duplicate = f.hooks.onEvidenceAction('good', good);
  assert.equal(f.calls.length, 1, 'a repeated click must not enqueue a second toggle');
  saved.resolve({ ok: true }); await Promise.all([change, duplicate]);
  assertPolarity(row, 'good');
  assert.notEqual(dot.style.background, neutralColor);
  assert.equal(good.disabled, false); assert.equal(bad.disabled, false); assert.equal(pick.disabled, false);
  assert.equal(f.fields[1].value, 'Keep this unsaved takeaway', 'rating evidence must not re-render the review form');
  assert.deepEqual(f.calls, [['set_evidence_polarity', { payload: { evidenceId: 301, polarity: 'good' } }]]);
});

test('a rejected evidence rating keeps the saved Good state and can be retried successfully', async t => {
  let rejectWrite = true;
  const f = fixture(t, () => {
    if (rejectWrite) throw new Error('Unable to save evidence');
    return { ok: true };
  });
  const row = f.hooks.clipCard(evidenceClip({ polarity: 'good' }), evidenceOptions);
  const { bad, dot } = evidenceControls(row);
  const goodColor = dot.style.background;
  await f.hooks.onEvidenceAction('bad', bad);
  assertPolarity(row, 'good');
  assert.equal(dot.style.background, goodColor);
  assert.equal(bad.disabled, false);
  const status = row.querySelector('.rv-evid-save-status');
  assert.ok(status && !status.hidden, 'the affected row must explain that the write failed');
  assert.match(status.textContent, /save|fail|try|retry/i);

  rejectWrite = false;
  await f.hooks.onEvidenceAction('bad', bad);
  assertPolarity(row, 'bad');
  assert.notEqual(dot.style.background, goodColor);
  assert.equal(f.calls.length, 2);
});

test('an explicit unsuccessful evidence response does not appear saved', async t => {
  const f = fixture(t, () => ({ ok: false, message: 'The evidence item no longer exists' }));
  const row = f.hooks.clipCard(evidenceClip(), evidenceOptions);
  await f.hooks.onEvidenceAction('good', evidenceControls(row).good);
  assertPolarity(row, 'neutral');
  const status = row.querySelector('.rv-evid-save-status');
  assert.ok(status && !status.hidden);
  assert.match(status.textContent, /save|fail|try|retry/i);
  assert.equal(evidenceControls(row).good.disabled, false);
});

test('objective reassignment restores Tempo after failure and preserves a later confirmed selection', async t => {
  let rejectWrite = true;
  const f = fixture(t, () => {
    if (rejectWrite) throw new Error('Objective update failed');
    return { ok: true };
  });
  const row = f.hooks.clipCard(evidenceClip({ polarity: 'good' }), evidenceOptions);
  const { pick } = evidenceControls(row);
  pick.value = '12'; await f.hooks.onEvidenceAction('objective', pick);
  assert.equal(pick.value, '9', 'the picker must return to the confirmed Tempo attachment');
  assertPolarity(row, 'good');
  assert.equal(pick.disabled, false);

  rejectWrite = false;
  pick.value = '12'; await f.hooks.onEvidenceAction('objective', pick);
  assert.equal(pick.value, '12');
  rejectWrite = true;
  pick.value = ''; await f.hooks.onEvidenceAction('objective', pick);
  assert.equal(pick.value, '12', 'failed detachment restores the latest confirmed attachment');
  assert.deepEqual(f.calls.map(([, { payload }]) => payload), [
    { evidenceId: 301, objectiveId: 12, gameId: 42 },
    { evidenceId: 301, objectiveId: 12, gameId: 42 },
    { evidenceId: 301, objectiveId: null, gameId: 42 },
  ]);
});

test('different evidence rows save independently while navigation waits for both writes', async t => {
  const startedA = deferred(), startedB = deferred(), savedA = deferred(), savedB = deferred();
  const f = fixture(t, async (command, { payload }) => {
    if (command !== 'set_evidence_polarity') return;
    if (payload.evidenceId === 301) { startedA.resolve(); return savedA.promise; }
    startedB.resolve(); return savedB.promise;
  }, { realNavigation: true });
  const rowA = f.hooks.clipCard(evidenceClip(), evidenceOptions);
  const rowB = f.hooks.clipCard(evidenceClip({ evidenceId: 302 }), evidenceOptions);
  const saveA = f.hooks.onEvidenceAction('good', evidenceControls(rowA).good);
  const saveB = f.hooks.onEvidenceAction('bad', evidenceControls(rowB).bad);
  await Promise.all([startedA.promise, startedB.promise]);
  let navigated = false;
  const launch = f.emit('click', node({ dataset: { action: 'review_vod' } })).then(() => { navigated = true; });
  let flushed = false;
  const flushing = f.hooks.flushEvidenceWrites().then(() => { flushed = true; });
  savedA.resolve({ ok: true }); await saveA;
  assert.equal(flushed, false); assert.equal(navigated, false);
  assert.equal(new URL(f.scope.location.href).pathname, '/review.html');
  assertPolarity(rowA, 'good'); assertPolarity(rowB, 'neutral');
  savedB.resolve({ ok: true }); await Promise.all([saveB, flushing, launch]);
  assert.equal(flushed, true); assert.equal(navigated, true);
  assert.equal(new URL(f.scope.location.href).pathname, '/vodplayer.html');
  assertPolarity(rowB, 'bad');
});

test('Save review waits for evidence confirmation before committing and reloading', async t => {
  const started = deferred(), saved = deferred();
  const f = fixture(t, async command => {
    if (command === 'set_evidence_polarity') { started.resolve(); return saved.promise; }
    return { ok: true };
  });
  const row = f.hooks.clipCard(evidenceClip(), evidenceOptions);
  const change = f.hooks.onEvidenceAction('good', evidenceControls(row).good);
  await started.promise;
  const commit = f.emit('click', node({ dataset: { action: 'save_review' } }));
  await Promise.resolve(); await Promise.resolve();
  assert.deepEqual(f.calls.map(([command]) => command), ['set_evidence_polarity']);
  saved.resolve({ ok: true }); await Promise.all([change, commit]);
  assert.deepEqual(f.calls.map(([command]) => command), ['set_evidence_polarity', 'save_review']);
  assertPolarity(row, 'good');
  assert.equal(f.nav.invalidations, 1);
});

test('failed in-flight evidence leaves the review open and prevents a misleading successful commit', async t => {
  for (const action of ['review_vod', 'save_review']) {
    const started = deferred(), saved = deferred();
    const f = fixture(t, async command => {
      if (command === 'set_evidence_polarity') { started.resolve(); return saved.promise; }
      return { ok: true };
    }, { realNavigation: true });
    const row = f.hooks.clipCard(evidenceClip(), evidenceOptions);
    const change = f.hooks.onEvidenceAction('good', evidenceControls(row).good);
    await started.promise;
    const leaving = f.emit('click', node({ dataset: { action } }));
    await Promise.resolve(); await Promise.resolve();
    saved.resolve({ ok: false, message: 'Evidence could not be saved' });
    await Promise.all([change, leaving]);
    assert.equal(new URL(f.scope.location.href).pathname, '/review.html', action);
    assert.deepEqual(f.calls.map(([command]) => command), ['set_evidence_polarity'], action);
    assertPolarity(row, 'neutral');
    assert.equal(f.$('rv-savebtn').disabled, false);
  }
});

test('primary VOD launch resumes the same match after flushing drafts and retains its remembered viewing state', async t => {
  const started = deferred(), saved = deferred();
  const f = fixture(t, async command => {
    if (command === 'save_review_draft') { started.resolve(); await saved.promise; }
  }, { realNavigation: true });
  const rememberedVod = { version: 1, gameId: 42, state: { schema: 1, gameId: 42, mediaTime: 123.375, rate: 1.5 } };
  f.store.save(42, 'vod', rememberedVod);
  f.fields[1].value = '  Keep the review draft  '; f.$('rv-tag-input').value = 'Unfinished tag';
  f.hooks.markDraftDirty();
  const launch = f.emit('click', node({ id: 'rv-open-vod', dataset: { action: 'review_vod' } }));
  await started.promise;
  assert.equal(new URL(f.scope.location.href).pathname, '/review.html', 'navigation waits for the active draft write');
  saved.resolve(); await launch;
  const destination = new URL(f.scope.location.href);
  assert.equal(destination.pathname, '/vodplayer.html'); assert.equal(destination.searchParams.get('gameId'), '42');
  assert.equal(destination.searchParams.get('resume'), '1');
  assert.equal(destination.searchParams.has('t'), false); assert.equal(destination.searchParams.has('clip'), false);
  assert.equal(f.store.peek(42, 'review').state.fields[1].value, '  Keep the review draft  ');
  assert.equal(f.store.peek(42, 'review').state.pendingTag, 'Unfinished tag');
  assert.deepEqual(f.store.peek(42, 'vod'), rememberedVod);
  assert.deepEqual(f.calls.map(([command]) => command), ['save_review_draft']);
});

test('death rows number each timestamp and keep ten seconds of context, including death at game start', async t => {
  const f = fixture(t);
  f.fields[1].value = '  Keep my draft while watching  ';
  f.hooks.renderDeaths({ deaths: [
    { gameTimeSeconds: 0, timeText: 'Old label' },
    { gameTimeSeconds: 7.5 },
    { gameTimeSeconds: 122.75 },
  ] });
  const rows = f.$('rv-deaths').children;
  assert.equal(f.$('rv-deathsec').hidden, false);
  assert.equal(f.$('rv-death-count').textContent, '3 deaths');
  assert.deepEqual(rows.map(row => row.querySelector('.rv-death-number').textContent), ['Death 1', 'Death 2', 'Death 3']);
  assert.deepEqual(rows.map(row => row.querySelector('.rv-death-time').textContent), ['00:00', '00:07', '02:02']);
  const buttons = rows.map(row => row.querySelector('.rv-death-jump'));
  assert.deepEqual(buttons.map(button => button.disabled), [false, false, false]);
  assert.deepEqual(buttons.map(button => button.dataset.seek), ['0', '0', '112.75']);
  assert.equal(buttons[0]['aria-label'], 'Watch death 1 at 00:00');
  assert.equal(buttons[2]['aria-label'], 'Watch death 3 at 02:02');
  for (const button of buttons) await f.emit('click', button);
  assert.deepEqual(f.nav.navigations, [
    'vodplayer.html?gameId=42&t=0', 'vodplayer.html?gameId=42&t=0', 'vodplayer.html?gameId=42&t=112.75',
  ]);
  assert.equal(f.nav.captured.fields[1].value, '  Keep my draft while watching  ');
  assert.equal(f.calls.length, 0, 'watching a death performs no classification or correction writes');
});

test('missing or invalid death times retain a disabled row and never fabricate a watch target', t => {
  const f = fixture(t);
  f.hooks.renderDeaths({ deaths: [null, undefined, '', false, -1, NaN, Infinity]
    .map(gameTimeSeconds => ({ gameTimeSeconds })) });
  const rows = f.$('rv-deaths').children;
  assert.equal(rows.length, 7);
  for (const row of rows) {
    const button = row.querySelector('.rv-death-jump');
    assert.equal(button.disabled, true);
    assert.equal(button.dataset.seek, undefined);
    assert.equal(row.querySelector('.rv-death-time').textContent, 'Time unavailable');
  }
  f.hooks.renderDeaths({ deaths: [] });
  assert.equal(f.$('rv-deaths').children.length, 0);
  assert.equal(f.$('rv-deathsec').hidden, true);
  assert.equal(f.$('rv-death-count').textContent, '0 deaths');
});

test('Review rejects another game and keeps an untouched mental slider unanswered', async t => {
  const f = fixture(t);
  const saved = plain(f.hooks.captureReviewState());
  saved.gameId = 99; saved.fields[0].value = 'Other game';
  await f.hooks.restoreReviewState(saved);
  assert.equal(f.fields[0].value, '');
  saved.gameId = 42;
  await f.hooks.restoreReviewState(saved);
  assert.equal(f.mental.dataset.touched, '');
  assert.equal(f.hooks.gatherForm().mentalRating, 0);
});

test('a blur confirmed during navigation keeps the fresh prompt baseline on return', async t => {
  const f = fixture(t, () => {});
  f.prompt.value = 'Answer sent while leaving';
  const saved = plain(f.hooks.captureReviewState());
  f.prompt.dataset.savedValue = f.prompt.value;
  await f.hooks.restoreReviewState(saved);
  await f.hooks.savePromptAnswer(f.prompt);
  assert.equal(f.prompt.dataset.savedValue, 'Answer sent while leaving');
  assert.equal(f.calls.length, 0, 'already-confirmed answers must not be written again');
});

test('failed prompt saves stay unsaved and retry after returning to Review', async t => {
  let attempts = 0;
  const f = fixture(t, () => { if (++attempts === 1) throw new Error('Temporarily offline'); });
  f.prompt.value = '  Retry this answer  ';
  await f.hooks.savePromptAnswer(f.prompt);
  assert.equal(f.prompt.dataset.savedValue, 'Confirmed answer');
  const saved = plain(f.hooks.captureReviewState());
  f.prompt.value = 'Confirmed answer';
  await f.hooks.restoreReviewState(saved);
  await f.hooks.savePromptAnswer(f.prompt);
  assert.equal(attempts, 2);
  assert.equal(f.prompt.dataset.savedValue, '  Retry this answer  ');
  assert.equal(f.calls[1][1].payload.text, '  Retry this answer  ');
});

test('prompt saves serialize and navigation also flushes edits made during an earlier write', async t => {
  const started = deferred(), first = deferred();
  const f = fixture(t, async (command, { payload }) => {
    if (command === 'save_prompt_answer' && payload.text === 'First answer') {
      started.resolve(); await first.promise;
    }
  });
  f.prompt.value = 'First answer';
  const one = f.hooks.savePromptAnswer(f.prompt);
  await started.promise;
  f.prompt.value = 'Latest answer';
  const two = f.hooks.savePromptAnswer(f.prompt);
  let flushed = false;
  const flushing = f.hooks.flushDraft().then(() => { flushed = true; });
  f.fields[1].value = 'Edited while waiting'; f.hooks.markDraftDirty();
  await Promise.resolve();
  assert.equal(flushed, false);
  first.resolve();
  await Promise.all([one, two, flushing]);
  assert.equal(f.prompt.dataset.savedValue, 'Latest answer');
  assert.deepEqual(f.calls.map(([command, { payload }]) => [command, payload.text ?? payload.reviewNotes]), [
    ['save_prompt_answer', 'First answer'], ['save_prompt_answer', 'Latest answer'],
    ['save_review_draft', 'Edited while waiting'],
  ]);
});

test('concurrent draft flushes wait for one writer and save the latest edit once', async t => {
  const started = deferred(), first = deferred();
  const f = fixture(t, async () => {
    if (f.calls.length === 1) { started.resolve(); await first.promise; }
  });
  f.fields[1].value = 'First draft'; f.hooks.markDraftDirty();
  const one = f.hooks.flushDraft(), two = f.hooks.flushDraft();
  await started.promise;
  f.fields[1].value = 'Second draft'; f.hooks.markDraftDirty();
  first.resolve();
  await Promise.all([one, two]);
  assert.deepEqual(f.calls.map(([, { payload }]) => payload.reviewNotes), ['First draft', 'Second draft']);
});

test('successful commit invalidates saved view state; a failed commit keeps it available', async t => {
  for (const action of ['save_review', 'skip_review', 'delete_review']) {
    for (const fails of [false, true]) {
      const f = fixture(t, command => { if (command === action && fails) throw new Error('Write failed'); });
      f.fields[1].value = 'My review';
      f.$('rv-skipbtn').dataset.confirm = '1';
      await f.emit('click', node({ dataset: { action } }));
      assert.equal(f.nav.invalidations, fails ? 0 : 1, `${action}: invalidation requires confirmed success`);
      const payload = f.calls.find(([command]) => command === action)[1].payload;
      assert.equal(payload.gameId, 42);
      if (action === 'save_review') assert.equal(payload.reviewNotes, 'My review');
      assert.equal(f.$('rv-savebtn').disabled, false);
    }
  }
});

test('Copy review waits for native acknowledgment, preserves exact markdown, and ignores duplicate clicks', async t => {
  const started = deferred(), copied = deferred();
  const markdown = '  # Match review\r\n\r\nTempo: move after pushing → then reset.\r\n';
  const f = fixture(t, async command => {
    if (command === 'get_review_export_markdown') return { found: true, markdown };
    if (command === 'copy_text_to_clipboard') { started.resolve(); return copied.promise; }
    throw new Error(`Unexpected command: ${command}`);
  });
  f.fields[1].value = 'Unsaved takeaway stays in the editor';
  f.hooks.markDraftDirty();
  f.$('rv-commit-msg').textContent = 'Existing review status';
  const button = node({ dataset: { action: 'copy_review' } });
  const copying = f.emit('click', button);
  await started.promise;
  assert.equal(button.disabled, true);
  assert.equal(f.$('rv-export-msg').textContent, 'Copying…');
  await f.emit('click', button);
  assert.deepEqual(f.calls, [
    ['get_review_export_markdown', { gameId: 42 }],
    ['copy_text_to_clipboard', { text: markdown }],
  ], 'copy uses the native bridge once and does not save or flush the review draft');
  copied.resolve({ ok: true }); await copying;
  assert.equal(button.disabled, false);
  assert.equal(f.$('rv-export-msg').textContent, 'Copied to clipboard.');
  assert.equal(f.$('rv-commit-msg').textContent, 'Existing review status', 'copy feedback belongs beside the export actions');
  assert.equal(f.fields[1].value, 'Unsaved takeaway stays in the editor');
  assert.equal(f.hooks.dirty, true);
  assert.equal(f.nav.invalidations, 0);
});

test('native clipboard failure never reports success and allows the user to retry', async t => {
  for (const failure of ['reject', 'not-ok', 'missing-acknowledgment']) {
    let attempt = 0;
    const f = fixture(t, command => {
      if (command === 'get_review_export_markdown') return { found: true, markdown: '# Saved review' };
      if (command === 'copy_text_to_clipboard') {
        if (++attempt > 1) return { ok: true };
        if (failure === 'reject') throw new Error('Clipboard is unavailable');
        return failure === 'not-ok' ? { ok: false } : undefined;
      }
      throw new Error(`Unexpected command: ${command}`);
    });
    const button = node({ dataset: { action: 'copy_review' } });
    await f.emit('click', button);
    assert.equal(button.disabled, false, failure);
    assert.equal(f.$('rv-export-msg').textContent, 'Copy failed. Please try again.', failure);
    await f.emit('click', button);
    assert.equal(attempt, 2, failure);
    assert.equal(button.disabled, false);
    assert.equal(f.$('rv-export-msg').textContent, 'Copied to clipboard.');
  }
});

test('Copy review rejects missing or blank exported text before calling the native clipboard', async t => {
  for (const built of [null, { found: false, markdown: '# Missing review' }, { found: true, markdown: 42 },
    { found: true, markdown: '' }, { found: true, markdown: ' \r\n\t ' }]) {
    const f = fixture(t, command => {
      assert.equal(command, 'get_review_export_markdown');
      return built;
    });
    const button = node({ dataset: { action: 'copy_review' } });
    await f.emit('click', button);
    assert.deepEqual(f.calls, [['get_review_export_markdown', { gameId: 42 }]]);
    assert.equal(button.disabled, false);
    assert.match(f.$('rv-export-msg').textContent, /could not|unavailable|failed|no.*review/i);
    assert.doesNotMatch(f.$('rv-export-msg').textContent, /copied/i);
  }
});

test('Copy review in preview reports unavailable without pretending to copy', async t => {
  const f = fixture(t);
  const button = node({ dataset: { action: 'copy_review' } });
  await f.emit('click', button);
  assert.equal(f.$('rv-export-msg').textContent, 'Copy is unavailable in preview.');
  assert.equal(button.disabled, false);
  assert.deepEqual(f.calls, []);
});

test('a failed clipboard bridge lookup reports an error and restores the Copy button', async t => {
  const f = fixture(t, null, { invokeError: new Error('Bridge disconnected') });
  const button = node({ dataset: { action: 'copy_review' } });
  await f.emit('click', button);
  assert.equal(button.disabled, false);
  assert.equal(f.$('rv-export-msg').textContent, 'Copy failed. Please try again.');
  assert.deepEqual(f.calls, []);
});

test('Export review continues to use the native save dialog and reports save or cancellation separately', async t => {
  for (const saved of [true, false]) {
    const markdown = '# Match review\n\nMy takeaway.\n';
    const f = fixture(t, command => {
      if (command === 'get_review_export_markdown') return { found: true, markdown, fileName: 'match-42.md' };
      if (command === 'save_export_file') return { saved };
      throw new Error(`Unexpected command: ${command}`);
    });
    const button = node({ dataset: { action: 'export_review' } });
    await f.emit('click', button);
    assert.deepEqual(f.calls, [
      ['get_review_export_markdown', { gameId: 42 }],
      ['save_export_file', { fileName: 'match-42.md', markdown }],
    ]);
    assert.equal(f.$('rv-export-msg').textContent, saved ? 'Export saved.' : 'Export canceled.');
    assert.equal(button.disabled, false);
    assert.equal(f.nav.invalidations, 0);
  }
});
