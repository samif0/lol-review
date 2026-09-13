import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Exercise the real iframe navigation function without booting the shell's
// recorder, updater, live-game bridge or other application services.
const shell = await readFile(new URL('../ui/shell-outer.js', import.meta.url), 'utf8');
const start = shell.indexOf('// Navigate the iframe');
const end = shell.indexOf('// Current iframe file', start);
assert.ok(start >= 0 && end > start, 'shell navigation section must remain identifiable');
const navigationSource = shell.slice(start, end);

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function fixture(beforeNavigate) {
  const initial = 'revu-app://ui/review.html?gameId=42';
  const assignments = [], active = [];
  const sourceWindow = { location: { href: initial }, document: {} };
  if (beforeNavigate) sourceWindow.revuBeforeNavigate = beforeNavigate;
  let src = initial;
  const frame = {
    contentWindow: sourceWindow,
    get src() { return src; },
    set src(value) { assignments.push(value); src = new URL(value, src).href; },
  };
  const context = vm.createContext({
    frame, URL, location: { href: 'revu-app://ui/index.html' },
    markActive: value => active.push(value),
    fileToNav: pathname => pathname.split('/').pop().replace(/\.html$/, ''),
    console: { warn() {}, error() {} },
  });
  vm.runInContext(`${navigationSource}\nglobalThis.navigate = frameGoto;`, context, { filename: 'shell-outer.js' });
  return { frame, initial, assignments, active, sourceWindow, navigate: context.navigate };
}

test('sidebar navigation waits for the current review write before changing route or active item', async () => {
  const saved = deferred();
  let checks = 0;
  const f = fixture(() => { checks++; return saved.promise; });
  const navigation = f.navigate('objectives.html');
  await Promise.resolve();
  assert.equal(checks, 1);
  assert.equal(f.frame.src, f.initial);
  assert.deepEqual(f.assignments, []);
  assert.deepEqual(f.active, []);
  saved.resolve(true); await navigation;
  assert.deepEqual(f.assignments, ['objectives.html']);
  assert.deepEqual(f.active, ['objectives']);
});

test('an unsuccessful or rejected review flush keeps the current route and sidebar selection', async () => {
  for (const reject of [false, true]) {
    const saved = deferred();
    const f = fixture(() => saved.promise);
    const navigation = f.navigate('games.html');
    if (reject) saved.reject(new Error('Unable to save evidence'));
    else saved.resolve(false);
    await navigation;
    assert.equal(f.frame.src, f.initial);
    assert.deepEqual(f.assignments, []);
    assert.deepEqual(f.active, []);
  }
});

test('rapid sidebar choices navigate only to the last requested page', async () => {
  const first = deferred(), second = deferred();
  let checks = 0;
  const f = fixture(() => ++checks === 1 ? first.promise : second.promise);
  const oldRequest = f.navigate('games.html');
  const latestRequest = f.navigate('objectives.html?objectiveId=9#evidence');
  assert.equal(checks, 2);
  second.resolve(true); await latestRequest;
  first.resolve(true); await oldRequest;
  assert.deepEqual(f.assignments, ['objectives.html?objectiveId=9#evidence']);
  assert.deepEqual(f.active, ['objectives']);
});

test('an earlier request cannot navigate while a newer choice is still waiting to save', async () => {
  const first = deferred(), second = deferred();
  let checks = 0;
  const f = fixture(() => ++checks === 1 ? first.promise : second.promise);
  const oldRequest = f.navigate('games.html');
  const latestRequest = f.navigate('patterns.html');
  first.resolve(true); await oldRequest;
  assert.deepEqual(f.assignments, []);
  assert.deepEqual(f.active, []);
  second.resolve(true); await latestRequest;
  assert.deepEqual(f.assignments, ['patterns.html']);
  assert.deepEqual(f.active, ['patterns']);
});

test('a delayed navigation does not overwrite a frame that already moved to another document', async () => {
  for (const change of ['location', 'window', 'document']) {
    const saved = deferred();
    const f = fixture(() => saved.promise);
    const navigation = f.navigate('objectives.html');
    if (change === 'window') {
      f.frame.contentWindow = { location: { href: f.initial }, document: {} };
    } else if (change === 'document') {
      f.sourceWindow.document = {};
    } else {
      f.sourceWindow.location.href = 'revu-app://ui/vodplayer.html?gameId=42';
    }
    saved.resolve(true); await navigation;
    assert.deepEqual(f.assignments, [], 'the delayed request belongs to the previous frame page');
    assert.deepEqual(f.active, []);
  }
});

test('choosing the current page cancels an earlier pending sidebar navigation', async () => {
  const saved = deferred();
  const f = fixture(() => saved.promise);
  const earlier = f.navigate('games.html');
  await f.navigate('review.html?gameId=42');
  saved.resolve(true); await earlier;
  assert.deepEqual(f.assignments, []);
  assert.deepEqual(f.active, []);
});

test('pages without a write barrier navigate normally and retain query and fragment', async () => {
  const f = fixture();
  await f.navigate('vodplayer.html?gameId=42&t=864#clip');
  assert.deepEqual(f.assignments, ['vodplayer.html?gameId=42&t=864#clip']);
  assert.deepEqual(f.active, ['vodplayer']);
});
