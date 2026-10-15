// Review notes (review-notes.js, review-notes-ui.js): a coach's bookmarks and clips on a shared
// game or clip, kept in this browser and sent as a link (#notes=...). The data first: cleaning,
// links and storage; then the composer (B, I and O, as in the app) and Copy link with notes.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const read = name => readFileSync(new URL(`../${name}`, import.meta.url), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

// The two scripts in a fresh realm; `compress` leaves out CompressionStream, as in an older browser.
function load({ compress = true } = {}) {
  const context = { TextEncoder, TextDecoder, btoa, atob, Blob, Response, URLSearchParams };
  if (compress) Object.assign(context, { CompressionStream, DecompressionStream });
  vm.createContext(context);
  vm.runInContext(read('review-notes.js'), context);
  vm.runInContext(read('review-notes-ui.js'), context);
  return { N: context.RevuNotes, Ui: context.RevuNotesUi };
}

const { N, Ui } = load();

function memoryStorage(seed = {}) {
  const map = new Map(Object.entries(seed));
  return {
    map,
    get length() { return map.size; },
    key: i => [...map.keys()][i] ?? null,
    getItem: k => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); },
    removeItem: k => { map.delete(k); },
  };
}

test('notes are cleaned: in range, in time order, each once, clips at least a second, text without control characters', () => {
  const rlo = String.fromCharCode(0x202e), bell = String.fromCharCode(7);
  const notes = N.clean([
    { t: 610.04, end: 640, text: '  Good trade ' + rlo + 'but back' + bell + '  ' },
    { t: 300, end: null, text: 'Wave was crashing' },
    { t: 300, end: null, text: 'Wave was crashing' },
    { t: 5, end: 5.5, text: 'x' },
    { t: -1, text: 'before the start' },
    { t: 99999, text: 'past any game' },
    { t: '12', text: 'a string time' },
    { t: 20, end: 99999, text: 'an end past any game' },
    { t: 30, text: 'line one\r\n\r\n\r\n\r\nline two\tand a tab' },
    null, 'text',
  ]);
  assert.deepEqual(plain(notes), [
    { t: 5, end: null, text: 'x' },
    { t: 30, end: null, text: 'line one\n\nline two and a tab' },
    { t: 300, end: null, text: 'Wave was crashing' },
    { t: 610, end: 640, text: 'Good trade but back' },
  ]);
  assert.equal(N.cleanText('a'.repeat(600)).length, N.MAX_TEXT);
  assert.equal(N.clean(Array.from({ length: 150 }, (_, i) => ({ t: i, text: '' }))).length, N.MAX_NOTES);
  assert.deepEqual(plain(N.merge([{ t: 1, text: 'a' }], [{ t: 1, text: 'a' }, { t: 2, end: 9, text: 'b' }])),
    [{ t: 1, end: null, text: 'a' }, { t: 2, end: 9, text: 'b' }]);
});

test('a link carries its notes after the #, compressed where the browser can, and reads back the same', async () => {
  const notes = N.clean(Array.from({ length: 25 }, (_, i) => ({ t: i * 60, end: i % 2 ? i * 60 + 20 : null,
    text: `Watch the minimap here, their jungler shows bot side ${i}` })));
  const packed = await N.encode(notes);
  assert.equal(packed[0], 'z');
  assert.ok(packed.length < N.encodeNow(notes).length / 3, 'deflate makes a long list short enough to paste');
  assert.match(packed, /^z[A-Za-z0-9_-]+$/, 'base64url: nothing a URL or a chat app would change');
  assert.deepEqual(plain(await N.decode(packed)), plain(notes));
  assert.deepEqual(plain(await N.decode(N.encodeNow(notes))), plain(notes));

  const old = load({ compress: false }).N;
  assert.equal((await old.encode(notes))[0], 'j', 'without CompressionStream the link is plain');
  assert.equal(await old.decode(packed), null, 'and a compressed link reads as unreadable, not as an error');

  const one = [{ t: 61.5, end: null, text: 'Ward here' }];
  assert.equal(N.encodeNow(one), 'j' + Buffer.from('[[61.5,"Ward here"]]').toString('base64url'));
  assert.equal(N.linkFor('https://revu.lol/v/Abc1234#notes=old', 'jXYZ'), 'https://revu.lol/v/Abc1234#notes=jXYZ');
  assert.equal(N.fromHash('#notes=jXYZ'), 'jXYZ');
  assert.equal(N.fromHash('#t=10&notes=zABC'), 'zABC');
  assert.equal(N.fromHash('#other=1'), '');
});

test('a link that is not ours, too long, broken or inflating past the cap reads as nothing', async () => {
  const b64 = value => Buffer.from(value).toString('base64url');
  for (const payload of ['', 'x', 'q' + b64('[]'), 'j' + b64('{"t":1}'), 'j' + b64('not json'), 'j!!', 'j' + 'A'.repeat(N.MAX_PAYLOAD),
    'z' + b64('not deflate'), 'j' + Buffer.from([0xff, 0xfe, 0x41]).toString('base64url')]) {
    assert.equal(await N.decode(payload), null, payload.slice(0, 20));
  }
  assert.deepEqual(plain(await N.decode('j' + b64('[[1,"a"],[2],["x",3,"b"],[4,9,"c"],5]'))),
    [{ t: 1, end: null, text: 'a' }, { t: 4, end: 9, text: 'c' }], 'rows of the wrong shape are dropped');
  // 300 kB of one character deflates to a few hundred bytes: refused once inflated past the cap.
  const bomb = await new Response(new Blob([Buffer.from('[[1,"' + 'a'.repeat(300000) + '"]]')]).stream()
    .pipeThrough(new CompressionStream('deflate-raw'))).arrayBuffer();
  const payload = 'z' + Buffer.from(bomb).toString('base64url');
  assert.ok(payload.length < N.MAX_PAYLOAD);
  assert.equal(await N.decode(payload), null);
});

test('notes stay in this browser per share, survive blocked storage, and leave it two weeks after their last change', () => {
  const storage = memoryStorage();
  const key = N.storageKey('v', 'Abc1234');
  assert.equal(key, 'revu.notes.v.Abc1234');
  assert.equal(N.save(storage, key, N.clean([{ t: 3, text: 'here' }]), 1000), true);
  assert.deepEqual(plain(N.load(storage, key)), [{ t: 3, end: null, text: 'here' }]);
  assert.deepEqual(JSON.parse(storage.getItem(key)).saved, 1000);
  N.save(storage, key, [], 2000);
  assert.equal(storage.getItem(key), null, 'no notes, no entry');

  storage.setItem('revu.notes.c.Old0001', JSON.stringify({ v: 1, saved: 0, notes: [] }));
  storage.setItem('revu.notes.c.New0001', JSON.stringify({ v: 1, saved: N.KEEP_MS, notes: [] }));
  storage.setItem('revu.notes.c.Bad0001', '{not json');
  storage.setItem('someone.else', 'kept');
  N.prune(storage, N.KEEP_MS + 1000);
  assert.deepEqual([...storage.map.keys()].sort(), ['revu.notes.c.New0001', 'someone.else']);

  const blocked = { get length() { throw new Error('blocked'); }, getItem() { throw new Error('blocked'); },
    setItem() { throw new Error('blocked'); }, removeItem() { throw new Error('blocked'); }, key() { throw new Error('blocked'); } };
  assert.deepEqual(plain(N.load(blocked, key)), []);
  assert.equal(N.save(blocked, key, [{ t: 1, end: null, text: '' }], 1), false);
  assert.doesNotThrow(() => N.prune(blocked, 1));
  assert.equal(N.browserStorage({ get localStorage() { throw new Error('SecurityError'); } }), null);
  assert.equal(N.browserStorage({}), null);
});

test('opening a link adds its notes to this browser\'s and takes them out of the address bar', async () => {
  const storage = memoryStorage();
  const key = N.storageKey('v', 'Abc1234');
  N.save(storage, key, N.clean([{ t: 3, text: 'mine' }]), 1);
  const link = N.linkFor('https://revu.lol/v/Abc1234', N.encodeNow([{ t: 3, text: 'mine' }, { t: 9, end: 20, text: 'theirs' }]));
  const replaced = [];
  const result = await N.adopt({ storage, key, href: link, nowMs: 5, replace: url => replaced.push(url) });
  assert.deepEqual(plain(result.notes), [{ t: 3, end: null, text: 'mine' }, { t: 9, end: 20, text: 'theirs' }]);
  assert.deepEqual([result.added, result.unreadable, replaced], [1, false, ['https://revu.lol/v/Abc1234']]);
  assert.equal(N.adoptedText(result), 'Added 1 note from the link.');
  assert.deepEqual(plain(N.load(storage, key)), plain(result.notes), 'kept for the next visit');

  const broken = await N.adopt({ storage, key, href: 'https://revu.lol/v/Abc1234#notes=jnope!', nowMs: 6, replace: () => {} });
  assert.deepEqual([broken.added, broken.unreadable, N.adoptedText(broken)], [0, true, "The notes in this link couldn't be read."]);
  const none = await N.adopt({ storage, key, href: 'https://revu.lol/v/Abc1234', nowMs: 7, replace: () => assert.fail('nothing to replace') });
  assert.deepEqual([none.notes.length, none.added, N.adoptedText(none)], [2, 0, '']);
  assert.equal(N.adoptedText({ added: 3 }), 'Added 3 notes from the link.');
});

// A form for the composer: elements with values, focus and listeners.
function composerPage({ now = 0 } = {}) {
  const make = tag => {
    const listeners = new Map(), attrs = new Map();
    return {
      tagName: tag.toUpperCase(), hidden: false, textContent: '', value: '', title: '', focused: false,
      addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(fn); },
      dispatch(type) { const ev = { type, preventDefault() { this.defaultPrevented = true; } }; for (const fn of listeners.get(type) || []) fn(ev); return ev; },
      setAttribute: (k, v) => attrs.set(k, String(v)), getAttribute: k => (attrs.has(k) ? attrs.get(k) : null),
      focus() { this.focused = true; }, blur() { this.focused = false; }, click() { this.dispatch('click'); },
    };
  };
  const els = { form: make('form'), when: make('p'), text: make('textarea'), cancel: make('button'), clip: make('button'),
    clipLabel: make('span'), clipKey: make('kbd'), hint: make('p') };
  const page = { time: now, paused: false, opened: 0, saved: [], drafts: [] };
  const composer = Ui.composer({ els, now: () => page.time, clock: t => `${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}`,
    pause: () => { page.paused = true; }, open: () => { page.opened += 1; }, onSave: note => page.saved.push(plain(note)),
    onDraft: draft => page.drafts.push(draft && plain(draft)) });
  const key = (k, init = {}) => {
    const ev = { key: k, target: { tagName: 'BODY' }, repeat: false, shiftKey: false, ctrlKey: false, metaKey: false, altKey: false, ...init };
    return composer.key(ev);
  };
  return { els, page, composer, key };
}

test('B bookmarks the moment on screen: the form opens with its time, pauses the game and takes the typing; Enter saves', () => {
  const { els, page, key } = composerPage({ now: 312.46 });
  assert.equal(els.form.hidden, true);
  assert.equal(key('b'), true);
  assert.deepEqual([els.form.hidden, els.when.textContent, page.paused, els.text.focused, page.opened], [false, 'Bookmark at 5:12.', true, true, 1]);
  els.text.value = 'You had flash here';
  assert.equal(key('Enter', { target: els.text, shiftKey: true }), false, 'Shift+Enter is a new line');
  assert.equal(key('k', { target: els.text }), false, 'typing stays typing');
  assert.equal(key('Enter', { target: els.text }), true);
  assert.deepEqual(page.saved, [{ t: 312.5, end: null, text: 'You had flash here' }]);
  assert.deepEqual([els.form.hidden, els.text.value, els.text.focused], [true, '', false], 'saved: the form closes and lets go of the keys');
  key('b');
  els.text.value = 'never mind';
  assert.equal(key('Escape', { target: els.text }), true);
  assert.deepEqual([els.form.hidden, page.saved.length], [true, 1], 'Esc drops the draft');
  assert.equal(key('b', { ctrlKey: true }), false, 'Ctrl+B is the browser\'s');
  assert.equal(key('b', { target: { tagName: 'INPUT' } }), false, 'another field keeps its typing');
  assert.equal(key('x'), false);
});

test('I starts a clip and the game plays on; O ends it, pauses and takes the note; the button follows along', () => {
  const { els, page, composer, key } = composerPage({ now: 600 });
  key('i');
  assert.deepEqual([els.when.textContent, page.paused, els.text.focused], ['Clip from 10:00. Press O to end it.', false, false]);
  assert.deepEqual([els.clipLabel.textContent, els.clipKey.textContent, els.clip.getAttribute('aria-pressed')], ['End clip', 'O', 'true']);
  assert.deepEqual(page.drafts.at(-1), { kind: 'clip', t: 600, end: null });
  page.time = 600.5;
  key('o');
  assert.equal(els.hint.textContent, 'Play on, then press O to end the clip.', 'under a second is not a clip');
  assert.equal(els.hint.hidden, false);
  page.time = 641;
  key('o');
  assert.deepEqual([els.when.textContent, page.paused, els.text.focused, els.hint.hidden], ['Clip from 10:00 to 10:41.', true, true, true]);
  assert.deepEqual([els.clipLabel.textContent, els.clipKey.textContent], ['Clip', 'I']);
  els.form.dispatch('submit');
  assert.deepEqual(page.saved, [{ t: 600, end: 641, text: '' }], 'a clip needs no words');

  // O before I asks for I; the clip button starts and ends; Save on an open clip ends it at the playhead.
  key('o');
  assert.equal(els.when.textContent, 'Press I to start a clip, then O to end it.');
  assert.equal(composer.save(), false);
  assert.equal(els.hint.textContent, 'Press I to start the clip first.');
  page.time = 700;
  composer.clipButton();
  page.time = 690;
  composer.clipButton();
  assert.deepEqual(plain(composer.draft()), { kind: 'clip', t: 690, end: 700 }, 'an end before the start swaps them');
  composer.cancel();
  composer.clipButton();
  page.time = 712;
  els.text.value = 'ended by Save';
  assert.equal(composer.save(), true);
  assert.deepEqual(page.saved.at(-1), { t: 690, end: 712, text: 'ended by Save' });
  assert.equal(key('i', { repeat: true }), true, 'a held key is used once');
  assert.equal(composer.draft(), null);
});

test('Copy link with notes copies at once, or shows the link to copy by hand; it hides without notes', async () => {
  const el = () => ({ hidden: false, textContent: '', value: '', listeners: [], addEventListener(t, fn) { this.listeners.push(fn); },
    click() { this.listeners.forEach(fn => fn()); }, focus() { this.focused = true; }, select() { this.selected = true; } });
  const els = { box: el(), copy: el(), status: el(), link: el() };
  let notes = [];
  const copied = [];
  let clipboard = { writeText: async text => { copied.push(text); } };
  const timers = [];
  const sharer = Ui.sharer({ els, href: () => 'https://revu.lol/v/Abc1234#notes=stale', notes: () => notes,
    get clipboard() { return clipboard; }, setTimeout: (fn, ms) => timers.push({ fn, ms }), clearTimeout: () => {} });
  assert.equal(els.box.hidden, true);
  notes = N.clean([{ t: 3, text: 'here' }]);
  sharer.refresh();
  assert.equal(els.box.hidden, false);
  await new Promise(resolve => setImmediate(resolve));
  els.copy.click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(copied.length, 1);
  assert.match(copied[0], /^https:\/\/revu\.lol\/v\/Abc1234#notes=[jz][A-Za-z0-9_-]+$/);
  assert.deepEqual(plain(N.clean(await N.decode(N.fromHash(copied[0].slice(copied[0].indexOf('#')))))), plain(notes));
  assert.equal(els.status.textContent, 'Link copied. Anyone with it sees these notes.');
  assert.equal(timers.at(-1).ms, 6000);
  clipboard = { writeText: async () => { throw new Error('denied'); } };
  els.copy.click();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual([els.status.textContent, els.link.hidden, els.link.selected], ['Copy this link:', false, true]);
  assert.match(els.link.value, /#notes=/);
  clipboard = undefined;
  els.status.textContent = '';
  els.copy.click();
  assert.equal(els.status.textContent, 'Copy this link:', 'no clipboard at all: the link to copy by hand');
});

test('the status line says what happened, with an Undo when there is one', () => {
  const doc = { createElement: tag => ({ tagName: tag.toUpperCase(), textContent: '', listeners: [], addEventListener(t, fn) { this.listeners.push(fn); } }) };
  const box = { children: [], textContent: 'old', get firstChild() { return this.children[0] || null; },
    appendChild(child) { this.children.push(child); return child; }, removeChild(child) { this.children = this.children.filter(c => c !== child); } };
  let undone = 0;
  const button = Ui.statusLine(doc, box, 'Note deleted.', 'Undo', () => { undone += 1; });
  assert.deepEqual(box.children.map(c => [c.tagName, c.textContent]), [['SPAN', 'Note deleted.'], ['BUTTON', 'Undo']]);
  button.listeners[0]();
  assert.equal(undone, 1);
  assert.equal(Ui.statusLine(doc, box, ''), null);
  assert.deepEqual(box.children, []);
});

test('the notes scripts write no HTML, keep to their storage prefix and stay under 500 lines', () => {
  for (const name of ['review-notes.js', 'review-notes-ui.js', 'vod-side.js', 'clip-coach.js']) {
    const source = read(name);
    assert.doesNotMatch(source, /innerHTML|insertAdjacentHTML|outerHTML|document\.write|\beval\(|new Function/, name);
    assert.doesNotMatch(source, /sessionStorage|document\.cookie|indexedDB|fetch\(|XMLHttpRequest|sendBeacon/, `${name} sends nothing`);
    assert.doesNotMatch(source, new RegExp(`[${String.fromCharCode(0x2013, 0x2014)}]`), `${name} has no dashes`);
    assert.doesNotMatch(source, /[^\x09\x0a\x0d\x20-\x7e]/, `${name} is plain ASCII: escapes, never invisible characters`);
    assert.ok(source.split('\n').length <= 500, `${name} stays under 500 lines`);
  }
});
