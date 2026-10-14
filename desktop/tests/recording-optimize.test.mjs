import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { createRecordingSpeedUp, initializeRecordingSpeedUp, speedUpFailureText, speedUpHoldsRecording, speedUpRowLabel,
  speedUpWaitingText, SPEED_UP_DONE, SPEED_UP_EMPTY, SPEED_UP_HELD, SPEED_UP_LOADING, SPEED_UP_UNCHECKED, SPEED_UP_WORKING }
  from '../ui/recording-optimize.mjs';
import { findSettings } from '../ui/settings-navigation.mjs';
import { fixture as vodFixture } from './vod-player-fixture.mjs';

const KEPT = 'The original recording was kept.';
const GiB = 1024 ** 3;
const AHRI = { gameId: 77, championName: 'Ahri', enemyChampion: 'Zed', datePlayed: 'Oct 13, 21:40', durationSeconds: 1920,
  fileSize: 1.4 * GiB, requiredBytes: 5.4 * GiB, ready: true, reason: null };
const LUX = { gameId: 78, championName: 'Lux', enemyChampion: 'Syndra', datePlayed: 'Oct 12, 20:15', durationSeconds: 1500,
  fileSize: 600 * 1024 ** 2, requiredBytes: 4.6 * GiB, ready: true, reason: null };
const answer = (items, extra = {}) => ({ available: true, blocked: null, working: null, items, ...extra });
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

// A small DOM: keyed rows move only when out of place, and a removed or hidden button drops focus like a browser.
function dom() {
  const doc = { activeElement: null, hidden: false, listeners: new Map(),
    addEventListener(type, callback) { this.listeners.set(type, callback); }, removeEventListener(type) { this.listeners.delete(type); } };
  doc.createElement = tag => {
    const classes = new Set(), attributes = new Map(), listeners = new Map();
    const node = { tagName: tag.toUpperCase(), children: [], parent: null, hidden: false, disabled: false, textContent: '', dataset: {},
      id: '', tabIndex: 0, type: '',
      get className() { return [...classes].join(' '); },
      set className(value) { classes.clear(); for (const name of String(value).split(/\s+/).filter(Boolean)) classes.add(name); },
      classList: { toggle: (name, on) => on ? classes.add(name) : classes.delete(name), contains: name => classes.has(name) },
      setAttribute: (name, value) => attributes.set(name, String(value)), getAttribute: name => attributes.get(name) ?? null,
      addEventListener: (type, callback) => listeners.set(type, callback),
      click() { return node.hidden || node.disabled ? undefined : listeners.get('click')?.(); },
      appendChild: child => node.insertBefore(child, null),
      insertBefore(child, before) {
        if (child.parent) { child.parent.children.splice(child.parent.children.indexOf(child), 1); node.moves = (node.moves || 0) + 1; }
        const at = before ? node.children.indexOf(before) : -1;
        node.children.splice(at < 0 ? node.children.length : at, 0, child); child.parent = node; return child;
      },
      remove() { node.parent?.children.splice(node.parent.children.indexOf(node), 1); node.parent = null; },
      focus() { doc.activeElement = node; },
      find: className => [node, ...node.children.flatMap(child => [child, ...child.children])].find(item => item.classList.contains(className)),
    };
    return node;
  };
  return doc;
}

function harness(replies = {}, { pollMs = 10_000 } = {}) {
  const document = dom(), calls = [], timers = [], shown = [];
  const card = document.createElement('div'), status = document.createElement('p'), list = document.createElement('ul');
  card.dataset.unavailable = 'true'; status.textContent = SPEED_UP_LOADING; list.hidden = true;
  const invoke = async (command, args) => {
    calls.push([command, args]);
    const reply = replies[command];
    return typeof reply === 'function' ? reply(args) : reply;
  };
  const speedUp = createRecordingSpeedUp({ card, status, list, invoke, document, pollMs, onAvailability: on => shown.push(on),
    setTimer: (callback, ms) => { timers.push({ callback, ms }); return timers.length; }, clearTimer: id => { timers[id - 1] = null; } });
  const rows = () => list.children.map(row => {
    const button = row.find('rule-act'), note = row.find('settings-speedup-note');
    return { gameId: Number(row.dataset.gameId), label: row.find('set-row-h').textContent, note: note.textContent,
      bad: note.classList.contains('bad'), action: button.hidden ? '' : button.textContent, disabled: button.disabled };
  });
  const button = gameId => list.children.find(row => row.dataset.gameId === String(gameId)).find('rule-act');
  const pending = () => timers.filter(Boolean);
  // Fire the next poll, as the timer would after pollMs.
  const tick = async () => { const timer = pending().at(-1); timers[timers.indexOf(timer)] = null; timer.callback(); await settle(); };
  // A click starts async work; wait until it and the check that follows have settled.
  const press = async gameId => { button(gameId).click(); await flush(); };
  return { document, card, status, list, calls, timers, shown, speedUp, rows, button, pending, tick, press,
    commands: () => calls.map(([command]) => command) };
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const flush = async () => { for (let i = 0; i < 5; i++) await settle(); };

test('rows read as date, matchup, duration and size; only the plain parts that are known', () => {
  assert.equal(speedUpRowLabel(AHRI), 'Oct 13, 21:40 · Ahri vs Zed · 32 min · 1.4 GB');
  assert.equal(speedUpRowLabel(LUX), 'Oct 12, 20:15 · Lux vs Syndra · 25 min · 600 MB');
  assert.equal(speedUpRowLabel({ gameId: 9, championName: 'Ahri', fileSize: 0 }), 'Ahri');
  assert.equal(speedUpRowLabel({ gameId: 9 }), 'Match');
  assert.equal(speedUpWaitingText({ reason: 'not-enough-space', requiredBytes: 5.2 * GiB }), 'It needs about 6 GB of free space on this drive.');
});

test('Speed up runs one recording at a time, holds the other rows, and leaves the finished row in place', async () => {
  const job = deferred();
  const replies = { get_recording_status: { available: true }, list_recording_optimizations: answer([AHRI, LUX]),
    optimize_recording: () => job.promise };
  const f = harness(replies);
  await f.speedUp.start();
  assert.equal(f.card.dataset.unavailable, 'false');
  assert.deepEqual(f.shown, [true]);
  assert.deepEqual(f.rows(), [
    { gameId: 77, label: 'Oct 13, 21:40 · Ahri vs Zed · 32 min · 1.4 GB', note: '', bad: false, action: 'Speed up', disabled: false },
    { gameId: 78, label: 'Oct 12, 20:15 · Lux vs Syndra · 25 min · 600 MB', note: '', bad: false, action: 'Speed up', disabled: false }]);
  assert.equal(f.status.textContent, '');
  assert.equal(f.list.hidden, false);
  assert.deepEqual(f.pending(), [], 'nothing to wait for: no polling');
  // Each button is named with its row ("Speed up Oct 13, 21:40 · Ahri vs Zed · …"); its note describes it.
  const speedUp = f.button(77);
  assert.equal(speedUp.id, 'speedup-action-77');
  assert.equal(speedUp.getAttribute('aria-labelledby'), 'speedup-action-77 speedup-label-77');
  assert.equal(speedUp.getAttribute('aria-describedby'), 'speedup-note-77');
  assert.equal(f.list.children[0].find('set-row-h').id, 'speedup-label-77');
  f.button(77).focus();
  f.button(77).click();
  await settle();
  assert.deepEqual(f.rows().map(row => [row.note, row.action, row.disabled]), [[SPEED_UP_WORKING, '', false], ['', 'Speed up', true]]);
  assert.equal(f.document.activeElement, f.list.children[0], 'keyboard focus stays on the row when its button goes away');
  assert.equal(f.button(78).click(), undefined, 'a held row cannot start a second job');
  assert.equal(f.pending()[0]?.ms, 10_000, 'the job is followed every 10 s');
  replies.list_recording_optimizations = answer([LUX]);
  job.resolve({ ok: true, status: 'optimized', fileSize: 10 });
  await flush();
  assert.deepEqual(f.rows().map(row => [row.gameId, row.note, row.action, row.disabled]),
    [[77, SPEED_UP_DONE, '', false], [78, '', 'Speed up', false]]);
  assert.deepEqual(f.commands(), ['get_recording_status', 'list_recording_optimizations', 'optimize_recording', 'list_recording_optimizations']);
  assert.deepEqual(f.calls[2][1], { gameId: 77 }, 'the page names a game, never a file');
  assert.equal(f.list.moves || 0, 0, 'rows are updated in place, never re-created or shuffled');
});

test('a game, a recording or a full drive holds the rows with its reason and clears within one check', async () => {
  const replies = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'game-running' }], { blocked: 'game-running' }) };
  const f = harness(replies);
  await f.speedUp.start();
  assert.equal(f.status.textContent, 'You can speed it up after your League game ends.');
  assert.deepEqual(f.rows().map(row => [row.note, row.action, row.disabled]), [['', 'Speed up', true]]);
  assert.equal(f.pending().length, 1);
  replies.list_recording_optimizations = answer([{ ...AHRI, ready: false, reason: 'recording-active' }], { blocked: 'recording-active' });
  await f.tick();
  assert.equal(f.status.textContent, 'You can speed it up after the current recording finishes.');
  replies.list_recording_optimizations = answer([{ ...AHRI, ready: false, reason: 'not-enough-space' }]);
  await f.tick();
  assert.equal(f.status.textContent, '');
  assert.deepEqual(f.rows().map(row => [row.note, row.disabled]), [['It needs about 6 GB of free space on this drive.', true]]);
  // A hidden page is not polled; it asks again as soon as it is visible.
  f.document.hidden = true;
  const asked = f.calls.length;
  await f.tick();
  assert.equal(f.calls.length, asked);
  replies.list_recording_optimizations = answer([AHRI]);
  f.document.hidden = false;
  f.speedUp.visible();
  await settle();
  assert.deepEqual(f.rows().map(row => [row.note, row.action, row.disabled]), [['', 'Speed up', false]]);
  assert.deepEqual(f.pending(), [], 'a clear list stops asking');
});

test('a block on every row is said once above the list, in words that fit one recording or several', async () => {
  const held = reason => ({ ready: false, reason });
  const replies = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ...held('game-running') }, { ...LUX, ...held('game-running') }], { blocked: 'game-running' }) };
  const f = harness(replies);
  await f.speedUp.start();
  assert.equal(f.status.textContent, 'You can speed them up after your League game ends.');
  assert.deepEqual(f.rows().map(row => [row.note, row.disabled]), [['', true], ['', true]], 'no row repeats it');
  replies.list_recording_optimizations = answer([{ ...AHRI, ...held('recording-active') }, { ...LUX, ...held('recording-active') }],
    { blocked: 'recording-active' });
  await f.tick();
  assert.equal(f.status.textContent, 'You can speed them up after the current recording finishes.');
  // The drive's free space could not be read: that holds every row too, so it is said once.
  replies.list_recording_optimizations = answer([{ ...AHRI, ...held('unavailable') }, { ...LUX, ...held('unavailable') }]);
  await f.tick();
  assert.equal(f.status.textContent, 'Speeding up is unavailable right now.');
  assert.deepEqual(f.rows().map(row => [row.note, row.disabled]), [['', true], ['', true]]);
  assert.equal(f.pending().length, 1, 'and it is checked again');
  replies.list_recording_optimizations = answer([AHRI, LUX]);
  await f.tick();
  assert.equal(f.status.textContent, '');
  assert.deepEqual(f.rows().map(row => [row.note, row.disabled]), [['', false], ['', false]]);
});

test('work started on an earlier visit shows as working, then done once the host confirms the file opens instantly', async () => {
  const replies = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'working' }, { ...LUX, ready: false, reason: 'busy' }], { working: 77 }),
    get_recording_optimization: { eligible: false, ready: false, reason: 'already-optimized' } };
  const f = harness(replies);
  await f.speedUp.start();
  assert.deepEqual(f.rows().map(row => [row.note, row.action, row.disabled]), [[SPEED_UP_WORKING, '', false], ['', 'Speed up', true]]);
  replies.list_recording_optimizations = null; // a missed check is not the end of the work
  await f.tick();
  assert.equal(f.rows()[0].note, SPEED_UP_WORKING);
  replies.list_recording_optimizations = answer([LUX]);
  await f.tick();
  assert.deepEqual(f.rows().map(row => [row.gameId, row.note, row.action, row.disabled]), [[77, SPEED_UP_DONE, '', false], [78, '', 'Speed up', false]]);
  assert.deepEqual(f.calls.filter(([command]) => command === 'get_recording_optimization'), [['get_recording_optimization', { gameId: 77 }]]);
  assert.equal(f.list.moves || 0, 0, 'the finished row keeps its place');
});

test('a speed-up from an earlier visit that ends without rewriting the file says so; a missed check is not mistaken for done', async () => {
  // Still listed after the work ended (a game started, the copy failed): the original was kept, and Try again waits for the game.
  const stopped = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'working' }], { working: 77 }) };
  const g = harness(stopped);
  await g.speedUp.start();
  stopped.list_recording_optimizations = answer([{ ...AHRI, ready: false, reason: 'game-running' }], { blocked: 'game-running' });
  await g.tick();
  assert.deepEqual(g.rows().map(row => [row.note, row.bad, row.action, row.disabled]),
    [[`Speeding up did not finish. ${KEPT}`, true, 'Try again', true]]);
  stopped.list_recording_optimizations = answer([AHRI]);
  await g.tick();
  assert.deepEqual(g.rows().map(row => [row.note, row.action, row.disabled]), [[`Speeding up did not finish. ${KEPT}`, 'Try again', false]]);
  assert.deepEqual(g.commands().filter(command => command === 'get_recording_optimization'), [], 'a listed row needs no extra check');

  // Left out of one answer because its check failed: the host says it is still slow, so the row comes back to try again.
  const missed = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'working' }, { ...LUX, ready: false, reason: 'busy' }], { working: 77 }),
    get_recording_optimization: { eligible: true, ready: true, reason: null } };
  const h = harness(missed);
  await h.speedUp.start();
  missed.list_recording_optimizations = answer([LUX]);
  await h.tick();
  await flush();
  assert.deepEqual(h.commands().slice(-2), ['get_recording_optimization', 'list_recording_optimizations']);
  assert.deepEqual(h.rows().map(row => [row.gameId, row.note, row.action]), [[77, `Speeding up did not finish. ${KEPT}`, ''], [78, '', 'Speed up']]);
  missed.list_recording_optimizations = answer([AHRI, LUX]);
  await h.speedUp.refresh();
  assert.deepEqual(h.rows().map(row => [row.gameId, row.note, row.action, row.disabled]),
    [[77, `Speeding up did not finish. ${KEPT}`, 'Try again', false], [78, '', 'Speed up', false]]);
  assert.notEqual(h.rows()[0].note, SPEED_UP_DONE);

  // The host cannot say either way (the game was deleted, or it did not answer): the row simply leaves.
  const gone = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'working' }], { working: 77 }),
    get_recording_optimization: () => { throw new Error('closing'); } };
  const k = harness(gone);
  await k.speedUp.start();
  gone.list_recording_optimizations = answer([]);
  await k.tick();
  await flush();
  assert.deepEqual(k.rows(), []);
  assert.equal(k.status.textContent, SPEED_UP_EMPTY);
});

test('a failure says the original was kept, offers Try again only when it can help, and Try again asks first', async () => {
  const replies = { get_recording_status: { available: true }, list_recording_optimizations: answer([AHRI]),
    optimize_recording: { ok: false, code: 'recording-in-use' } };
  const f = harness(replies);
  await f.speedUp.start();
  await f.press(77);
  assert.deepEqual(f.rows().map(row => [row.note, row.bad, row.action, row.disabled]),
    [[speedUpFailureText('recording-in-use'), true, 'Try again', false]]);
  assert.ok(f.rows()[0].note.endsWith(KEPT));
  replies.optimize_recording = { ok: true, status: 'optimized' };
  replies.list_recording_optimizations = answer([]);
  f.calls.length = 0;
  // The host is asked first; the list now has nothing for it, so Try again does not run blind.
  await f.press(77);
  assert.deepEqual(f.commands(), ['list_recording_optimizations']);
  assert.equal(f.status.textContent, SPEED_UP_EMPTY);
  assert.deepEqual(f.rows(), []);

  for (const [reply, code] of [[{ ok: false, code: 'verify-failed' }, 'verify-failed'], [() => { throw new Error('ipc'); }, undefined]]) {
    const g = harness({ get_recording_status: { available: true }, list_recording_optimizations: answer([AHRI]), optimize_recording: reply });
    await g.speedUp.start();
    await g.press(77);
    assert.deepEqual(g.rows().map(row => [row.note, row.action]), [[speedUpFailureText(code), '']]);
  }
  const retry = harness({ get_recording_status: { available: true }, list_recording_optimizations: answer([AHRI]),
    optimize_recording: { ok: false, code: 'game-running' } });
  await retry.speedUp.start();
  await retry.press(77);
  assert.equal(retry.rows()[0].note, `A League game started, so Revu stopped speeding up this recording. ${KEPT}`);
  retry.calls.length = 0;
  await retry.press(77);
  assert.deepEqual(retry.commands().slice(0, 2), ['list_recording_optimizations', 'optimize_recording'], 'once clear, Try again runs');
  assert.equal(speedUpFailureText('mystery'), `Speeding up did not finish. ${KEPT}`);
});

test('the card stays hidden where recording is unavailable, says when nothing is slow, and when it could not check', async () => {
  const off = harness({ get_recording_status: { available: false } });
  await off.speedUp.start();
  assert.equal(off.card.dataset.unavailable, 'true');
  assert.deepEqual(off.commands(), ['get_recording_status'], 'nothing is listed where recording is unavailable');
  assert.deepEqual(off.shown, []);
  const isolated = harness({ get_recording_status: { available: true }, list_recording_optimizations: { available: false, items: [] } });
  await isolated.speedUp.start();
  assert.equal(isolated.card.dataset.unavailable, 'true');
  assert.deepEqual(isolated.shown, [true, false]);
  const empty = harness({ get_recording_status: { available: true }, list_recording_optimizations: answer([]) });
  await empty.speedUp.start();
  assert.equal(empty.status.textContent, SPEED_UP_EMPTY);
  assert.equal(empty.list.hidden, true);
  assert.deepEqual(empty.pending(), []);
  const broken = harness({ get_recording_status: { available: true }, list_recording_optimizations: () => { throw new Error('closing'); } });
  await broken.speedUp.start();
  assert.equal(broken.status.textContent, SPEED_UP_UNCHECKED);
  // Browser previews have no host: the card is never shown.
  const document = dom(), card = document.createElement('div');
  card.dataset.unavailable = 'true';
  const ids = { 'speedup-settings': card, 'speedup-status': document.createElement('p'), 'speedup-list': document.createElement('ul') };
  document.getElementById = id => ids[id] ?? null;
  assert.equal(await initializeRecordingSpeedUp({ document, scope: {}, getInvoke: async () => null }), null);
  assert.equal(card.dataset.unavailable, 'true');
});

test('leaving Settings stops asking; the card is wired outside every save group', async () => {
  const document = dom(), listeners = new Map(), timers = new Set();
  const ids = { 'speedup-settings': document.createElement('div'), 'speedup-status': document.createElement('p'), 'speedup-list': document.createElement('ul') };
  document.getElementById = id => ids[id] ?? null;
  const scope = { addEventListener: (type, callback) => listeners.set(type, callback),
    setTimeout: () => { const id = {}; timers.add(id); return id; }, clearTimeout: id => timers.delete(id) };
  const replies = { get_recording_status: { available: true },
    list_recording_optimizations: answer([{ ...AHRI, ready: false, reason: 'game-running' }], { blocked: 'game-running' }) };
  const speedUp = await initializeRecordingSpeedUp({ document, scope, getInvoke: async () => async command => replies[command] });
  assert.ok(speedUp);
  assert.equal(timers.size, 1);
  assert.equal(typeof document.listeners.get('visibilitychange'), 'function');
  listeners.get('pagehide')();
  assert.equal(timers.size, 0);
  assert.equal(document.listeners.has('visibilitychange'), false);

  const html = (await readFile(new URL('../ui/settings.html', import.meta.url), 'utf8')).replace(/\r\n/g, '\n');
  const recorder = html.indexOf('id="recording-settings"'), at = html.indexOf('id="speedup-settings"');
  assert.ok(recorder > 0 && at > recorder && at < html.indexOf('class="settings-inline-note"'), 'right after the recording card');
  const card = html.slice(html.lastIndexOf('<div', at), html.indexOf('</ul>', at));
  assert.match(card, /<div class="set-card" id="speedup-settings" data-settings-card data-unavailable="true" hidden data-search="[^"]*"/);
  assert.doesNotMatch(card, /data-native-group|data-config-group|data-action/, 'it saves nothing, so it never marks Settings unsaved');
  assert.match(card, /<h3>Speed up slow recordings<\/h3>/);
  assert.match(card, /Recordings saved before Revu 4\.0\.1, or interrupted before they finished saving, can take a while to open\. Speeding one up rewrites it once so it opens instantly\. The video and audio stay exactly the same\./);
  assert.match(card, /<p class="set-status" id="speedup-status" role="status" aria-live="polite">Checking your recordings…<\/p>/);
  const text = card.replace(/<[^>]+>/g, ' ');
  const search = card.match(/data-search="([^"]*)"/)[1];
  for (const query of ['speed', 'slow', 'speed up recordings']) {
    assert.deepEqual(findSettings([{ id: 'recording', title: 'Recording', cards: [{ id: 'speedup-settings', text: `${search} ${text}` }] }], query)[0].cards,
      ['speedup-settings'], query);
  }
  const source = await readFile(new URL('../ui/recording-optimize.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(source + card, /[\u2014\u2013]|[\u{1F300}-\u{1FAFF}]/u);
  const js = await readFile(new URL('../ui/settings.js', import.meta.url), 'utf8');
  assert.match(js, /initializeRecordingSpeedUp\(\{ getInvoke, onAvailability: \(\) => navigation\?\.refresh\(\) \}\)/);
});

test('the VOD page has no speed-up row; a file held by a running speed-up says so in the player, not as a load error', async () => {
  const read = name => readFile(new URL(`../ui/${name}`, import.meta.url), 'utf8');
  const [html, css, js] = await Promise.all([read('vodplayer.html'), read('vod-review.css'), read('vodplayer.js')]);
  for (const source of [html, css, js]) assert.doesNotMatch(source, /vp-speedup|speedup-|createRecordingSpeedUp|releaseRecording|reloadRecording/);
  // The note sits in the player stage, where the picture would be, and is announced politely.
  assert.match(html.replace(/\r\n/g, '\n'), /<div class="vp-stage">\s*<video id="vp-video"[^>]*><\/video>\s*<!--[^\n]*-->\s*<p class="vp-stage-note" id="vp-stage-note" role="status"><\/p>\s*<\/div>/);
  // The well is dark in every theme: the note carries its own raised surface so its text reads in light modes too.
  assert.match(css, /\.vod-review-page \.vp-stage-note\{[^}]*background:var\(--raised\);color:var\(--t2\)/);
  assert.equal(await speedUpHoldsRecording(null, 42), false);
  assert.equal(await speedUpHoldsRecording({ invoke: async () => { throw new Error('closing'); } }, 42), false);

  const LOAD_ERROR = 'This local video could not be opened. It may have moved or be unavailable.';
  for (const [reply, note, error] of [[{ eligible: true, ready: false, reason: 'working' }, SPEED_UP_HELD, ''],
    [{ eligible: false, ready: false, reason: 'not-linked' }, '', LOAD_ERROR]]) {
    const asked = [];
    const f = vodFixture({ assetUrl: null, invokeWrite: (command, args) => { asked.push([command, args]); return reply; } });
    f.$('errpanel').hidden = true;
    f.hooks.render({ ...f.snapshot }, f.core);
    await settle();
    assert.deepEqual(asked, [['get_recording_optimization', { gameId: 42 }]]);
    assert.equal(f.$('vp-stage-note').textContent, note);
    assert.equal(f.$('err-detail').textContent, error);
    assert.equal(f.$('errpanel').hidden, !error, 'the error panel is only for a real load failure');
  }
  // An answer about an earlier render never replaces what a newer one shows, and a new render clears the note.
  let answer;
  const f = vodFixture({ assetUrl: null, invokeWrite: () => new Promise(resolve => { answer = resolve; }) });
  f.hooks.render({ ...f.snapshot }, f.core);
  f.hooks.render({ ...f.snapshot, hasVod: false, filePath: '' }, f.core);
  answer({ reason: 'working' });
  await settle();
  assert.equal(f.$('vp-stage-note').textContent, '');
  const held = vodFixture({ assetUrl: null, invokeWrite: () => ({ reason: 'working' }) });
  held.hooks.render({ ...held.snapshot }, held.core);
  await settle();
  assert.equal(held.$('vp-stage-note').textContent, SPEED_UP_HELD);
  held.hooks.render({ ...held.snapshot }, held.core); // a later render, for example once the recording is linked again
  assert.equal(held.$('vp-stage-note').textContent, '');
});
