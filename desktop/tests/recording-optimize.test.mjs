import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { createRecordingSpeedUp, speedUpFailureText, speedUpWaitingText, SPEED_UP_DONE, SPEED_UP_OFFER, SPEED_UP_WORKING }
  from '../ui/recording-optimize.mjs';

const VOD = { hasVod: true, gameId: 77, filePath: 'C:\\Revu\\Recordings\\id\\capture.mp4' };
const KEPT = 'The original recording was kept.';

function fixture(replies = {}, { pollMs } = {}) {
  const doc = { activeElement: null }, classes = new Set(), attributes = new Map(), calls = [];
  const text = { textContent: '' };
  const button = { hidden: true, textContent: '', disabled: false, listeners: new Map(),
    addEventListener(type, callback) { this.listeners.set(type, callback); }, focus() { doc.activeElement = button; } };
  const root = { hidden: true, ownerDocument: doc, classList: { toggle: (name, on) => { if (on) classes.add(name); else classes.delete(name); } },
    setAttribute: (name, value) => attributes.set(name, value),
    querySelector: selector => ({ '[data-speedup-text]': text, '[data-speedup-action]': button })[selector] ?? null,
    focus() { doc.activeElement = root; } };
  const invoke = async (command, args) => {
    calls.push([command, args]);
    const reply = replies[command];
    return typeof reply === 'function' ? reply(args) : reply;
  };
  let speedUp;
  const reloads = [];
  speedUp = createRecordingSpeedUp({ root, invoke, pollMs,
    release: () => { calls.push(['release']); return { mediaTime: 412 }; },
    reload: async view => { reloads.push(view); await speedUp.check(VOD); } });
  return { speedUp, root, text, button, doc, classes, attributes, calls, reloads,
    view: () => ({ hidden: root.hidden, text: text.textContent, action: button.hidden ? '' : button.textContent, disabled: button.disabled,
      error: classes.has('is-error') }) };
}

test('an eligible recording is offered once, released before the host rewrites it, then reloaded at the same view', async () => {
  let finish;
  const f = fixture({ get_recording_optimization: { eligible: true, ready: true },
    optimize_recording: () => new Promise(resolve => { finish = resolve; }) });
  await f.speedUp.check(VOD);
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_OFFER, action: 'Speed up', disabled: false, error: false });
  assert.equal(typeof f.button.listeners.get('click'), 'function');
  f.button.focus();
  const running = f.speedUp.run();
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_WORKING, action: '', disabled: false, error: false });
  assert.equal(f.doc.activeElement, f.root, 'keyboard focus stays on the row when the button goes away');
  assert.deepEqual(f.calls, [['get_recording_optimization', { gameId: 77 }], ['release'], ['optimize_recording', { gameId: 77 }]]);
  await f.speedUp.run(); // a second activation while working does nothing
  assert.equal(f.calls.filter(([command]) => command === 'optimize_recording').length, 1);
  finish({ ok: true, status: 'optimized', fileSize: 10 });
  await running;
  assert.deepEqual(f.reloads, [{ mediaTime: 412 }]);
  // The reload renders the same game again; the result stays until the page changes game.
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_DONE, action: '', disabled: false, error: false });
  assert.equal(f.calls.filter(([command]) => command === 'get_recording_optimization').length, 1);
  await f.speedUp.check(null);
  assert.equal(f.root.hidden, true);
});

test('ineligible, missing or stale answers leave no row; a temporary block explains itself without a working button', async () => {
  const hidden = fixture({ get_recording_optimization: { eligible: false, ready: false, reason: 'already-optimized' } });
  await hidden.speedUp.check(VOD);
  assert.equal(hidden.root.hidden, true);
  const failing = fixture({ get_recording_optimization: () => { throw new Error('closing'); } });
  await failing.speedUp.check(VOD);
  assert.equal(failing.root.hidden, true);
  const none = fixture({});
  await none.speedUp.check({ hasVod: false, gameId: 77, filePath: '' });
  assert.deepEqual(none.calls, []);

  let answer;
  const stale = fixture({ get_recording_optimization: ({ gameId }) => gameId === 77
    ? new Promise(resolve => { answer = resolve; }) : { eligible: false } });
  const first = stale.speedUp.check(VOD);
  await stale.speedUp.check({ ...VOD, gameId: 78 });
  answer({ eligible: true, ready: true });
  await first;
  assert.equal(stale.root.hidden, true, 'an older game answer cannot show a row for the current game');

  const waiting = fixture({ get_recording_optimization: { eligible: true, ready: false, reason: 'game-running' } });
  await waiting.speedUp.check(VOD);
  assert.deepEqual(waiting.view(), { hidden: false, text: `${SPEED_UP_OFFER} You can speed it up after your League game ends.`,
    action: 'Speed up', disabled: true, error: false });
  await waiting.speedUp.run();
  assert.equal(waiting.calls.some(([command]) => command === 'optimize_recording'), false);
  await waiting.speedUp.check(null);
  assert.equal(speedUpWaitingText({ reason: 'not-enough-space', requiredBytes: 5.2 * 1024 ** 3 }),
    'It needs about 6 GB of free space on this drive.');
});

test('a failure says the original was kept, restores playback, and offers a retry only when it can help', async () => {
  const replies = { get_recording_optimization: { eligible: true, ready: true }, optimize_recording: { ok: false, code: 'recording-in-use' } };
  const f = fixture(replies);
  await f.speedUp.check(VOD);
  await f.speedUp.run();
  assert.deepEqual(f.view(), { hidden: false, text: speedUpFailureText('recording-in-use'), action: 'Try again', disabled: false,
    error: true });
  assert.ok(f.text.textContent.endsWith(KEPT));
  assert.deepEqual(f.reloads, [{ mediaTime: 412 }], 'the video is loaded again after a failure');
  replies.optimize_recording = { ok: true, status: 'optimized' };
  await f.speedUp.run(); // Try again asks the host first, then runs
  assert.equal(f.text.textContent, SPEED_UP_DONE);
  assert.deepEqual(f.calls.map(([command]) => command), ['get_recording_optimization', 'release', 'optimize_recording',
    'get_recording_optimization', 'release', 'optimize_recording']);

  for (const [reply, retry] of [[{ ok: false, code: 'verify-failed' }, ''], [() => { throw new Error('ipc'); }, '']]) {
    const g = fixture({ get_recording_optimization: { eligible: true, ready: true }, optimize_recording: reply });
    await g.speedUp.check(VOD);
    await g.speedUp.run();
    assert.equal(g.view().action, retry);
    assert.ok(g.text.textContent.endsWith(KEPT));
    assert.equal(g.reloads.length, 1);
  }
  assert.equal(speedUpFailureText('mystery'), `Speeding up did not finish. ${KEPT}`);
});

test('an answer read with the snapshot paints the row at once, before the page restores its view', () => {
  const f = fixture();
  void f.speedUp.check(VOD, { status: { eligible: true, ready: true } });
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_OFFER, action: 'Speed up', disabled: false, error: false });
  assert.deepEqual(f.calls, [], 'no second host read');
  void f.speedUp.check({ ...VOD, gameId: 78 }, { status: null });
  assert.equal(f.root.hidden, true);
});

const settle = ms => new Promise(resolve => setTimeout(resolve, ms));
test('a waiting row asks again and offers the button once the block clears', async () => {
  const replies = { get_recording_optimization: { eligible: true, ready: false, reason: 'recording-active' } };
  const f = fixture(replies, { pollMs: 5 });
  await f.speedUp.check(VOD);
  assert.equal(f.view().disabled, true);
  replies.get_recording_optimization = { eligible: true, ready: true };
  await settle(40);
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_OFFER, action: 'Speed up', disabled: false, error: false });
  const asked = f.calls.length;
  await settle(30);
  assert.equal(f.calls.length, asked, 'an offer is not polled');
});

test('a page reopened during its own speed-up shows the work, keeps waiting through no answer, then reloads the video', async () => {
  const replies = { get_recording_optimization: { eligible: true, ready: false, reason: 'working' } };
  const f = fixture(replies, { pollMs: 5 });
  await f.speedUp.check(VOD);
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_WORKING, action: '', disabled: false, error: false });
  replies.get_recording_optimization = () => { throw new Error('busy host'); };
  await settle(30);
  assert.equal(f.speedUp.state, 'working');
  assert.deepEqual(f.reloads, []);
  replies.get_recording_optimization = { eligible: false, ready: false, reason: 'already-optimized' };
  await settle(30);
  assert.deepEqual(f.view(), { hidden: false, text: SPEED_UP_DONE, action: '', disabled: false, error: false });
  assert.deepEqual(f.reloads, [null], 'the video loads again once, from the rewritten file');

  const left = fixture({ get_recording_optimization: { eligible: true, ready: false, reason: 'working' } }, { pollMs: 5 });
  await left.speedUp.check(VOD);
  await left.speedUp.check(null); // leaving the recording stops asking
  const asked = left.calls.length;
  await settle(30);
  assert.equal(left.calls.length, asked);
});

test('the speed-up row is a labelled live region with a described button, and its copy stays plain', async () => {
  const html = await readFile(new URL('../ui/vodplayer.html', import.meta.url), 'utf8');
  const row = html.match(/<div class="vp-speedup" id="vp-speedup"[\s\S]*?<\/div>/)?.[0] ?? '';
  assert.match(row, /role="group" aria-label="Recording file speed-up" tabindex="-1" hidden/);
  assert.match(row, /id="vp-speedup-txt" data-speedup-text role="status" aria-live="polite"/);
  assert.match(row, /<button class="ui-btn ui-btn-quiet vp-speedup-btn" data-speedup-action type="button" aria-describedby="vp-speedup-txt" hidden>/);
  assert.doesNotMatch(row, /aria-busy/);
  const source = await readFile(new URL('../ui/recording-optimize.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /[\u2014\u2013]|[\u{1F300}-\u{1FAFF}]/u);
  assert.doesNotMatch(source, /aria-busy/, 'a busy region can hold back its own live announcement');
  const js = await readFile(new URL('../ui/vodplayer.js', import.meta.url), 'utf8');
  const render = js.slice(js.indexOf('function render(data, core)'), js.indexOf('function releaseRecording()'));
  const painted = render.indexOf('_speedUp?.check(core ? data : null, { status: speedUpStatus });');
  assert.ok(painted > 0 && painted < render.indexOf('restoreMatchView();', painted), 'the row is painted before the view restores');
  assert.match(js, /_speedUpStatus = await speedUp;/);
});
