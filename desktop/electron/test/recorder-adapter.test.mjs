import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, rm, writeFile, mkdir, symlink } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { createOverwolfRecorder, RECORDING_PRESETS } from '../recorder-adapter.mjs';
import { recorderError } from '../recorder-policy.mjs';

const nextTurn = () => new Promise(resolve => setImmediate(resolve));
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function builder(type) {
  return { videoSettings: {}, videoEncoderSettings: { type }, audioEncoder: { type: 'ffmpeg_aac', codec: 'aac' },
    audioSettings: { inputs: [], outputs: [], applications: [] }, sources: [],
    addGameSource(properties) { this.sources.push({ type: 'Game', properties }); },
    addApplicationAudioCapture({ processName }, settings) { this.audioSettings.applications.push({ name: processName, type: 'output', ...settings }); },
    build() { return this; } };
}
async function fixture(t, options = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-adapter-'));
  const games = [], unavailable = [], stopped = [], calls = { start: 0, stop: 0, scan: 0 };
  let built, providerStop;
  const encoder = options.encoder ?? 'obs_nvenc_h264_tex';
  const recorder = Object.assign(new EventEmitter(), {
    ffmpegPath: path.join(root, 'ffmpeg.exe'), ffprobePath: path.join(root, 'ffprobe.exe'),
    registerGames: async value => { calls.registration = value; }, isActive: async () => false,
    queryInformation: async () => ({ video: { defaultEncoder: encoder, encoders: [{ type: encoder, codec: 'h264' }] },
      audio: { encoders: [{ type: 'ffmpeg_aac', codec: 'aac' }] } }),
    createSettingsBuilder: async value => { calls.builder = value; return built = builder(value.videoEncoder); },
    startRecording: async (value, settings, callback) => { calls.start++; calls.options = value; calls.settings = settings; providerStop = callback; },
    stopRecording: async () => { calls.stop++; },
  });
  const adapter = createOverwolfRecorder({ recorder, onGame: value => games.push(value), onUnavailable: code => unavailable.push(code),
    inspectGame: async pid => ({ pid, width: 2560, height: 1440 }),
    scanGames: async () => { calls.scan++; return []; }, isAlive: () => true, ...options });
  t.after(async () => { adapter.dispose(); await nextTurn(); await rm(root, { recursive: true }); });
  await adapter.initialize(); await nextTurn();
  const start = extra => adapter.start({ pid: 222, preset: '720p30', outputDirectory: root, onStopped: value => stopped.push(value), ...extra });
  const finish = extra => providerStop?.({ hasError: false, filePath: path.join(root, 'capture.mp4'), duration: 5000,
    startTimeEpoch: 1_700_000_000_000, reason: 0, splitCount: 0, ...extra });
  return { adapter, recorder, calls, games, unavailable, stopped, root, start, finish, built: () => built };
}

for (const encoder of ['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2']) {
  test(`${encoder} uses only the game and its application audio with explicit recording presets`, async t => {
    const f = await fixture(t, { encoder });
    for (const [preset, target] of Object.entries(RECORDING_PRESETS)) {
      const result = await f.start({ preset });
      assert.equal(result.encoder, encoder); assert.equal(result.width, target.width); assert.equal(result.height, target.height);
      assert.equal(result.fps, target.fps); assert.equal(result.bitrate, target.bitrate);
      assert.deepEqual(f.calls.registration, { gamesIds: [5426], all: false, includeUnsupported: false });
      assert.deepEqual(f.calls.builder, { videoEncoder: encoder, audioEncoder: 'ffmpeg_aac', includeDefaultAudioSources: false, separateAudioTracks: false });
      assert.deepEqual(f.calls.settings.sources, [{ type: 'Game', properties: { gameProcess: 222, captureCursor: true, captureOverlays: false, limitFramerate: true, sliCompatibility: false } }]);
      assert.deepEqual(f.calls.settings.audioSettings.applications, [{ name: 'League of Legends.exe', type: 'output', volume: 1, filters: [] }]);
      assert.deepEqual(f.calls.options, { filePath: path.join(f.root, 'capture'), fileFormat: 'fragmented_mp4', autoShutdownOnGameExit: true });
      f.finish();
    }
  });
}

test('native client dimensions determine aspect-preserving output without upscaling', async t => {
  const f = await fixture(t, { inspectGame: async pid => ({ pid, width: 1024, height: 768 }) });
  const result = await f.start({ preset: '1080p60' });
  assert.deepEqual([result.width, result.height], [1024, 768]);
  assert.deepEqual([f.calls.settings.videoSettings.baseWidth, f.calls.settings.videoSettings.baseHeight], [1024, 768]);
  f.finish();
});

test('window-not-ready is a fixed transient failure that can retry before native capture starts', async t => {
  let ready = false;
  const f = await fixture(t, { inspectGame: async pid => {
    if (!ready) throw recorderError('source-window-not-ready');
    return { pid, width: 1920, height: 1080 };
  } });
  await assert.rejects(f.start(), { code: 'source-window-not-ready' });
  assert.equal(f.calls.start, 0); assert.equal(f.calls.stop, 0);
  ready = true; await f.start(); assert.equal(f.calls.start, 1); f.finish();
});

test('software-only availability and an already active provider never trigger fallback or stop', async t => {
  const f = await fixture(t, { encoder: 'obs_x264' });
  await assert.rejects(f.start(), { code: 'hardware-encoder-unavailable' });
  f.recorder.isActive = async () => true;
  await assert.rejects(f.start(), { code: 'recorder-already-active' });
  assert.equal(f.calls.start, 0); assert.equal(f.calls.stop, 0);
});

for (const [name, change] of Object.entries({
  microphone: b => b.audioSettings.inputs.push({ name: 'device' }),
  systemAudio: b => b.audioSettings.outputs.push({ name: 'device' }),
  desktopSource: b => b.sources.push({ type: 'Display' }),
  wrongPid: b => { b.sources[0].properties.gameProcess = 333; },
  overlays: b => { b.sources[0].properties.captureOverlays = true; },
  audioFilter: b => b.audioSettings.applications[0].filters.push({ type: 'noise_suppress_filter' }),
})) {
  test(`built graph rejects ${name}`, async t => {
    const f = await fixture(t);
    f.recorder.createSettingsBuilder = async value => {
      const b = builder(value.videoEncoder); b.build = () => { change(b); return b; }; return b;
    };
    await assert.rejects(f.start(), { code: 'capture-scope-mismatch' });
    assert.equal(f.calls.start, 0); assert.equal(f.calls.stop, 0);
  });
}

test('cancellation while capability setup is pending cannot later start or stop the provider', async t => {
  const f = await fixture(t); const pending = deferred(), abort = new AbortController();
  const information = await f.recorder.queryInformation();
  f.recorder.queryInformation = () => pending.promise;
  const starting = f.start({ signal: abort.signal });
  await delay(10); abort.abort();
  await assert.rejects(starting, { code: 'recording-aborted' });
  pending.resolve(information); await nextTurn();
  assert.equal(f.calls.start, 0); assert.equal(f.calls.stop, 0);
});

test('late native start after cancellation is stopped again and completion remains single', async t => {
  const f = await fixture(t); const pending = deferred(), entered = deferred(), abort = new AbortController();
  let callback;
  f.recorder.startRecording = (_o, _s, stopped) => { f.calls.start++; callback = stopped; entered.resolve(); return pending.promise; };
  const starting = f.start({ signal: abort.signal }); await entered.promise; abort.abort();
  await assert.rejects(starting, { code: 'recording-aborted' }); await nextTurn(); assert.equal(f.calls.stop, 1);
  pending.resolve(); await nextTurn(); await nextTurn(); assert.equal(f.calls.stop, 2);
  callback({ hasError: true, error: 'private native message', duration: 0, stats: { cpuUsage: 'private payload' } });
  callback({ hasError: false, duration: 5000 });
  assert.equal(f.stopped.length, 1); assert.equal(f.stopped[0].error, 'recording-provider-error');
  assert.equal(f.stopped[0].stats.cpuUsage, null); assert.equal(JSON.stringify(f.stopped).includes('private'), false);
});

test('only exact League events are emitted and a tracked live game avoids repeated discovery', async t => {
  const f = await fixture(t, { pollMs: 5 });
  const game = pid => ({ id: 5426, type: 'Game', processInfo: { pid, fullPath: 'C:\\Riot\\League of Legends.exe' } });
  f.recorder.emit('game-launched', { ...game(222), type: 'Launcher' });
  f.recorder.emit('game-launched', { ...game(222), id: 111 });
  f.recorder.emit('game-launched', game(222));
  f.recorder.emit('game-exit', game(333));
  const scans = f.calls.scan; await delay(20); assert.equal(f.calls.scan, scans);
  assert.deepEqual(f.games, [{ pid: 222, running: true }]);
  f.adapter.dispose(); f.recorder.emit('game-launched', game(333)); assert.equal(f.games.length, 1);
});

test('disabled discovery does no scans, then enabling discovers an already running game', async t => {
  let enabled = false, scans = 0;
  const f = await fixture(t, { pollMs: 5, shouldDiscover: () => enabled, scanGames: async () => { scans++; return [222]; } });
  await delay(20); assert.equal(scans, 0); assert.deepEqual(f.games, []);
  enabled = true; await delay(20);
  assert.equal(scans, 1); assert.deepEqual(f.games, [{ pid: 222, running: true }]);
});

test('temporary discovery failure and ambiguity recover without marking the package unavailable', async t => {
  let scans = 0; const issues = [];
  const f = await fixture(t, { pollMs: 5, onDetectionIssue: code => issues.push(code), scanGames: async () => {
    scans++;
    if (scans === 1) throw recorderError('game-process-inspection-unavailable');
    if (scans === 2) return [222, 333];
    return [222];
  } });
  await delay(30);
  assert.equal(scans, 3); assert.deepEqual(f.unavailable, []);
  assert.deepEqual(issues, ['game-process-inspection-unavailable', 'game-process-ambiguous']);
  assert.deepEqual(f.games, [{ pid: 222, running: true }]);
});

test('a failed liveness check preserves the identified game until a confirmed exit', async t => {
  let alive = 'unknown'; const issues = [];
  const f = await fixture(t, { pollMs: 5, onDetectionIssue: code => issues.push(code), isAlive: () => {
    if (alive === 'unknown') throw recorderError('game-process-inspection-unavailable');
    return false;
  } });
  f.recorder.emit('game-launched', { id: 5426, type: 'Game', processInfo: { pid: 222, fullPath: 'C:\\Riot\\League of Legends.exe' } });
  await delay(20);
  assert.deepEqual(f.games, [{ pid: 222, running: true }]); assert.ok(issues.length > 0); assert.deepEqual(f.unavailable, []);
  alive = 'exited'; await delay(20);
  assert.deepEqual(f.games, [{ pid: 222, running: true }, { pid: 222, running: false }]);
});

test('an automatic completion releases an unresponsive start and consumes its late rejection', async t => {
  const f = await fixture(t); const native = deferred(), entered = deferred();
  let callback;
  f.recorder.startRecording = (_o, _s, stopped) => { callback = stopped; entered.resolve(); return native.promise; };
  const starting = f.start(); await entered.promise;
  callback({ hasError: false, filePath: path.join(f.root, 'capture.mp4'), duration: 5000, reason: 0 });
  const settings = await starting; assert.equal(settings.fps, 30); assert.equal(f.stopped.length, 1);
  native.reject(new Error('private late error')); await nextTurn();
  assert.deepEqual(f.unavailable, []); assert.equal(f.calls.stop, 0);
});

test('owned completion releases an unresponsive stop without losing the recording', async t => {
  const f = await fixture(t); const native = deferred(), entered = deferred();
  await f.start();
  f.recorder.stopRecording = () => { entered.resolve(); return native.promise; };
  const stopping = f.adapter.stop(); await entered.promise; f.finish(); await stopping;
  assert.equal(f.stopped.length, 1); assert.equal(f.stopped[0].hasError, false);
  native.reject(new Error('private late error')); await nextTurn(); assert.deepEqual(f.unavailable, []);
});

function box(type, payload = Buffer.alloc(0)) { const header = Buffer.alloc(8); header.writeUInt32BE(payload.length + 8); header.write(type, 4); return Buffer.concat([header, payload]); }
const decodedVideo = '[Parsed_showinfo_0 @ 000001] n: 0 pts: 0 pts_time:0 fmt:yuv420p';
const decodedAudio = '[Parsed_ashowinfo_0 @ 000002] n:0 pts:0 pts_time:0 fmt:fltp channels:2 rate:48000 nb_samples:1024';
test('validation checks owned path, container, codecs, dimensions and bounded decode samples using bundled tools', async t => {
  const runs = [];
  let missingStream;
  const f = await fixture(t, { runTool: async (tool, args, options) => {
    runs.push({ tool, args, options });
    if (!args.includes('-show_entries')) return { stdout: '', stderr: [missingStream === 'video' ? '' : decodedVideo,
      missingStream === 'audio' ? '' : decodedAudio].join('\n') };
    return { stdout: JSON.stringify({ format: { duration: '5', format_name: 'mov,mp4,m4a,3gp,3g2,mj2' }, streams: [
      { codec_type: 'video', codec_name: 'h264', width: 1280, height: 720, avg_frame_rate: '30/1' },
      { codec_type: 'audio', codec_name: 'aac', channels: 2, sample_rate: '48000' }] }) };
  } });
  await f.start();
  await writeFile(path.join(f.root, 'capture.mp4'), Buffer.concat([box('ftyp'), box('moov'), box('moof'), box('mdat', Buffer.alloc(32))]));
  f.finish();
  const result = await f.adapter.validate(f.stopped[0]);
  assert.equal(result.filePath, path.join(f.root, 'capture.mp4')); assert.equal(result.fileSize, 64); assert.equal(result.durationSeconds, 5);
  assert.equal(result.startedAt, '2023-11-14T22:13:20.000Z');
  assert.equal(result.startedAtVerified, true);
  assert.equal(runs.length, 4); assert.equal(runs[0].tool, f.recorder.ffprobePath);
  assert.ok(runs.slice(1).every(value => value.tool === f.recorder.ffmpegPath && value.args.includes('-xerror') && value.options.timeout === 10_000));
  delete f.stopped[0].startTimeEpoch;
  const unknownStart = await f.adapter.validate(f.stopped[0]);
  assert.equal(unknownStart.startedAtVerified, false); assert.equal(Object.hasOwn(unknownStart, 'startedAt'), false);
  for (const missing of ['video', 'audio']) {
    missingStream = missing;
    await assert.rejects(f.adapter.validate(f.stopped[0]), { code: 'recording-media-invalid' });
  }
  await assert.rejects(f.adapter.validate({ ...f.stopped[0] }), { code: 'recording-incomplete' });
  f.stopped[0].filePath = path.join(f.root, '..', 'capture.mp4');
  await assert.rejects(f.adapter.validate(f.stopped[0]), { code: 'recording-incomplete' });
});

test('validation never follows an output file link outside its owned directory', async t => {
  const f = await fixture(t); const outside = await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-outside-'));
  t.after(() => rm(outside, { recursive: true }));
  await f.start();
  await writeFile(path.join(outside, 'capture.mp4'), Buffer.alloc(32));
  // Directory junctions need no developer-mode symlink privilege on Windows.
  await symlink(outside, path.join(f.root, 'alias'), process.platform === 'win32' ? 'junction' : 'dir');
  f.finish({ filePath: path.join(f.root, 'alias', 'capture.mp4') });
  await assert.rejects(f.adapter.validate(f.stopped[0]), { code: 'recording-incomplete' });
});

test('registration and disposal are bounded and leave no live late events', async () => {
  const pending = deferred(); const recorder = Object.assign(new EventEmitter(), {
    registerGames: () => pending.promise, queryInformation() {}, createSettingsBuilder() {}, isActive() {}, startRecording() {}, stopRecording() {},
  });
  const games = [];
  const adapter = createOverwolfRecorder({ recorder, onGame: value => games.push(value), deadlines: { registration: 10 }, scanGames: async () => [] });
  await assert.rejects(adapter.initialize(), { code: 'recorder-registration-failed' }); adapter.dispose(); pending.resolve();
  recorder.emit('game-launched', { id: 5426, type: 'Game', processInfo: { pid: 222, fullPath: 'C:\\Riot\\League of Legends.exe' } });
  assert.deepEqual(games, []);
});
