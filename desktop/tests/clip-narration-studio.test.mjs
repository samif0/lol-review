import test from 'node:test';
import assert from 'node:assert/strict';
import { openNarrationStudio, STUDIO_COPY } from '../ui/clip-narration.mjs';

// A small browser stand-in: EventTarget elements, a scripted <video>, a recorder
// that yields a WebM blob, and a microphone that can be granted, denied or busy.
class El extends EventTarget {
  constructor(name = '') { super(); this.name = name; this.hidden = false; this.disabled = false; this.textContent = ''; this.value = '';
    this.checked = false; this.dataset = {}; this.style = {}; this.options = []; this.focused = 0; }
  click() { this.dispatchEvent(new Event('click')); }
  replaceChildren(...children) { this.options = children; }
  focus() { this.focused++; if (globalThis.document) globalThis.document.activeElement = this; }
  closest(selector) { return selector === '[hidden]' && this.hidden ? this : null; }
}
class Video extends El {
  constructor() { super('video'); this.attrs = {}; this.time = 0; this.duration = 10; this.readyState = 1; this.paused = true; this.ended = false;
    this.controls = false; this.volume = 1; this.playbackRate = 1; this.frameCallbacks = []; this.plays = 0; }
  get currentTime() { return this.time; }
  set currentTime(value) { this.time = value; this.dispatchEvent(new Event('seeking')); queueMicrotask(() => this.dispatchEvent(new Event('seeked'))); }
  set src(value) { this.attrs.src = value; }
  getAttribute(name) { return this.attrs[name] ?? null; }
  removeAttribute(name) { delete this.attrs[name]; }
  load() {}
  play() { this.plays++; this.paused = false; this.dispatchEvent(new Event('playing')); return Promise.resolve(); }
  pause() { if (this.paused) return; this.paused = true; this.dispatchEvent(new Event('pause')); }
  requestVideoFrameCallback(callback) { this.frameCallbacks.push(callback); }
}
const webm = () => { const bytes = new Uint8Array(2048); bytes.set([0x1A, 0x45, 0xDF, 0xA3]); return new Blob([bytes]); };
class FakeRecorder extends EventTarget {
  static instances = [];
  static deferStop = false; // true: the stop event waits for finishStop(), as Chromium flushes first
  static isTypeSupported(type) { return type === 'audio/webm;codecs=opus'; }
  constructor(stream, options) { super(); this.stream = stream; this.options = options; this.state = 'inactive'; FakeRecorder.instances.push(this); }
  start(timeslice) { this.timeslice = timeslice; this.state = 'recording'; queueMicrotask(() => this.dispatchEvent(new Event('start'))); }
  stop() {
    this.state = 'inactive';
    this.finishStop = () => {
      this.dispatchEvent(Object.assign(new Event('dataavailable'), { data: webm() }));
      this.dispatchEvent(new Event('stop'));
    };
    if (!FakeRecorder.deferStop) this.finishStop();
  }
}
const flush = async (n = 8) => { for (let i = 0; i < n; i++) await new Promise(resolve => setTimeout(resolve, 2)); };

function environment({ access = 'granted', micError = null, savedMic = null, devicesGate = null } = {}) {
  const names = ['close', 'shared', 'mic-block', 'mic', 'level', 'blocked', 'open-settings', 'countdown', 'rec', 'game', 'game-out',
    'voice', 'voice-out', 'duck', 'sync-row', 'nudge-minus', 'nudge-plus', 'sync', 'record', 'stop', 'apply', 'record-again',
    'remove', 'done', 'status'];
  const parts = Object.fromEntries(names.map(name => [name, new El(name)]));
  parts.video = new Video();
  const dialog = new El('dialog');
  dialog.open = false;
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = () => { if (!dialog.open) return; dialog.open = false; dialog.dispatchEvent(new Event('close')); };
  dialog.querySelector = selector => parts[/data-nr="([^"]+)"/.exec(selector)?.[1]] || null;
  const tracks = [];
  const calls = { gum: [], invoke: [], saves: [], changed: 0, confirms: [], audio: [] };
  const storage = new Map(savedMic ? [['revu.narration.mic', JSON.stringify(savedMic)]] : []);
  const win = new EventTarget();
  const doc = new EventTarget();
  Object.assign(doc, { visibilityState: 'visible', activeElement: null, createElement: () => new El('option') });
  let micFailures = micError ? [micError].flat() : [];
  const globals = {
    window: win, document: doc, MediaRecorder: FakeRecorder,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    AudioContext: class { constructor() { calls.audio.push(this); } createAnalyser() { return { fftSize: 0, getFloatTimeDomainData() {} }; }
      createMediaStreamSource() { return { connect() {}, disconnect() {} }; } close() { this.closed = true; } },
    requestAnimationFrame: () => 1, cancelAnimationFrame() {},
    navigator: { mediaDevices: {
      async getUserMedia(constraints) {
        calls.gum.push(constraints);
        const failure = micFailures.shift();
        if (failure) throw Object.assign(new Error(failure), { name: failure });
        const track = { label: 'USB Mic', stopped: false, stop() { this.stopped = true; }, getSettings: () => ({ deviceId: 'mic-1' }) };
        tracks.push(track);
        return { getTracks: () => [track], getAudioTracks: () => [track] };
      },
      async enumerateDevices() {
        if (devicesGate) await devicesGate;
        return [{ kind: 'audioinput', deviceId: 'communications', label: 'Communications' }, { kind: 'videoinput', deviceId: 'cam', label: 'Cam' },
          { kind: 'audioinput', deviceId: 'mic-1', label: 'USB Mic' }, { kind: 'audioinput', deviceId: 'default', label: 'Default' }];
      },
    } },
  };
  const previous = {};
  for (const [name, value] of Object.entries(globals)) {
    previous[name] = Object.getOwnPropertyDescriptor(globalThis, name);
    Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
  }
  const restore = () => {
    for (const [name, descriptor] of Object.entries(previous)) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
    FakeRecorder.instances = [];
    FakeRecorder.deferStop = false;
  };
  let clock = 1000, confirmAnswer = true, saveReply = null;
  const opener = new El('opener');
  const open = (ctx, extra = {}) => openNarrationStudio({
    dialog, ctx, gameId: 42, opener, now: () => clock, countdownMs: 0,
    media: { resolveMedia: path => `revu-media://grant/${path}` },
    invoke: async (command, args) => {
      calls.invoke.push([command, args]);
      if (command === 'get_microphone_access') return { status: access };
      return { ok: true, shareCleared: false };
    },
    platform: { saveNarration: (bytes, meta) => { calls.saves.push({ bytes, meta }); return saveReply ? saveReply() : Promise.resolve({ ok: true, shareCleared: true }); } },
    pauseMainVod: () => { calls.paused = true; },
    onChanged: async () => { calls.changed++; return extra.fresh?.() ?? ctx; },
    confirmFn: message => { calls.confirms.push(message); return confirmAnswer; },
  });
  return { dialog, parts, tracks, calls, storage, win, doc, opener, open, restore,
    tick: ms => { clock += ms; }, set confirm(value) { confirmAnswer = value; }, set saveReply(value) { saveReply = value; } };
}
const clipCtx = (extra = {}) => ({ bookmarkId: 7, clipPath: 'clips/a.mp4', narration: null, clipStartSeconds: 20, shareUrl: '', ...extra });
const narrated = (extra = {}) => ({ bookmarkId: 7, narratedClipPath: 'clips/narrated/a.mp4', offsetMs: -260, gameVolume: 0.8,
  narrationVolume: 1, duck: true, transcriptStatus: 'pending', transcript: null, ...extra });

async function startTake(env) {
  env.parts.record.click();
  await flush();
  assert.equal(env.dialog.dataset.state, 'recording');
  const recorder = FakeRecorder.instances.at(-1);
  env.parts.video.frameCallbacks.shift()(1300, { mediaTime: 0.04, expectedDisplayTime: 1300 });
  return recorder;
}

test('opening pauses the VOD, asks for audio only and lists microphones with Default first', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    assert.equal(env.calls.paused, true); assert.equal(env.dialog.open, true); assert.equal(studio.state, 'setup');
    await flush();
    assert.deepEqual(env.calls.invoke[0], ['get_microphone_access', undefined]);
    assert.deepEqual(env.calls.gum, [{ audio: { deviceId: undefined, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
      channelCount: { ideal: 1 }, sampleRate: { ideal: 48000 } }, video: false }]);
    assert.deepEqual(env.parts.mic.options.map(option => option.value), ['default', 'mic-1', 'communications']);
    assert.equal(env.parts.mic.value, 'mic-1');
    assert.deepEqual(JSON.parse(env.storage.get('revu.narration.mic')), { deviceId: 'mic-1', label: 'USB Mic' });
    assert.equal(env.parts.record.hidden, false); assert.equal(env.parts.record.disabled, false);
    assert.equal(env.parts['mic-block'].hidden, false); assert.equal(env.parts.blocked.hidden, true);
    assert.equal(env.parts.shared.hidden, true);
    assert.equal(env.parts.video.getAttribute('src'), 'revu-media://grant/clips/a.mp4');
    assert.equal(env.parts.video.volume, 0.8);
    // A previous studio's queued close event arriving while this one is open is ignored.
    env.dialog.dispatchEvent(new Event('close'));
    assert.equal(studio.state, 'setup'); assert.equal(env.dialog.open, true);
    studio.close();
    assert.equal(env.tracks[0].stopped, true); assert.equal(env.dialog.open, false); assert.equal(env.opener.focused, 1);
  } finally { env.restore(); }
});

test('a take stops and keeps on a mid-clip pause, saves automatically and plays the narrated render', async () => {
  const env = environment();
  try {
    const fresh = clipCtx({ narration: narrated() });
    const studio = env.open(clipCtx(), { fresh: () => fresh });
    await flush();
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    env.saveReply = () => pending;
    const recorder = await startTake(env);
    assert.deepEqual(recorder.options, { mimeType: 'audio/webm;codecs=opus', audioBitsPerSecond: 96000 });
    assert.equal(recorder.timeslice, 1000);
    assert.equal(env.parts.video.plays, 1); assert.equal(env.parts.video.controls, false);
    assert.equal(env.win.__revuActiveWork, 'narration');
    env.confirm = false;
    assert.equal(await env.win.revuBeforeNavigate(), false);
    assert.deepEqual(env.calls.confirms, [STUDIO_COPY.leave]);
    env.parts.video.duration = 300;
    env.tick(4000); env.parts.video.time = 252.4;
    env.parts.video.pause(); // the user paused: stop-and-keep
    await flush();
    assert.equal(studio.state, 'saving');
    assert.match(env.parts.status.textContent, /^Recording stopped early at 4:12\. Rendering narrated clip\.\.\. You can leave this page\. Revu keeps rendering\.$/);
    assert.equal(env.calls.saves.length, 1);
    const [{ bytes, meta }] = env.calls.saves;
    assert.ok(bytes instanceof Uint8Array); assert.equal(bytes.byteLength, 2048); assert.equal(bytes[0], 0x1A);
    assert.deepEqual(meta, { gameId: 42, bookmarkId: 7, mimeType: 'audio/webm', offsetMs: -260, durationMs: 4000,
      gameVolume: 0.8, narrationVolume: 1, duck: true });
    assert.equal(await env.win.revuBeforeNavigate(), true, 'leaving during the render is allowed');
    release({ ok: true, narration: fresh.narration, shareCleared: true });
    await flush();
    assert.equal(studio.state, 'watching');
    assert.equal(env.calls.changed, 1);
    assert.equal(env.parts.status.textContent, 'Narration changed. Share again to update the link.');
    assert.equal(env.parts.video.getAttribute('src'), 'revu-media://grant/clips/narrated/a.mp4');
    assert.equal(env.parts.video.controls, true); assert.equal(env.parts.video.volume, 1);
    assert.equal(env.tracks[0].stopped, true, 'the microphone is released while watching');
    assert.equal(env.win.__revuActiveWork, false); assert.equal(env.win.revuBeforeNavigate, undefined);
    assert.equal(env.parts['sync-row'].hidden, false); assert.equal(env.parts.sync.textContent, 'Sync: -260 ms');
    for (const name of ['apply', 'record-again', 'remove', 'done']) assert.equal(env.parts[name].hidden, false, name);
    assert.equal(env.parts.apply.disabled, true, 'nothing to apply yet');
    studio.close();
  } finally { env.restore(); }
});

test('the end of the clip and the Stop button keep the whole take without an early-stop note', async () => {
  for (const stop of ['ended', 'button']) {
    const env = environment();
    try {
      const studio = env.open(clipCtx());
      await flush();
      await startTake(env);
      env.tick(9500);
      if (stop === 'ended') {
        env.parts.video.time = 9.9; env.parts.video.pause(); // reaching the end pauses first: ignored
        assert.equal(studio.state, 'recording');
        env.parts.video.ended = true; env.parts.video.dispatchEvent(new Event('ended'));
      } else env.parts.stop.click();
      await flush();
      assert.equal(env.calls.saves[0].meta.durationMs, 9500, stop);
      assert.doesNotMatch(env.parts.status.textContent, /stopped early/, stop);
      studio.close();
    } finally { env.restore(); }
  }
});

test('seeking, a long stall or hiding the window also stop and keep the take', async () => {
  for (const cause of ['seeking', 'waiting', 'hidden']) {
    const env = environment();
    try {
      const studio = env.open(clipCtx());
      await flush();
      env.saveReply = () => new Promise(() => {}); // still rendering
      await startTake(env);
      env.tick(2000); env.parts.video.time = 2;
      if (cause === 'hidden') { env.doc.visibilityState = 'hidden'; env.doc.dispatchEvent(new Event('visibilitychange')); }
      else if (cause === 'seeking') env.parts.video.dispatchEvent(new Event('seeking'));
      else { env.parts.video.dispatchEvent(new Event('waiting')); await new Promise(resolve => setTimeout(resolve, 550)); }
      await flush();
      assert.equal(env.calls.saves.length, 1, cause);
      assert.equal(studio.state, 'saving', cause);
      assert.match(env.parts.status.textContent, /^Recording stopped early at 0:02\./, cause);
      studio.close();
    } finally { env.restore(); }
  }
});

test('a microphone that drops out mid-take stops the recorder and the captured audio is kept', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    env.saveReply = () => new Promise(() => {}); // still rendering
    const recorder = await startTake(env);
    env.tick(3000); env.parts.video.time = 3;
    recorder.stop(); // Chromium ends the recording when its only track ends
    await flush();
    assert.equal(studio.state, 'saving');
    assert.equal(env.calls.saves.length, 1);
    assert.equal(env.calls.saves[0].meta.durationMs, 3000);
    assert.match(env.parts.status.textContent, /^Recording stopped early at 0:03\./);
    assert.equal(env.parts.video.paused, true);
    studio.close();
  } finally { env.restore(); }
});

test('a take under one second is discarded and returns to setup', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    await startTake(env);
    env.tick(600);
    env.parts.stop.click();
    await flush();
    assert.equal(studio.state, 'setup');
    assert.equal(env.parts.status.textContent, 'Recording was too short. Record at least one second.');
    assert.equal(env.calls.saves.length, 0);
    studio.close();
  } finally { env.restore(); }
});

test('Escape during a take asks first; leaving discards the take and releases everything', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    await startTake(env);
    env.confirm = false;
    const cancel = new Event('cancel', { cancelable: true });
    env.dialog.dispatchEvent(cancel);
    assert.equal(cancel.defaultPrevented, true);
    assert.equal(studio.state, 'recording');
    env.confirm = true;
    env.dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    await flush();
    assert.equal(studio.state, 'closed');
    assert.deepEqual(env.calls.confirms, [STUDIO_COPY.leave, STUDIO_COPY.leave]);
    assert.equal(env.calls.saves.length, 0);
    assert.equal(env.tracks[0].stopped, true); assert.equal(env.dialog.open, false);
    assert.equal(env.win.revuBeforeNavigate, undefined); assert.equal(env.win.__revuActiveWork, false);
  } finally { env.restore(); }
});

test('pagehide during a take discards it; keys other than Tab stay inside the dialog', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    for (const [key, stopped] of [['k', true], [' ', true], ['Tab', false]]) {
      const event = Object.assign(new Event('keydown'), { key });
      let propagationStopped = false;
      event.stopPropagation = () => { propagationStopped = true; };
      env.dialog.dispatchEvent(event);
      assert.equal(propagationStopped, stopped, key);
    }
    await startTake(env);
    env.tick(5000);
    env.win.dispatchEvent(new Event('pagehide'));
    await flush();
    assert.equal(studio.state, 'closed');
    assert.equal(env.calls.saves.length, 0);
  } finally { env.restore(); }
});

test('Windows privacy blocking shows the settings action and never opens the microphone', async () => {
  const env = environment({ access: 'denied' });
  try {
    const studio = env.open(clipCtx());
    await flush();
    assert.equal(env.calls.gum.length, 0);
    assert.equal(env.parts.blocked.hidden, false); assert.equal(env.parts['mic-block'].hidden, true);
    assert.equal(env.parts.record.disabled, true);
    env.parts['open-settings'].click();
    await flush();
    assert.deepEqual(env.calls.invoke.at(-1), ['open_microphone_settings', undefined]);
    studio.close();
  } finally { env.restore(); }
});

test('a missing microphone, a busy one and a vanished saved device each behave as documented', async () => {
  const missing = environment({ micError: 'NotFoundError' });
  try {
    missing.open(clipCtx()); await flush();
    assert.equal(missing.parts.status.textContent, 'No microphone found. Plug one in and try again.');
    assert.equal(missing.parts.record.disabled, true);
  } finally { missing.restore(); }
  const busy = environment({ micError: 'NotReadableError' });
  try {
    busy.open(clipCtx()); await flush();
    assert.equal(busy.parts.status.textContent, 'Your microphone is busy in another app, or Windows blocked it.');
  } finally { busy.restore(); }
  const gone = environment({ micError: 'OverconstrainedError', savedMic: { deviceId: 'old-mic', label: 'Old' } });
  try {
    gone.open(clipCtx()); await flush();
    assert.deepEqual(gone.calls.gum.map(constraints => constraints.audio.deviceId), [{ exact: 'old-mic' }, undefined]);
    assert.equal(gone.parts.record.disabled, false);
    assert.equal(gone.parts.status.textContent, '');
  } finally { gone.restore(); }
});

test('a missing clip file disables recording', async () => {
  const env = environment();
  try {
    env.open(clipCtx({ clipPath: null })); await flush();
    assert.equal(env.parts.status.textContent, 'Clip file is missing. Save the clip again.');
    assert.equal(env.parts.record.disabled, true);
  } finally { env.restore(); }
});

test('a shared or narrated clip confirms before recording, and a refused render shows the sidecar message', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx({ shareUrl: 'https://revu.lol/x' }));
    await flush();
    assert.equal(env.parts.shared.hidden, false);
    env.confirm = false;
    env.parts.record.click(); await flush();
    assert.equal(studio.state, 'setup');
    assert.deepEqual(env.calls.confirms, [STUDIO_COPY.shared]);
    env.confirm = true;
    env.saveReply = () => Promise.reject(new Error("Error invoking remote method 'revu:narration-save': Error: Clip file is missing. Save the clip again."));
    await startTake(env);
    env.tick(3000); env.parts.stop.click(); await flush();
    assert.equal(studio.state, 'error');
    assert.equal(env.parts.status.textContent, 'Clip file is missing. Save the clip again.');
    assert.equal(env.parts['record-again'].hidden, false);
    env.saveReply = () => Promise.reject(new Error('Backend HTTP 500'));
    env.parts['record-again'].click(); await flush();
    assert.equal(studio.state, 'setup');
    await startTake(env);
    env.tick(3000); env.parts.stop.click(); await flush();
    assert.equal(env.parts.status.textContent, STUDIO_COPY.renderFailed);
    studio.close();
  } finally { env.restore(); }
});

test('an existing narration opens in watching; Apply mix re-renders and Remove returns to recording setup', async () => {
  const env = environment();
  try {
    const ctx = clipCtx({ narration: narrated(), shareUrl: 'https://revu.lol/x' });
    const studio = env.open(ctx, { fresh: () => ({ ...ctx, narration: null, shareUrl: '' }) });
    await flush();
    assert.equal(studio.state, 'watching');
    assert.equal(env.calls.gum.length, 0, 'watching does not open the microphone');
    assert.equal(env.parts.video.getAttribute('src'), 'revu-media://grant/clips/narrated/a.mp4');
    env.parts['nudge-plus'].click();
    assert.equal(env.parts.sync.textContent, 'Sync: -210 ms');
    env.parts.game.value = '1.2'; env.parts.game.dispatchEvent(new Event('input'));
    env.parts.duck.checked = false; env.parts.duck.dispatchEvent(new Event('change'));
    assert.equal(env.parts.apply.disabled, false);
    env.parts.apply.click(); await flush();
    assert.deepEqual(env.calls.confirms, [STUDIO_COPY.shared]);
    assert.deepEqual(env.calls.invoke.at(-1), ['mix_clip_narration', { payload: { gameId: 42, bookmarkId: 7, offsetMs: -210,
      gameVolume: 1.2, narrationVolume: 1, duck: false } }]);
    assert.equal(env.calls.changed, 1);
    env.parts.remove.click(); await flush();
    assert.deepEqual(env.calls.confirms.slice(1), [STUDIO_COPY.remove]);
    assert.deepEqual(env.calls.invoke.find(([command]) => command === 'delete_clip_narration'),
      ['delete_clip_narration', { payload: { gameId: 42, bookmarkId: 7 } }]);
    assert.equal(studio.state, 'setup');
    assert.equal(env.calls.gum.length, 1, 'removing the narration reopens the microphone for a new take');
    assert.equal(env.parts.video.getAttribute('src'), 'revu-media://grant/clips/a.mp4');
    studio.close();
  } finally { env.restore(); }
});

const keydown = (key, extra = {}) => {
  const event = Object.assign(new Event('keydown', { cancelable: true }), { key, ...extra });
  event.propagationStopped = false;
  event.stopPropagation = () => { event.propagationStopped = true; };
  return event;
};

test('Escape is handled on keydown so a repeated Escape cannot skip the confirm, and a forced close keeps the take', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    await startTake(env);
    env.confirm = false;
    for (let i = 0; i < 2; i++) {
      const escape = keydown('Escape');
      env.dialog.dispatchEvent(escape);
      assert.equal(escape.defaultPrevented, true, 'a prevented Escape never becomes a close request');
      assert.equal(escape.propagationStopped, true);
      assert.equal(studio.state, 'recording');
    }
    assert.deepEqual(env.calls.confirms, [STUDIO_COPY.leave, STUDIO_COPY.leave]);
    env.dialog.dispatchEvent(keydown('Escape', { repeat: true }));
    assert.equal(env.calls.confirms.length, 2, 'a held key asks once');
    env.saveReply = () => new Promise(() => {}); // still rendering
    env.tick(4000);
    // The browser closed the dialog anyway (a non-cancelable close request): keep the take.
    env.dialog.open = false;
    env.dialog.dispatchEvent(new Event('close'));
    await flush();
    assert.equal(studio.state, 'closed');
    assert.equal(env.calls.saves.length, 1);
    assert.equal(env.calls.saves[0].meta.durationMs, 4000);
    assert.equal(env.tracks[0].stopped, true);
  } finally { env.restore(); }
});

test('Escape outside a take closes the studio without asking', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    const escape = keydown('Escape');
    env.dialog.dispatchEvent(escape);
    assert.equal(escape.defaultPrevented, true);
    assert.equal(studio.state, 'closed');
    assert.deepEqual(env.calls.confirms, []);
  } finally { env.restore(); }
});

test('focus moves to Stop when Record hides for the countdown, and to Done once the take is saving', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    env.saveReply = () => new Promise(() => {}); // still rendering
    env.parts.record.focus();
    await startTake(env);
    assert.equal(env.doc.activeElement, env.parts.stop, 'keys keep landing inside the dialog during the take');
    env.tick(3000);
    env.parts.stop.click();
    await flush();
    assert.equal(studio.state, 'saving');
    assert.equal(env.doc.activeElement, env.parts.done);
    studio.close();
  } finally { env.restore(); }
});

test('leaving between Stop and the recorder stop event waits until the take reaches the host', async () => {
  const env = environment();
  try {
    const studio = env.open(clipCtx());
    await flush();
    env.saveReply = () => new Promise(() => {}); // still rendering
    FakeRecorder.deferStop = true;
    const recorder = await startTake(env);
    env.tick(3000);
    env.parts.stop.click();
    assert.equal(studio.state, 'saving');
    let left = null;
    env.win.revuBeforeNavigate().then(value => { left = value; });
    await flush();
    assert.equal(left, null, 'the recorder has not handed over the take yet');
    assert.equal(env.calls.saves.length, 0);
    recorder.finishStop();
    await flush();
    assert.equal(env.calls.saves.length, 1);
    assert.equal(left, true);
    studio.close();
  } finally { env.restore(); }
});

test('closing while the microphone list loads never starts a level meter afterwards', async () => {
  let release;
  const env = environment({ devicesGate: new Promise(resolve => { release = resolve; }) });
  try {
    const studio = env.open(clipCtx());
    await flush();
    assert.equal(env.calls.gum.length, 1);
    studio.close();
    release();
    await flush();
    assert.equal(env.calls.audio.length, 0, 'no AudioContext outlives the studio');
    assert.equal(env.storage.has('revu.narration.mic'), false);
    assert.equal(env.tracks[0].stopped, true);
  } finally { env.restore(); }
});
