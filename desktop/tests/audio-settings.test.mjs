import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { AUDIO_SETTINGS_COPY, initializeAudioSettings, MIC_TEST_LIMIT_MS } from '../ui/audio-settings.mjs';
import { MIC_COPY } from '../ui/narration-capture.mjs';

const html = (await readFile(new URL('../ui/settings.html', import.meta.url), 'utf8')).replace(/\r\n/g, '\n');
const flush = async (n = 8) => { for (let i = 0; i < n; i++) await new Promise(resolve => setTimeout(resolve, 0)); };

class El extends EventTarget {
  constructor(id = '') {
    super();
    Object.assign(this, { id, hidden: false, disabled: false, textContent: '', value: '', options: [], style: {}, attributes: {} });
    const classes = new Set();
    this.classList = { add: (...names) => names.forEach(name => classes.add(name)),
      remove: (...names) => names.forEach(name => classes.delete(name)), contains: name => classes.has(name) };
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  replaceChildren(...children) { this.options = children; }
  click() { this.dispatchEvent(new Event('click')); }
}
const IDS = ['audio-settings', 'audioInput', 'audioOutput', 'audio-mic-test', 'audio-mic-meter', 'audio-mic-level',
  'audio-input-missing', 'audio-output-test', 'audio-output-missing', 'audio-names', 'audio-show-names', 'audio-status'];
const DEVICES = [
  { kind: 'audioinput', deviceId: 'default', label: 'Default - Desk Mic' },
  { kind: 'audioinput', deviceId: 'communications', label: 'Communications - Headset Mic' },
  { kind: 'audioinput', deviceId: 'mic-desk', label: 'Desk Mic' },
  { kind: 'audioinput', deviceId: 'mic-headset', label: 'Headset Mic' },
  { kind: 'audiooutput', deviceId: 'default', label: 'Default - Speakers' },
  { kind: 'audiooutput', deviceId: 'spk', label: 'Speakers' },
  { kind: 'audiooutput', deviceId: 'phones', label: 'Headphones' },
];
const HIDDEN = [{ kind: 'audioinput', deviceId: '', label: '' }, { kind: 'audiooutput', deviceId: '', label: '' }];

function fixture({ saved = {}, list = DEVICES, access = 'granted', gumGate = null, gumError = null, unlocks = null } = {}) {
  const ids = new Map(IDS.map(id => [id, new El(id)]));
  for (const id of ['audio-mic-meter', 'audio-input-missing', 'audio-output-missing', 'audio-names']) ids.get(id).hidden = true;
  const document = new EventTarget();
  Object.assign(document, { hidden: false, visibilityState: 'visible', getElementById: id => ids.get(id) || null,
    createElement: () => ({ value: '', textContent: '' }) });
  const scope = new EventTarget();
  const storage = new Map(Object.entries(saved).map(([key, value]) => [key, JSON.stringify(value)]));
  const calls = { gum: [], invoke: [], contexts: [] };
  const tracks = [], timers = [], frames = [];
  const mediaDevices = new EventTarget();
  mediaDevices.list = list;
  mediaDevices.enumerateDevices = async () => mediaDevices.list.map(device => ({ groupId: '', ...device }));
  mediaDevices.getUserMedia = async constraints => {
    calls.gum.push(constraints);
    if (gumGate) await gumGate;
    if (gumError) throw Object.assign(new Error(gumError), { name: gumError });
    if (unlocks) mediaDevices.list = unlocks; // granting the microphone reveals names
    const track = { stopped: false, stop() { this.stopped = true; } };
    tracks.push(track);
    return { getTracks: () => [track], getAudioTracks: () => [track] };
  };
  class FakeAudioContext {
    constructor() {
      Object.assign(this, { closed: false, sinks: [], sinkId: '', currentTime: 2, state: 'running', destination: {}, gains: [], started: [], stopped: [] });
      calls.contexts.push(this);
    }
    async setSinkId(id) { this.sinks.push(id); this.sinkId = id; }
    createAnalyser() { return { fftSize: 0, getFloatTimeDomainData(samples) { samples.fill(0.1); } }; }
    createMediaStreamSource(stream) { this.source = { stream, connect() {}, disconnect() { this.disconnected = true; } }; return this.source; }
    createOscillator() {
      const context = this;
      return { type: '', frequency: { value: 0 }, connect() {}, start(at) { context.started.push(at); },
        stop(at) { context.stopped.push(at); queueMicrotask(() => this.onended?.()); } };
    }
    createGain() {
      const gains = this.gains;
      return { connect() {}, gain: { setValueAtTime(value) { gains.push(value); }, linearRampToValueAtTime(value) { gains.push(value); } } };
    }
    close() { this.closed = true; return Promise.resolve(); }
  }
  const card = initializeAudioSettings({
    document, scope, mediaDevices, AudioContextCtor: FakeAudioContext,
    storage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    getInvoke: async () => async command => { calls.invoke.push(command); return command === 'get_microphone_access' ? { status: access } : null; },
    requestFrame: callback => { frames.push(callback); return frames.length; }, cancelFrame() {},
    setTimer: (callback, ms) => { const timer = { callback, ms, cleared: false }; timers.push(timer); return timer; },
    clearTimer: timer => { if (timer) timer.cleared = true; },
  });
  const $ = id => ids.get(id);
  return { card, $, document, scope, storage, calls, tracks, timers, frames, mediaDevices,
    saved: key => JSON.parse(storage.get(key) ?? 'null'),
    choose: async (id, value) => { $(id).value = value; $(id).dispatchEvent(new Event('change')); await flush(); },
    async click(id) { $(id).click(); await flush(); },
    fire(ms) { for (const timer of timers.filter(t => t.ms === ms && !t.cleared)) { timer.cleared = true; timer.callback(); } },
  };
}
const options = select => select.options.map(option => [option.value, option.textContent]);

test('the Audio devices card lives under Playback & clips, outside the saved config cards', () => {
  const playback = html.slice(html.indexOf('id="settings-playback"'), html.indexOf('id="settings-appearance"'));
  const card = playback.slice(playback.indexOf('<div class="set-card" id="audio-settings"'));
  assert.ok(card.length > 0 && playback.includes('id="audio-settings"'));
  const open = card.slice(0, card.indexOf('>'));
  assert.match(open, /data-settings-card/);
  assert.doesNotMatch(open, /data-config-group|data-native-group/, 'renderer-local choices never join dirty tracking');
  for (const word of ['audio', 'microphone', 'mic', 'input', 'output', 'speakers', 'headphones', 'narration', 'narrate', 'playback', 'sound', 'device'])
    assert.match(open, new RegExp(`data-search="[^"]*\\b${word}\\b`), word);
  for (const text of ['<h3>Audio devices</h3>', '>Narration microphone</label>', '>Playback output</label>', '>Test microphone</button>',
    '>Play test sound</button>', '>Show device names</button>', 'Saved device is not connected. Using the system default.'])
    assert.ok(card.includes(text), text);
  assert.doesNotMatch(card.slice(0, card.indexOf('</section>')), /[–—]|data-action=/);
});

test('opening Settings lists devices and the saved choices without opening the microphone', async () => {
  const f = fixture({ saved: { 'revu.narration.mic': { deviceId: 'mic-headset', label: 'Headset Mic' },
    'revu.playback.output': { deviceId: 'phones', label: 'Headphones' } } });
  await flush();
  assert.deepEqual(options(f.$('audioInput')), [['', 'System default'], ['mic-desk', 'Desk Mic'], ['mic-headset', 'Headset Mic'],
    ['communications', 'Communications - Headset Mic']]);
  assert.equal(f.$('audioInput').value, 'mic-headset');
  assert.deepEqual(options(f.$('audioOutput')), [['', 'System default'], ['spk', 'Speakers'], ['phones', 'Headphones']]);
  assert.equal(f.$('audioOutput').value, 'phones');
  for (const id of ['audio-input-missing', 'audio-output-missing', 'audio-names', 'audio-mic-meter']) assert.equal(f.$(id).hidden, true, id);
  assert.equal(f.$('audio-mic-test').disabled, false); assert.equal(f.$('audio-output-test').disabled, false);
  assert.deepEqual(f.calls.gum, [], 'visiting Settings never opens the microphone');
  assert.deepEqual(f.calls.invoke, []);
  assert.equal(f.calls.contexts.length, 0);
  f.card.dispose();
});

test('a choice saves at once with a short status, and never reaches the sidecar or dirty tracking', async () => {
  const f = fixture();
  await flush();
  assert.equal(f.$('audioInput').value, ''); assert.equal(f.$('audioOutput').value, '');
  await f.choose('audioOutput', 'spk');
  assert.deepEqual(f.saved('revu.playback.output'), { deviceId: 'spk', label: 'Speakers' });
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.outputSaved);
  assert.equal(f.$('audio-status').classList.contains('good'), true);
  f.fire(2400);
  assert.equal(f.$('audio-status').textContent, '', 'the status clears itself');
  await f.choose('audioInput', 'mic-desk');
  assert.deepEqual(f.saved('revu.narration.mic'), { deviceId: 'mic-desk', label: 'Desk Mic' });
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.inputSaved);
  await f.choose('audioInput', '');
  assert.deepEqual(f.saved('revu.narration.mic'), { deviceId: '', label: '' }, 'System default');
  assert.deepEqual(f.calls.invoke, []);
  assert.equal(f.$('audio-settings').dataset, undefined, 'the card never carries a dirty flag');
  assert.deepEqual(f.calls.gum, []);
  f.card.dispose();
});

test('a saved device that is not connected stays chosen with a notice and returns when plugged in', async () => {
  const usb = { kind: 'audioinput', deviceId: 'usb', label: 'USB Mic' };
  const f = fixture({ saved: { 'revu.narration.mic': { deviceId: 'usb', label: 'USB Mic' } } });
  await flush();
  assert.deepEqual(options(f.$('audioInput')).at(-1), ['usb', 'USB Mic (not connected)']);
  assert.equal(f.$('audioInput').value, 'usb');
  assert.equal(f.$('audio-input-missing').hidden, false);
  assert.equal(f.$('audioInput').getAttribute('aria-describedby'), 'audio-input-missing audio-input-help');
  assert.equal(f.$('audio-output-missing').hidden, true);
  assert.equal(f.$('audioOutput').getAttribute('aria-describedby'), 'audio-output-help');
  assert.deepEqual(f.saved('revu.narration.mic'), { deviceId: 'usb', label: 'USB Mic' }, 'the choice is kept');
  f.mediaDevices.list = [...DEVICES, usb];
  f.mediaDevices.dispatchEvent(new Event('devicechange'));
  await flush();
  assert.equal(f.$('audio-input-missing').hidden, true);
  assert.equal(f.$('audioInput').getAttribute('aria-describedby'), 'audio-input-help', 'a hidden notice is never read out');
  assert.deepEqual(options(f.$('audioInput')).at(-2), ['usb', 'USB Mic']);
  assert.equal(f.$('audioInput').value, 'usb');
  // A change made in another page (the narration studio) shows up here too.
  f.storage.set('revu.narration.mic', JSON.stringify({ deviceId: 'mic-desk', label: 'Desk Mic' }));
  f.scope.dispatchEvent(Object.assign(new Event('storage'), { key: 'revu.narration.mic' }));
  await flush();
  assert.equal(f.$('audioInput').value, 'mic-desk');
  f.card.dispose();
});

test('hidden device names: Show device names asks once and closes the microphone right away', async () => {
  const f = fixture({ list: HIDDEN, unlocks: DEVICES });
  await flush();
  assert.equal(f.$('audio-names').hidden, false);
  assert.deepEqual(options(f.$('audioInput')), [['', 'System default']]);
  await f.click('audio-show-names');
  assert.deepEqual(f.calls.invoke, ['get_microphone_access']);
  assert.deepEqual(f.calls.gum, [{ audio: true, video: false }]);
  assert.equal(f.tracks[0].stopped, true, 'the stream opened only for names is stopped at once');
  assert.equal(f.$('audio-names').hidden, true);
  assert.equal(options(f.$('audioInput')).length, 4);
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.namesShown);
  assert.equal(f.calls.contexts.length, 0);
  f.card.dispose();

  const denied = fixture({ list: HIDDEN, access: 'denied' });
  await flush();
  await denied.click('audio-show-names');
  assert.deepEqual(denied.calls.gum, []);
  assert.equal(denied.$('audio-status').textContent, MIC_COPY.denied);
  denied.card.dispose();
});

test('Test microphone shows a live level and stops when toggled off, hidden, timed out or unloaded', async () => {
  const f = fixture({ saved: { 'revu.narration.mic': { deviceId: 'mic-desk', label: 'Desk Mic' } } });
  await flush();
  await f.click('audio-mic-test');
  assert.deepEqual(f.calls.gum, [{ audio: { deviceId: { exact: 'mic-desk' }, echoCancellation: true, noiseSuppression: true,
    autoGainControl: true }, video: false }]);
  assert.equal(f.$('audio-mic-meter').hidden, false);
  assert.equal(f.$('audio-mic-test').textContent, 'Stop test');
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.testing);
  f.frames.shift()();
  assert.equal(f.$('audio-mic-level').style.width, '40%');
  assert.equal(f.$('audio-mic-meter').getAttribute('aria-valuenow'), '40');
  assert.equal(f.card.testing, true);
  // Toggled off.
  await f.click('audio-mic-test');
  assert.equal(f.tracks[0].stopped, true); assert.equal(f.calls.contexts[0].closed, true);
  assert.equal(f.calls.contexts[0].source.disconnected, true);
  assert.equal(f.$('audio-mic-meter').hidden, true); assert.equal(f.$('audio-mic-test').textContent, 'Test microphone');
  assert.equal(f.$('audio-status').textContent, '');
  assert.equal(f.card.testing, false);
  // The page is hidden.
  await f.click('audio-mic-test');
  f.document.hidden = true; f.document.visibilityState = 'hidden';
  f.document.dispatchEvent(new Event('visibilitychange'));
  assert.equal(f.tracks[1].stopped, true); assert.equal(f.$('audio-mic-meter').hidden, true);
  f.document.hidden = false; f.document.visibilityState = 'visible';
  // Thirty seconds pass.
  await f.click('audio-mic-test');
  assert.equal(f.timers.filter(timer => timer.ms === MIC_TEST_LIMIT_MS && !timer.cleared).length, 1);
  f.fire(MIC_TEST_LIMIT_MS);
  assert.equal(f.tracks[2].stopped, true);
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.testTimedOut);
  // Picking another microphone during a test switches the test to it.
  await f.click('audio-mic-test');
  await f.choose('audioInput', 'mic-headset');
  assert.equal(f.tracks[3].stopped, true);
  assert.deepEqual(f.calls.gum.at(-1).audio.deviceId, { exact: 'mic-headset' });
  // Leaving the page.
  f.scope.dispatchEvent(new Event('pagehide'));
  assert.equal(f.tracks[4].stopped, true);
  assert.ok(f.calls.contexts.every(context => context.closed));
  await f.click('audio-mic-test');
  assert.equal(f.calls.gum.length, 5, 'a left page never opens the microphone again');
});

test('a test stopped while the microphone opens releases it as soon as it arrives', async () => {
  let open;
  const f = fixture({ gumGate: new Promise(resolve => { open = resolve; }) });
  await flush();
  await f.click('audio-mic-test');
  assert.equal(f.calls.gum.length, 1);
  await f.click('audio-mic-test');
  open();
  await flush();
  assert.equal(f.tracks[0].stopped, true);
  assert.equal(f.calls.contexts.length, 0);
  assert.equal(f.$('audio-mic-meter').hidden, true);
  f.card.dispose();
});

test('microphone failures explain themselves and leave nothing open', async () => {
  const denied = fixture({ access: 'denied' });
  await flush();
  await denied.click('audio-mic-test');
  assert.deepEqual(denied.calls.gum, []);
  assert.equal(denied.$('audio-status').textContent, MIC_COPY.denied);
  assert.equal(denied.$('audio-mic-meter').hidden, true);
  denied.card.dispose();
  const busy = fixture({ gumError: 'NotReadableError' });
  await flush();
  await busy.click('audio-mic-test');
  assert.equal(busy.$('audio-status').textContent, MIC_COPY.busy);
  assert.equal(busy.$('audio-mic-test').textContent, 'Test microphone');
  busy.card.dispose();
  // A listed microphone that refuses its id falls back to the system default.
  const refused = fixture({ saved: { 'revu.narration.mic': { deviceId: 'mic-desk', label: 'Desk Mic' } } });
  await flush();
  let first = true;
  const gum = refused.mediaDevices.getUserMedia;
  refused.mediaDevices.getUserMedia = async constraints => {
    if (first) { first = false; refused.calls.gum.push(constraints); throw Object.assign(new Error('x'), { name: 'OverconstrainedError' }); }
    return gum(constraints);
  };
  await refused.click('audio-mic-test');
  assert.deepEqual(refused.calls.gum.map(constraints => constraints.audio.deviceId), [{ exact: 'mic-desk' }, undefined]);
  assert.equal(refused.card.testing, true);
  refused.card.dispose();
  assert.equal(refused.tracks[0].stopped, true);
});

test('Play test sound plays a short, quiet tone on the chosen output, or says it used the default', async () => {
  const f = fixture({ saved: { 'revu.playback.output': { deviceId: 'phones', label: 'Headphones' } } });
  await flush();
  await f.click('audio-output-test');
  const [context] = f.calls.contexts;
  assert.deepEqual(context.sinks, ['phones']);
  assert.ok(context.started.length === 1 && context.stopped.length === 1);
  assert.ok(context.stopped[0] - context.started[0] <= 1, 'the tone is short');
  assert.ok(Math.max(...context.gains) <= 0.1, 'the tone is quiet');
  assert.equal(context.closed, true);
  assert.equal(f.$('audio-status').textContent, AUDIO_SETTINGS_COPY.tonePlayed);
  assert.equal(f.$('audio-output-test').disabled, false);
  assert.deepEqual(f.calls.gum, [], 'a test sound never opens the microphone');
  f.card.dispose();

  const gone = fixture({ saved: { 'revu.playback.output': { deviceId: 'usb-dac', label: 'USB DAC' } } });
  await flush();
  assert.equal(gone.$('audio-output-missing').hidden, false);
  await gone.click('audio-output-test');
  assert.deepEqual(gone.calls.contexts[0].sinks, [], 'the default needs no switch');
  assert.equal(gone.$('audio-status').textContent, AUDIO_SETTINGS_COPY.toneFallback);
  gone.card.dispose();
});

test('a host without device access keeps the card readable and inert', async () => {
  const ids = new Map(IDS.map(id => [id, new El(id)]));
  const document = Object.assign(new EventTarget(), { getElementById: id => ids.get(id) || null, createElement: () => ({}) });
  const card = initializeAudioSettings({ document, scope: new EventTarget(), mediaDevices: null, AudioContextCtor: null,
    storage: null, getInvoke: async () => null, setTimer: () => 0, clearTimer() {} });
  await flush();
  assert.equal(ids.get('audioInput').disabled, true); assert.equal(ids.get('audioOutput').disabled, true);
  assert.equal(ids.get('audio-mic-test').disabled, true); assert.equal(ids.get('audio-output-test').disabled, true);
  assert.equal(ids.get('audio-status').textContent, AUDIO_SETTINGS_COPY.unavailable);
  card.dispose();
  assert.equal(initializeAudioSettings({ document: { getElementById: () => null } }), null, 'no card, nothing to do');
});
