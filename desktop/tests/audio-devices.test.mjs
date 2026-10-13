import test from 'node:test';
import assert from 'node:assert/strict';
import { applyOutputSink, AUDIO_PREF_KEYS, bindOutputSink, listAudioDevices, onAudioPrefsChange, readAudioPref,
  resolveAudioDevice, resolveSavedDevice, saveAudioPref } from '../ui/audio-devices.mjs';

const flush = async (n = 6) => { for (let i = 0; i < n; i++) await new Promise(resolve => setTimeout(resolve, 0)); };
function memoryStorage(entries = {}) {
  const map = new Map(Object.entries(entries));
  return { map, getItem: key => map.get(key) ?? null, setItem: (key, value) => map.set(key, String(value)) };
}
function devices(list) {
  const target = new EventTarget();
  target.list = list;
  target.enumerateDevices = async () => target.list.map(device => ({ groupId: '', ...device }));
  return target;
}
const DEVICES = [
  { kind: 'audioinput', deviceId: 'default', label: 'Default - Desk Mic' },
  { kind: 'audioinput', deviceId: 'communications', label: 'Communications - Headset Mic' },
  { kind: 'audioinput', deviceId: 'mic-desk', label: 'Desk Mic' },
  { kind: 'audioinput', deviceId: 'mic-headset', label: 'Headset Mic' },
  { kind: 'audiooutput', deviceId: 'default', label: 'Default - Speakers' },
  { kind: 'audiooutput', deviceId: 'spk', label: 'Speakers' },
  { kind: 'audiooutput', deviceId: 'phones', label: 'Headphones' },
  { kind: 'videoinput', deviceId: 'cam', label: 'Camera' },
];
const storageEvent = key => Object.assign(new Event('storage'), { key });

test('a saved device resolves by id, then by name, else to the system default flagged missing', () => {
  const list = [{ deviceId: 'a', label: 'USB Mic' }, { deviceId: 'b', label: 'Headset Mic' }];
  assert.deepEqual(resolveSavedDevice(null, list), { deviceId: '', label: '', matchedBy: 'default', missing: false });
  assert.deepEqual(resolveSavedDevice({ deviceId: 'default', label: 'x' }, list).matchedBy, 'default');
  assert.deepEqual(resolveSavedDevice({ deviceId: 'b', label: 'old name' }, list),
    { deviceId: 'b', label: 'Headset Mic', matchedBy: 'id', missing: false });
  // Ids can change (cleared site data, a driver update); the device name still finds it.
  assert.deepEqual(resolveSavedDevice({ deviceId: 'stale', label: ' usb MIC ' }, list),
    { deviceId: 'a', label: 'USB Mic', matchedBy: 'label', missing: false });
  assert.deepEqual(resolveSavedDevice({ deviceId: 'gone', label: 'Old Mic' }, list),
    { deviceId: '', label: '', matchedBy: 'default', missing: true });
  assert.equal(resolveSavedDevice({ deviceId: 'gone', label: '' }, list).missing, true, 'an unnamed device is never matched by name');
  // With names hidden, or no list at all, the saved id is tried as is.
  assert.deepEqual(resolveSavedDevice({ deviceId: 'gone', label: 'Old Mic' }, [], { labelsHidden: true }),
    { deviceId: 'gone', label: 'Old Mic', matchedBy: 'unverified', missing: false });
  assert.equal(resolveSavedDevice({ deviceId: 'gone', label: 'Old Mic' }, null).matchedBy, 'unverified');
});

test('preferences read and write the shared keys and tolerate blocked or corrupt storage', () => {
  assert.deepEqual(AUDIO_PREF_KEYS, { input: 'revu.narration.mic', output: 'revu.playback.output' });
  const storage = memoryStorage({ 'revu.narration.mic': JSON.stringify({ deviceId: 'mic-desk', label: 'Desk Mic' }) });
  const target = new EventTarget();
  assert.deepEqual(readAudioPref('input', { storage }), { deviceId: 'mic-desk', label: 'Desk Mic' });
  assert.equal(readAudioPref('output', { storage }), null);
  assert.equal(saveAudioPref('output', { deviceId: 'phones', label: 'Headphones' }, { storage, target }), true);
  assert.deepEqual(JSON.parse(storage.map.get('revu.playback.output')), { deviceId: 'phones', label: 'Headphones' });
  for (const deviceId of ['', 'default', null, undefined]) {
    assert.equal(saveAudioPref('audiooutput', { deviceId, label: 'Default - Speakers' }, { storage, target }), true);
    assert.deepEqual(JSON.parse(storage.map.get('revu.playback.output')), { deviceId: '', label: '' }, String(deviceId));
    assert.equal(readAudioPref('output', { storage }), null);
  }
  for (const raw of ['{', '"text"', 'null', '{"deviceId":4}', '{"deviceId":"default"}', '{"deviceId":""}']) {
    assert.equal(readAudioPref('input', { storage: memoryStorage({ 'revu.narration.mic': raw }) }), null, raw);
  }
  assert.deepEqual(readAudioPref('input', { storage: memoryStorage({ 'revu.narration.mic': '{"deviceId":"x","label":7}' }) }),
    { deviceId: 'x', label: '' });
  const blocked = { getItem() { throw new Error('SecurityError'); }, setItem() { throw new Error('QuotaExceededError'); } };
  assert.equal(readAudioPref('input', { storage: blocked }), null);
  assert.equal(saveAudioPref('input', { deviceId: 'mic-desk' }, { storage: blocked, target }), false);
  assert.equal(readAudioPref('input', { storage: null }), null);
  assert.equal(saveAudioPref('input', { deviceId: 'mic-desk' }, { storage: null, target }), false);
});

test('device lists hide the virtual default, keep communications last and report hidden names', async () => {
  const media = devices(DEVICES);
  assert.deepEqual(await listAudioDevices('input', { mediaDevices: media }), { available: true, labelsHidden: false, devices: [
    { deviceId: 'mic-desk', label: 'Desk Mic', groupId: '' }, { deviceId: 'mic-headset', label: 'Headset Mic', groupId: '' },
    { deviceId: 'communications', label: 'Communications - Headset Mic', groupId: '' }] });
  assert.deepEqual((await listAudioDevices('audiooutput', { mediaDevices: media })).devices.map(device => device.deviceId), ['spk', 'phones']);
  // Before microphone permission Chromium returns one nameless entry per kind.
  media.list = [{ kind: 'audioinput', deviceId: '', label: '' }, { kind: 'audiooutput', deviceId: '', label: '' }];
  assert.deepEqual(await listAudioDevices('input', { mediaDevices: media }), { available: true, labelsHidden: true, devices: [] });
  for (const mediaDevices of [null, {}, { enumerateDevices: async () => { throw new Error('NotAllowedError'); } }]) {
    assert.deepEqual(await listAudioDevices('input', { mediaDevices }), { devices: [], labelsHidden: false, available: false });
  }
});

test('resolveAudioDevice combines the saved choice with the connected devices', async () => {
  const mediaDevices = devices(DEVICES);
  const storage = memoryStorage({ 'revu.playback.output': JSON.stringify({ deviceId: 'old-phones', label: 'Headphones' }) });
  assert.equal((await resolveAudioDevice('output', { storage, mediaDevices })).deviceId, 'phones');
  storage.setItem('revu.playback.output', JSON.stringify({ deviceId: 'usb-dac', label: 'USB DAC' }));
  const missing = await resolveAudioDevice('output', { storage, mediaDevices });
  assert.equal(missing.deviceId, ''); assert.equal(missing.missing, true);
  assert.deepEqual(missing.saved, { deviceId: 'usb-dac', label: 'USB DAC' }, 'the saved choice is kept for when it returns');
  assert.equal((await resolveAudioDevice('output', { storage, mediaDevices: null })).deviceId, 'usb-dac', 'unknown list: try the id');
});

test('changes arrive from other pages, from this page and from plugged devices, until unsubscribed', async () => {
  const target = new EventTarget(), mediaDevices = devices(DEVICES), storage = memoryStorage();
  const changes = [];
  const stop = onAudioPrefsChange(change => changes.push(change), { target, mediaDevices, storage });
  target.dispatchEvent(storageEvent('revu.narration.mic'));
  target.dispatchEvent(storageEvent('revu.playback.output'));
  target.dispatchEvent(storageEvent('revu.unrelated'));
  target.dispatchEvent(storageEvent(null)); // localStorage.clear()
  mediaDevices.dispatchEvent(new Event('devicechange'));
  saveAudioPref('output', { deviceId: 'phones', label: 'Headphones' }, { target, storage });
  saveAudioPref('input', { deviceId: 'mic-desk', label: 'Desk Mic' }, { target, storage });
  assert.deepEqual(changes, [{ which: 'input', reason: 'storage' }, { which: 'output', reason: 'storage' },
    { which: 'both', reason: 'storage' }, { which: 'both', reason: 'devicechange' },
    { which: 'output', reason: 'saved' }, { which: 'input', reason: 'saved' }]);
  stop(); stop();
  target.dispatchEvent(storageEvent('revu.narration.mic'));
  mediaDevices.dispatchEvent(new Event('devicechange'));
  saveAudioPref('input', { deviceId: 'mic-headset' }, { target, storage });
  assert.equal(changes.length, 6);
  // A throwing listener never breaks the event or other listeners.
  const quiet = onAudioPrefsChange(() => { throw new Error('listener bug'); }, { target, mediaDevices });
  assert.doesNotThrow(() => target.dispatchEvent(storageEvent('revu.narration.mic')));
  quiet();
  assert.equal(typeof onAudioPrefsChange(null), 'function');
});

test('applyOutputSink routes to the chosen device, skips unsupported targets and never throws', async () => {
  const mediaDevices = devices(DEVICES);
  const storage = memoryStorage({ 'revu.playback.output': JSON.stringify({ deviceId: 'phones', label: 'Headphones' }) });
  const calls = [];
  const element = { sinkId: '', async setSinkId(id) { calls.push(id); this.sinkId = id; } };
  assert.deepEqual(await applyOutputSink(element, { storage, mediaDevices }), { ok: true, sinkId: 'phones', fallback: false });
  assert.deepEqual(await applyOutputSink(element, { storage, mediaDevices }), { ok: true, sinkId: 'phones', fallback: false });
  assert.deepEqual(calls, ['phones'], 'an unchanged sink is not set again');
  assert.deepEqual(await applyOutputSink({}, { storage, mediaDevices }), { ok: false, sinkId: null, fallback: false });
  assert.deepEqual(await applyOutputSink(null), { ok: false, sinkId: null, fallback: false });

  // A refused device logs once and the element plays on the system default.
  const warnings = [], warn = console.warn;
  console.warn = (...args) => warnings.push(args);
  try {
    const refusing = { sinkId: 'spk', calls: [], async setSinkId(id) {
      this.calls.push(id);
      if (id) throw Object.assign(new Error('denied'), { name: 'NotAllowedError' });
      this.sinkId = id;
    } };
    assert.deepEqual(await applyOutputSink(refusing, { storage, mediaDevices }), { ok: false, sinkId: '', fallback: true });
    assert.deepEqual(refusing.calls, ['phones', '']);
    const throwing = { sinkId: '', setSinkId() { throw new TypeError('sync failure'); } };
    assert.deepEqual(await applyOutputSink(throwing, { storage, mediaDevices }), { ok: false, sinkId: '', fallback: true });
    assert.equal(warnings.length, 1, 'failures are logged once');
  } finally { console.warn = warn; }

  // Calls on one target apply in order, so the last choice wins.
  let release;
  const slow = { sinkId: '', applied: [], setSinkId(id) {
    this.applied.push(id);
    return id === 'phones' ? new Promise(resolve => { release = () => { this.sinkId = id; resolve(); }; }) : (this.sinkId = id, Promise.resolve());
  } };
  const first = applyOutputSink(slow, { resolved: { deviceId: 'phones' } });
  const second = applyOutputSink(slow, { resolved: { deviceId: 'spk' } });
  await flush();
  assert.deepEqual(slow.applied, ['phones']);
  release();
  await Promise.all([first, second]);
  assert.deepEqual(slow.applied, ['phones', 'spk']);
  assert.equal(slow.sinkId, 'spk');
});

test('bound players follow the output choice and fall back to the default when the device is unplugged', async () => {
  const target = new EventTarget(), mediaDevices = devices(DEVICES);
  const storage = memoryStorage({ 'revu.playback.output': JSON.stringify({ deviceId: 'phones', label: 'Headphones' }) });
  const video = { sinkId: '', sinks: [], async setSinkId(id) { this.sinks.push(id); this.sinkId = id; } };
  const narration = { sinkId: '', sinks: [], async setSinkId(id) { this.sinks.push(id); this.sinkId = id; } };
  const release = bindOutputSink([video, narration, null, {}], { target, mediaDevices, storage });
  await flush();
  assert.deepEqual([video.sinks, narration.sinks], [['phones'], ['phones']]);
  // Settings (another page) picks the speakers.
  storage.setItem('revu.playback.output', JSON.stringify({ deviceId: 'spk', label: 'Speakers' }));
  target.dispatchEvent(storageEvent('revu.playback.output'));
  await flush();
  assert.equal(video.sinkId, 'spk');
  // An input change is not an output change.
  target.dispatchEvent(storageEvent('revu.narration.mic'));
  await flush();
  assert.deepEqual(video.sinks, ['phones', 'spk']);
  // The speakers are unplugged: playback continues on the system default and the
  // choice comes back when they return.
  mediaDevices.list = DEVICES.filter(device => device.deviceId !== 'spk');
  mediaDevices.dispatchEvent(new Event('devicechange'));
  await flush();
  assert.equal(video.sinkId, ''); assert.equal(narration.sinkId, '');
  mediaDevices.list = DEVICES;
  mediaDevices.dispatchEvent(new Event('devicechange'));
  await flush();
  assert.equal(video.sinkId, 'spk');
  release();
  saveAudioPref('output', { deviceId: 'phones', label: 'Headphones' }, { target, storage });
  await flush();
  assert.equal(video.sinkId, 'spk', 'a released player stops following');
  assert.equal(typeof bindOutputSink([{}]), 'function', 'nothing to bind is a no-op');
});
