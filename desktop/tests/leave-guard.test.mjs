import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { LEAVE_COPY, askToLeave, changedFromDefaults, registerLeaveGuard, trackForm } from '../ui/leave-guard.mjs';

// The shell's real sidebar navigation, driven against a page that registered the guard.
const shell = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
const navigationSource = shell.slice(shell.indexOf('// Navigate the iframe'), shell.indexOf('// Current iframe file'));
function shellFor(page) {
  const assignments = [];
  let src = 'revu-app://ui/settings.html';
  page.location = { href: src }; page.document = {};
  const frame = { contentWindow: page, get src() { return src; }, set src(value) { assignments.push(value); src = new URL(value, src).href; } };
  const context = vm.createContext({ frame, URL, location: { href: 'revu-app://ui/index.html' },
    markActive() {}, fileToNav: () => '', console: { warn() {} } });
  vm.runInContext(`${navigationSource}\nglobalThis.navigate = frameGoto;`, context);
  return { assignments, navigate: target => context.navigate(target) };
}

function page({ dirty = true, flush, autosave, answers = [] } = {}) {
  const scope = {}, questions = [], state = { dirty };
  const guard = registerLeaveGuard({ scope, isDirty: () => state.dirty, flush, autosave,
    ask: async question => { questions.push(question); return answers.shift() ?? 'stay'; } });
  return { scope, questions, state, guard };
}

test('a clean page leaves without a question; a dirty one asks and Stay keeps the sidebar click from navigating', async () => {
  const clean = page({ dirty: false });
  const cleanShell = shellFor(clean.scope);
  assert.equal(await cleanShell.navigate('games.html'), true);
  assert.deepEqual(clean.questions, []);
  assert.deepEqual(cleanShell.assignments, ['games.html']);

  const dirty = page({ flush: async () => true, answers: ['stay'] });
  const dirtyShell = shellFor(dirty.scope);
  assert.equal(await dirtyShell.navigate('games.html'), false);
  assert.deepEqual(dirty.questions, [{ message: LEAVE_COPY.unsaved, canSave: true }]);
  assert.deepEqual(dirtyShell.assignments, [], 'staying keeps the page and its unsaved edits');
});

test('Discard leaves without saving; Save leaves only after the edits are saved', async () => {
  let saves = 0;
  const discarded = page({ flush: async () => { saves++; return true; }, answers: ['discard'] });
  assert.equal(await shellFor(discarded.scope).navigate('rules.html'), true);
  assert.equal(saves, 0);

  const scope = {};
  let dirty = true;
  const saved = registerLeaveGuard({ scope, isDirty: () => dirty, ask: async () => 'save',
    flush: async () => { saves++; dirty = false; return true; } });
  const s = shellFor(scope);
  assert.equal(await s.navigate('rules.html'), true);
  assert.equal(saves, 1);
  assert.deepEqual(s.assignments, ['rules.html']);
  saved.dispose();
  assert.equal(scope.revuBeforeNavigate, undefined);
});

test('a failed save keeps the question open with an explanation until the player discards or stays', async () => {
  for (const flush of [async () => false, async () => { throw new Error('locked'); }]) {
    const p = page({ flush, answers: ['save', 'discard'] });
    assert.equal(await p.guard.beforeNavigate(), true);
    assert.deepEqual(p.questions.map(q => q.message), [LEAVE_COPY.unsaved, LEAVE_COPY.failed]);
  }
  const stillDirty = page({ flush: async () => true, answers: ['save', 'stay'] });
  assert.equal(await stillDirty.guard.beforeNavigate(), false, 'a save that leaves edits behind is not a success');
  const noSave = page({ answers: ['stay'] });
  await noSave.guard.beforeNavigate();
  assert.equal(noSave.questions[0].canSave, false, 'without a save action only Discard and Stay are offered');
});

test('silent autosave runs first; only its failure asks, and Save retries it', async () => {
  let runs = 0, ok = true;
  const p = page({ dirty: false, autosave: async () => { runs++; return ok; }, answers: ['save'] });
  assert.equal(await p.guard.beforeNavigate(), true);
  assert.equal(runs, 1); assert.deepEqual(p.questions, []);
  ok = false;
  const failing = page({ dirty: false, autosave: async () => { runs++; if (runs === 3) ok = true; return ok; }, answers: ['save'] });
  assert.equal(await failing.guard.beforeNavigate(), true);
  assert.deepEqual(failing.questions, [{ message: LEAVE_COPY.failed, canSave: true }]);
});

test('a second rail click while the question is open joins it instead of stacking dialogs', async () => {
  let answer;
  const scope = {}, asked = [];
  const guard = registerLeaveGuard({ scope, isDirty: () => true,
    ask: () => { asked.push(1); return new Promise(resolve => { answer = resolve; }); } });
  const first = guard.beforeNavigate(), second = scope.revuBeforeNavigate();
  await Promise.resolve();
  answer('discard');
  assert.deepEqual(await Promise.all([first, second]), [true, true]);
  assert.equal(asked.length, 1);
});

function field(props) {
  return { type: 'text', tagName: 'INPUT', value: '', defaultValue: '', checked: false, defaultChecked: false, ...props };
}
const root = fields => ({ hidden: false, querySelectorAll: () => fields });

test('open forms are dirty only after an edit; closed forms never are; extra state counts', () => {
  const title = field({ value: 'Ward before river' }), chips = ['Ahri'];
  const form = root([title, field({ type: 'checkbox', checked: true })]);
  const tracked = trackForm(form, () => chips);
  assert.equal(tracked.isDirty(), false, 'nothing to protect before an opener fills the form');
  tracked.mark();
  assert.equal(tracked.isDirty(), false);
  title.value = 'Ward before river crossing';
  assert.equal(tracked.isDirty(), true);
  title.value = 'Ward before river';
  chips.push('Lux');
  assert.equal(tracked.isDirty(), true);
  form.hidden = true;
  assert.equal(tracked.isDirty(), false);
});

test('an always-visible form is dirty once any field leaves its markup default', () => {
  const champ = field(), loss = field({ type: 'radio', checked: true, defaultChecked: true });
  const mental = field({ type: 'range', value: '5', defaultValue: '5' });
  const form = root([champ, loss, mental]);
  assert.equal(changedFromDefaults(form), false);
  mental.value = '7';
  assert.equal(changedFromDefaults(form), true);
  mental.value = '5'; loss.checked = false;
  assert.equal(changedFromDefaults(form), true);
});

function fakeDocument() {
  const appended = [];
  const element = tag => {
    const listeners = new Map(), children = [];
    return { tag, style: {}, children, attributes: {}, textContent: '', className: '', open: false, removed: false,
      setAttribute(name, value) { this.attributes[name] = value; },
      append(...nodes) { children.push(...nodes); },
      addEventListener(type, fn) { listeners.set(type, fn); },
      dispatch(type) { const event = { prevented: false, preventDefault() { this.prevented = true; } }; listeners.get(type)?.(event); return event; },
      showModal() { this.open = true; }, close() { this.open = false; }, remove() { this.removed = true; } };
  };
  return { appended, createElement: element, body: { append: node => appended.push(node) } };
}

test('the dialog offers Save, Discard and Stay in plain copy, and Escape means stay', async () => {
  const doc = fakeDocument();
  const choice = askToLeave({ message: LEAVE_COPY.unsaved, canSave: true, document: doc });
  const dialog = doc.appended[0];
  assert.equal(dialog.open, true);
  const [title, body, actions] = dialog.children;
  assert.equal(title.textContent, 'Leave without saving?');
  assert.equal(body.textContent, LEAVE_COPY.unsaved);
  assert.deepEqual(actions.children.map(b => b.textContent), ['Save and leave', 'Discard changes', 'Stay on this page']);
  actions.children[1].dispatch('click');
  assert.equal(await choice, 'discard');
  assert.equal(dialog.open, false); assert.equal(dialog.removed, true);

  const escaped = askToLeave({ message: LEAVE_COPY.failed, canSave: false, document: doc });
  const second = doc.appended[1];
  assert.deepEqual(second.children[2].children.map(b => b.textContent), ['Discard changes', 'Stay on this page']);
  assert.equal(second.dispatch('cancel').prevented, true);
  assert.equal(await escaped, 'stay');
  for (const text of Object.values(LEAVE_COPY)) assert.doesNotMatch(text, /—/, 'no em dashes in copy');
});

test('every form page registers the shared guard', async () => {
  for (const file of ['settings.js', 'objectives.js', 'rules.js', 'manualentry.js', 'matchups.js']) {
    const source = await readFile(new URL(`../ui/${file}`, import.meta.url), 'utf8');
    assert.match(source, /from '\.\/leave-guard\.mjs'/, file);
    assert.match(source, /registerLeaveGuard\(\{/, file);
  }
});

test('the first-review tutorial pre-fill is not an edit: leaving asks only after the player changes it', async () => {
  const objectives = (await readFile(new URL('../ui/objectives.js', import.meta.url), 'utf8')).replace(/\r\n/g, '\n');
  const start = objectives.indexOf('function applyFirstReviewTutorialDefaults()');
  const prefill = objectives.slice(start, objectives.indexOf('\n}\n', start) + 2);
  const ids = ['f-title', 'f-skill', 'f-type', 'f-pre', 'f-in', 'f-post', 'f-focus', 'f-crit-metric', 'f-crit-op',
    'f-crit-val', 'f-criteria', 'f-desc'];
  const fields = Object.fromEntries(ids.map(id => [id, field({ focus() {}, ...(/^f-(pre|in|post)$/.test(id) ? { type: 'checkbox' } : {}) })]));
  const form = root(Object.values(fields));
  const context = vm.createContext({ trackForm, form, fields, other: { focus() {} } });
  vm.runInContext(`var _promptRows = [], _champs = [], _eventTypes = [];
    const $ = id => fields[id] || other;
    function setChecked(id, on) { const el = $(id); if (el) el.checked = !!on; }
    function setVal(id, v) { const el = $(id); if (el) el.value = v == null ? '' : String(v); }
    function syncTargetVisibility() {} function syncCriteriaVisibility() {}
    function renderPromptRows() {} function renderChampChips() {} function renderEventPicker() {}
    const _formGuard = trackForm(form, () => [_promptRows, _champs, _eventTypes]);
    function openCreateForm() { for (const f of Object.values(fields)) { f.value = ''; f.checked = false; } _formGuard.mark(); }
    ${prefill}
    applyFirstReviewTutorialDefaults();
    globalThis.guard = _formGuard;`, context);
  assert.equal(fields['f-title'].value, 'Review every death', 'the real tutorial pre-fill ran');
  assert.equal(context.guard.isDirty(), false);
  fields['f-title'].value = 'Review every death before queueing';
  assert.equal(context.guard.isDirty(), true);
});
