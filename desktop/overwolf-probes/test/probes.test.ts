import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { CaptureController, type StopResult } from '../src/controller.ts';
import { dimensions, FEATURES, isLeague, ObservationFilter, MAX_JOURNAL_BYTES } from '../src/policy.ts';
import { Journal } from '../src/journal.ts';
import { prepareRecorder } from '../src/recorder.ts';

const packet = (feature: string, key: string, value: unknown) => ({ gameId: 5426, feature, key, value });
test('chat, names, unsupported keys and queues never enter observations', () => {
  const filter = new ObservationFilter();
  assert.equal(FEATURES.includes('chat' as never), false);
  assert.equal(filter.accept(packet('damage', 'physical_damage_taken', 4), 0), undefined);
  filter.accept(packet('matchState', 'queueId', 420), 1);
  for (const p of [packet('chat', 'chat', 'private content'), packet('matchState', 'matchId', '12345'),
    packet('damage', 'physical_damage_taken', { damage: 10, chat: 'private content' }), packet('damage', 'physical_damage_taken', 'private content')]) {
    assert.equal(filter.accept(p, 2), undefined);
  }
  assert.equal(filter.accept(packet('damage', 'physical_damage_taken', '10'), 3)?.value, 10);
  filter.accept(packet('matchState', 'queueId', 2300), 4);
  assert.equal(filter.accept(packet('damage', 'physical_damage_taken', 10), 5), undefined);
});
test('clock and queue reset do not manufacture continuity', () => {
  const filter = new ObservationFilter();
  filter.accept(packet('matchState', 'queueId', 420), 0);
  assert.equal(filter.accept(packet('counters', 'match_clock', 30), 1)?.gameClock, 30);
  filter.reset();
  assert.equal(filter.clock, null);
  assert.equal(filter.accept(packet('damage', 'physical_damage_taken', 10), 2), undefined);
});
test('documented ability activation and lowercase announcer keys normalize without retaining arbitrary fields', () => {
  const f = new ObservationFilter(); f.accept(packet('matchState', 'queueId', 420), 0);
  assert.equal(f.accept(packet('abilities', 'usedAbility', { type: '4', chat: 'private content' }), 1)?.value, 4);
  assert.equal(f.accept(packet('abilities', 'ability', 4), 2), undefined);
  assert.equal(f.accept(packet('announcer', 'victory', null), 3)?.value, true);
  assert.equal(f.accept(packet('matchState', 'matchEnd', null), 4), undefined);
});
test('capture scope rejects launcher, wrong executable and missing PID', () => {
  const good = { id: 5426, type: 'Game', processInfo: { pid: 22, fullPath: 'C:\\Riot\\Game\\League of Legends.exe' } };
  assert.equal(isLeague(good), true);
  assert.equal(isLeague({ ...good, type: 'Launcher' }), false);
  assert.equal(isLeague({ ...good, processInfo: { pid: 22, fullPath: 'LeagueClient.exe' } }), false);
  assert.equal(isLeague({ ...good, processInfo: undefined }), false);
});
test('capture dimensions retain aspect ratio and never upscale', () => {
  assert.deepEqual(dimensions(1024, 768, '1080p60'), { baseWidth: 1024, baseHeight: 768, outputWidth: 1024, outputHeight: 768, fps: 60 });
  assert.equal(dimensions(3840, 2160, '720p30').outputWidth, 1280);
  assert.throws(() => dimensions(0, 1080, '720p30'));
});
function fixture({ validate = true, failStart = false, failStop = false } = {}) {
  let starts = 0, stops = 0, validations = 0;
  let callback: (r: StopResult) => void = () => {};
  const states: string[] = [];
  const controller = new CaptureController({
    start: async cb => { starts++; callback = cb; if (failStart) throw new Error('fixture-start'); },
    stop: async () => { stops++; if (failStop) throw new Error('fixture-stop'); },
    validate: async () => { validations++; return validate; }
  }, state => states.push(state));
  return { controller, states, complete: (r: StopResult) => callback(r), counts: () => ({ starts, stops, validations }) };
}
const success = { hasError: false, filePath: 'synthetic.mp4', duration: 5000, splitCount: 0 };
test('concurrent duplicate start/stop/callbacks finalize exactly once', async () => {
  const f = fixture();
  await Promise.all([f.controller.start(), f.controller.start()]);
  await Promise.all([f.controller.stop(), f.controller.stop()]);
  assert.equal(f.controller.state, 'stopping');
  f.complete(success); f.complete(success); await f.controller.drained();
  assert.equal(f.controller.state, 'finalized');
  assert.deepEqual(f.counts(), { starts: 1, stops: 1, validations: 1 });
  assert.deepEqual(f.states, ['starting', 'recording', 'stopping', 'finalizing', 'finalized']);
});
test('start intent is persisted before driver capture', async () => {
  const states: string[] = [];
  const c = new CaptureController({ start: async () => assert.deepEqual(states, ['starting']), stop: async () => {}, validate: async () => true }, s => states.push(s));
  await c.start(); assert.equal(c.state, 'recording');
});
test('stop promise alone does not announce finalized media', async () => {
  const f = fixture(); await f.controller.start(); await f.controller.stop(); assert.equal(f.controller.state, 'stopping');
});
for (const [name, result] of Object.entries({ error: { ...success, hasError: true }, contradictoryError: { ...success, error: 'synthetic failure' }, missingFile: { ...success, filePath: undefined },
  missingDuration: { ...success, duration: undefined }, invalidDuration: { ...success, duration: NaN }, reconnectSplit: { ...success, splitCount: 1 } })) {
  test(`incomplete stop ${name} remains partial`, async () => {
    const f = fixture(); await f.controller.start(); f.complete(result); await f.controller.drained();
    assert.equal(f.controller.state, 'partial'); assert.equal(f.counts().validations, 0);
  });
}
test('media validation failure leaves original media partial', async () => {
  const f = fixture({ validate: false }); await f.controller.start(); f.complete(success); await f.controller.drained(); assert.equal(f.controller.state, 'partial');
});
test('recorder crash and late callbacks cannot resurrect finalized state', async () => {
  const f = fixture(); await f.controller.start(); await f.controller.crash(); f.complete(success); await f.controller.drained(); assert.equal(f.controller.state, 'partial');
});
test('start and stop failures are recorded without leaking exception text', async () => {
  const a = fixture({ failStart: true }); await a.controller.start(); assert.equal(a.controller.state, 'failed');
  const b = fixture({ failStop: true }); await b.controller.start(); await b.controller.stop(); assert.equal(b.controller.state, 'partial');
});
test('journal bounds prevent unlimited logs', () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'revu-probe-test-'));
  try {
    const journal = new Journal(root);
    for (let i = 0; i < 25_000; i++) journal.write({ kind: 'synthetic-observation', value: 42 });
    assert.equal(journal.rows, 20_000); assert.equal(journal.dropped, 5000); assert.ok(journal.bytes <= MAX_JOURNAL_BYTES);
    assert.ok(readFileSync(journal.file).length <= MAX_JOURNAL_BYTES);
  } finally { rmSync(root, { recursive: true }); }
});
test('actual recorder adapter requests only game video/application audio and explicit H264/AAC', async () => {
  const calls: unknown[] = [];
  const builder = { videoSettings: {}, videoEncoderSettings: { type: 'obs_nvenc_h264_tex' },
    audioEncoder: { type: 'ffmpeg_aac', codec: 'aac' },
    audioSettings: { inputs: [], outputs: [], applications: [{ name: 'League of Legends.exe', type: 'output', volume: 1 }] },
    sources: [{ type: 'Game', properties: { gameProcess: 222, captureCursor: true, captureOverlays: false, limitFramerate: true, sliCompatibility: false } }],
    addGameSource: (v: unknown) => { calls.push(v); }, addApplicationAudioCapture: (v: unknown) => { calls.push(v); },
    build() { return this; } };
  const recorder = { queryInformation: async () => ({ video: { encoders: [{ type: 'obs_nvenc_h264_tex', codec: 'h264' }] }, audio: { encoders: [{ type: 'ffmpeg_aac', codec: 'aac' }] } }),
    createSettingsBuilder: async (v: unknown) => { calls.push(v); return builder; } };
  await prepareRecorder(recorder as never, 222, { sourceWidth: 1280, sourceHeight: 720, preset: '720p30', allowSoftwareEncoder: false }, { write: () => {} } as never);
  assert.deepEqual(calls, [{ videoEncoder: 'obs_nvenc_h264_tex', audioEncoder: 'ffmpeg_aac', includeDefaultAudioSources: false, separateAudioTracks: false },
    { gameProcess: 222, captureCursor: true, captureOverlays: false, limitFramerate: true, sliCompatibility: false }, { processName: 'League of Legends.exe' }]);
});
