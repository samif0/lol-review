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
        ...(selector.startsWith('.') && child.className?.split(' ').includes(selector.slice(1)) ? [child] : []),
        ...child.querySelectorAll(selector),
      ]);
    },
    appendChild(child) { this.children.push(child); child.parent = this; },
    append(...children) { children.forEach(child => this.appendChild(child)); },
    get childElementCount() { return this.children.length; },
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); },
    closest(selector) { return selector === '[data-action]' && this.dataset.action ? this : null; },
    setAttribute(name, value) { this[name] = value; },
  };
}

function fixture(t, invoke = null, { realNavigation = false } = {}) {
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
    createElement: tag => ({ ...node(), tagName: tag.toUpperCase() }),
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
        for (const selector of ['.rv-evid-dot', '.rv-evid-time', '.rv-clip-note', '.rv-evid-actions']) row.selectors.set(selector, [node()]);
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
    getInvoke: async () => invoke ? async (...args) => { calls.push(plain(args)); return invoke(...args); } : null,
    readSnapshot: async () => ({ subject: null }),
    setTimeout, clearTimeout, URLSearchParams, CustomEvent: class {},
    console: { info() {}, warn() {}, error() {} },
  });
  vm.runInContext(`${source}\n globalThis.hooks = { captureReviewState, restoreReviewState, savePromptAnswer, renderDeaths,
    render, renderHeader, renderUnsorted, pruneEmptyClipSections,
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
