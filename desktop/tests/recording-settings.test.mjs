import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import { createRecordingSettingsModel, recordingStatusText } from '../ui/recording-settings.mjs';
import { validateCommand } from '../ui/platform/commands.mjs';

const status = { state: 'unavailable', available: false, message: 'Recording is not available in this build yet.',
  settings: { enabled: true, preset: '720p30' } };

test('native recorder model retains developer opt-in independently of the product UI release gate', async () => {
  const views = [], calls = [];
  const model = createRecordingSettingsModel({ invoke: async (command, args) => {
    calls.push([command, args]); return status;
  }, renderRecording: value => views.push(value), renderStatus: value => views.push(recordingStatusText(value)), renderBackground() {} });
  await model.saveRecording({ enabled: true, preset: '720p30' });
  assert.deepEqual(calls, [['save_recording_settings', { payload: status.settings }]]);
  assert.deepEqual(views, ['Recording unavailable. Recording is not available in this build yet.', status.settings]);
});

const uiSource = (await readFile(new URL('../ui/recording-settings.mjs', import.meta.url), 'utf8'))
  .replace(/^import .*?;\r?\n/gm, '').replace(/^export /gm, '');

async function uiFixture(invoke) {
  const ids = new Map(), calls = [], intervals = [], documentListeners = new Map(), windowListeners = new Map();
  const element = () => {
    const classes = new Set(), attributes = new Map(), listeners = new Map();
    return {
      disabled: false, textContent: '', value: '720p30', hidden: false,
      classList: { toggle: (name, on) => on ? classes.add(name) : classes.delete(name), contains: name => classes.has(name) },
      setAttribute: (name, value) => attributes.set(name, value), getAttribute: name => attributes.get(name),
      addEventListener: (type, callback) => listeners.set(type, callback),
      async emit(type) { await listeners.get(type)?.(); },
    };
  };
  const $ = id => { if (!ids.has(id)) ids.set(id, element()); return ids.get(id); };
  const document = { hidden: false, getElementById: $,
    addEventListener: (type, callback) => documentListeners.set(type, callback),
    removeEventListener: type => documentListeners.delete(type) };
  const scope = { addEventListener: (type, callback) => windowListeners.set(type, callback),
    setInterval(callback) { intervals.push(callback); return intervals.length; }, clearInterval() {} };
  const context = vm.createContext({ getInvoke: async () => invoke ? async (command, args) => {
    calls.push({ command, args: args ? JSON.parse(JSON.stringify(args)) : undefined });
    return invoke(command, args);
  } : null });
  vm.runInContext(`${uiSource}\nglobalThis.initialize = initializeRecordingSettings;`, context);
  await context.initialize({ document, scope });
  return { $, calls, intervals, async visible() { await documentListeners.get('visibilitychange')?.(); } };
}

test('unreleased built-in controls stay disabled despite native access while startup preferences remain usable', async () => {
  const background = { closeToTray: false, startWithWindows: false, trayAvailable: true, startupAvailable: true };
  const f = await uiFixture(async (command, args) => {
    if (command === 'get_recording_status') return { state: 'idle', available: true,
      message: 'Developer access is approved.', settings: { enabled: true, preset: '1080p60' } };
    if (command === 'save_background_settings') return { ...background, ...args.payload };
    return background;
  });
  for (const id of ['recordingEnabled', 'recordingPreset', 'save-recording-settings', 'open-recordings-folder']) assert.equal(f.$(id).disabled, true, id);
  assert.equal(f.$('recordingEnabled').getAttribute('aria-checked'), 'false');
  assert.match(f.$('recording-status').textContent, /coming in a future update.*Use Ascent recordings above/);
  assert.doesNotMatch(f.$('recording-status').textContent, /Developer|approved|key/);
  assert.equal(f.$('recording-coming-soon').hidden, false);
  await f.$('save-recording-settings').emit('click');
  await f.$('open-recordings-folder').emit('click');
  await f.visible();
  assert.equal(f.intervals.length, 0, 'unreleased recorder status does not poll');
  assert.ok(f.calls.every(call => !['save_recording_settings', 'open_recordings_folder'].includes(call.command)));
  assert.equal(f.$('closeToTray').disabled, false); assert.equal(f.$('startWithWindows').disabled, false);
  f.$('closeToTray').setAttribute('aria-checked', 'true');
  await f.$('save-background-settings').emit('click');
  assert.deepEqual(f.calls.find(call => call.command === 'save_background_settings').args,
    { payload: { closeToTray: true, startWithWindows: false } });
  assert.equal(f.$('background-settings-status').textContent, 'Saved.');
  assert.equal(f.$('recordingEnabled').disabled, true);
});

test('browser preview and failed native initialization retain the same coming-soon message', async () => {
  for (const invoke of [null, async command => {
    if (command === 'get_recording_status') throw new Error('Missing developer credentials');
    return { closeToTray: false, startWithWindows: false };
  }]) {
    const f = await uiFixture(invoke);
    for (const id of ['recordingEnabled', 'recordingPreset', 'save-recording-settings', 'open-recordings-folder']) assert.equal(f.$(id).disabled, true, id);
    assert.equal(f.$('recording-status').textContent,
      'Automatic match recording is coming in a future update. Use Ascent recordings above for now.');
    assert.equal(f.intervals.length, 0);
  }
});

test('poll reads status without overwriting a preset the user is editing', async () => {
  const edits = [], statuses = [];
  const model = createRecordingSettingsModel({ invoke: async () => status,
    renderRecording: value => edits.push(value), renderStatus: value => statuses.push(value), renderBackground() {} });
  await model.refresh();
  assert.deepEqual(edits, []);
  assert.deepEqual(statuses, [status]);
});

test('a poll started before save cannot overwrite the resulting recording status', async () => {
  let complete; const statuses = [];
  const saved = { ...status, state: 'disabled', message: '', settings: { enabled: false, preset: '1080p30' } };
  const model = createRecordingSettingsModel({ invoke: command => command === 'get_recording_status'
    ? new Promise(resolve => { complete = resolve; }) : Promise.resolve(saved),
  renderRecording() {}, renderStatus: value => statuses.push(value), renderBackground() {} });
  const poll = model.refresh();
  await model.saveRecording(saved.settings);
  complete(status); await poll;
  assert.deepEqual(statuses, [saved]);
});

test('recording and background initialization failures are independent', async () => {
  const backgrounds = [], statuses = [];
  const background = { closeToTray: false, startWithWindows: false };
  const model = createRecordingSettingsModel({ invoke: async command => {
    if (command === 'get_recording_status') throw new Error('package unavailable');
    return background;
  }, renderRecording: () => assert.fail('failed load enabled editing'),
  renderStatus: value => statuses.push(value), renderBackground: value => backgrounds.push(value) });
  await model.load();
  assert.deepEqual(backgrounds, [background]);
  assert.match(recordingStatusText(statuses[0]), /could not be loaded/);
});

test('navigation disposes pending initialization, polling, and saves without late DOM writes', async () => {
  const completions = [];
  const model = createRecordingSettingsModel({ invoke: () => new Promise(resolve => completions.push(resolve)),
    renderRecording: () => assert.fail('late recording render'), renderStatus: () => assert.fail('late status render'),
    renderBackground: () => assert.fail('late background render') });
  const load = model.load(); const refresh = model.refresh();
  const recordingSave = model.saveRecording(status.settings);
  const backgroundSave = model.saveBackground({ closeToTray: true, startWithWindows: false });
  model.dispose(); completions.forEach(resolve => resolve(status));
  await Promise.all([load, refresh, recordingSave, backgroundSave]);
});

test('save errors remain errors rather than falsely rendering a saved state', async () => {
  const model = createRecordingSettingsModel({ invoke: async () => ({ ok: false, message: 'Disk is full' }),
    renderRecording: () => assert.fail('failed save render'), renderStatus: () => assert.fail('failed status render'),
    renderBackground: () => assert.fail('failed background render') });
  await assert.rejects(model.saveRecording(status.settings), /Disk is full/);
  await assert.rejects(model.saveBackground({ closeToTray: true, startWithWindows: false }), /Disk is full/);
});

test('new native commands permit only their declared envelopes', () => {
  for (const command of ['get_recording_status', 'get_background_settings', 'open_recordings_folder']) {
    assert.equal(validateCommand(command).method, 'NATIVE');
    assert.throws(() => validateCommand(command, { path: 'arbitrary' }), /Unexpected argument/);
  }
  for (const command of ['save_recording_settings', 'save_background_settings']) {
    assert.equal(validateCommand(command, { payload: {} }).method, 'NATIVE');
    assert.throws(() => validateCommand(command), /Invalid argument/);
    assert.throws(() => validateCommand(command, { payload: [] }), /Invalid argument/);
  }
});
