import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { CaptureController, type Driver, type StopResult } from '../src/controller.ts';
import { prepareRecorder, recorderController, trackRecorderGame, isFragmentedMp4, validateMedia } from '../src/recorder.ts';

const nextTurn = () => new Promise<void>(resolve => setImmediate(resolve));
function deferred<T = void>() {
  let resolve!: (value: T | PromiseLike<T>) => void, reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const never = () => new Promise<void>(() => {});
const deadlines = { startMs: 25, stopMs: 25, completionMs: 25, validationMs: 25 };
const complete = { hasError: false, filePath: 'fixture.mp4', duration: 5000, splitCount: 0, reason: 0 };
const defaultDriver: Driver = { start: async () => {}, stop: async () => {}, validate: async () => true };

for (const interruption of ['stop', 'crash'] as const) {
  test(`same-turn start plus ${interruption} never reaches the provider`, async () => {
    let starts = 0, stops = 0;
    const c = new CaptureController({ ...defaultDriver, start: async () => { starts++; }, stop: async () => { stops++; } }, () => {}, deadlines);
    await Promise.all([c.start(), c[interruption]()]);
    assert.equal(starts, 0); assert.equal(stops, 0); assert.equal(c.state, 'failed'); assert.equal(c.mayBeActive, false);
    assert.equal(c.lastFailure, interruption === 'stop' ? 'start-cancelled' : 'provider-crash');
  });
}

test('hung start is bounded; a late start requires another stop and cannot resurrect the manifest', async () => {
  const start = deferred(); let stops = 0;
  const c = new CaptureController({ ...defaultDriver, start: () => start.promise, stop: async () => { stops++; } }, () => {}, deadlines);
  await c.start();
  assert.equal(c.state, 'partial'); assert.equal(c.lastFailure, 'start-timeout');
  assert.equal(c.mayBeActive, true); assert.equal(stops, 1);
  start.resolve(); await nextTurn(); await c.drained();
  assert.equal(stops, 2); assert.equal(c.mayBeActive, false); assert.equal(c.state, 'partial');
});
test('stop can interrupt a start that ignores cancellation', async () => {
  const c = new CaptureController({ ...defaultDriver, start: never }, () => {}, { ...deadlines, startMs: 1000 });
  const starting = c.start(); await nextTurn(); await c.stop(); await starting;
  assert.equal(c.lastFailure, 'start-cancelled'); assert.equal(c.state, 'partial'); assert.equal(c.mayBeActive, true);
});
test('crash interrupts pending startup and late provider rejection is consumed', async () => {
  const start = deferred();
  const c = new CaptureController({ ...defaultDriver, start: () => start.promise }, () => {}, { ...deadlines, startMs: 1000 });
  const starting = c.start(); await nextTurn(); await c.crash(); await starting;
  assert.equal(c.lastFailure, 'provider-crash'); assert.equal(c.state, 'partial');
  start.reject(new Error('private-provider-diagnostic')); await nextTurn(); await c.drained();
  assert.equal(c.lastFailure, 'provider-crash'); assert.equal(c.state, 'partial'); assert.equal(c.mayBeActive, false);
});
test('unresponsive stop is partial and retains unresolved activity', async () => {
  const c = new CaptureController({ ...defaultDriver, stop: never }, () => {}, deadlines);
  await c.start(); await c.stop();
  assert.equal(c.state, 'partial'); assert.equal(c.lastFailure, 'stop-timeout'); assert.equal(c.mayBeActive, true);
  await c.onStopped(complete);
  assert.equal(c.state, 'partial'); assert.equal(c.mayBeActive, false);
});
test('stop without completion metadata gets a bounded partial outcome', async () => {
  const c = new CaptureController(defaultDriver, () => {}, deadlines);
  await c.start(); await c.stop(); assert.equal(c.state, 'stopping');
  await c.drained(); assert.equal(c.state, 'partial'); assert.equal(c.lastFailure, 'completion-timeout'); assert.equal(c.mayBeActive, false);
});
test('completion callback can release hung stop and finalizes once', async () => {
  let validations = 0;
  const c = new CaptureController({ ...defaultDriver, stop: never, validate: async () => { validations++; return true; } }, () => {}, { ...deadlines, stopMs: 1000 });
  await c.start(); const stopping = c.stop(); await nextTurn();
  await c.onStopped(complete); await stopping; await c.onStopped(complete);
  assert.equal(c.state, 'finalized'); assert.equal(c.mayBeActive, false); assert.equal(validations, 1);
});
test('completion before pending start resolves prevents a false recording transition', async () => {
  const start = deferred(); const states: string[] = [];
  const c = new CaptureController({ ...defaultDriver, start: () => start.promise }, s => states.push(s), deadlines);
  const starting = c.start(); await nextTurn(); await c.onStopped(complete); await starting;
  start.resolve(); await nextTurn(); await c.drained();
  assert.deepEqual(states, ['starting', 'finalizing', 'finalized']);
});
test('validation timeout is partial and aborts validation subprocess work', async () => {
  let signal: AbortSignal | undefined;
  const c = new CaptureController({ ...defaultDriver, validate: async (_r, s) => { signal = s; return new Promise(() => {}); } }, () => {}, deadlines);
  await c.start(); await c.onStopped(complete);
  assert.equal(c.lastFailure, 'validation-timeout'); assert.equal(signal?.aborted, true); assert.equal(c.state, 'partial');
});
test('provider crash during validation cannot later become finalized', async () => {
  const validating = deferred<boolean>();
  const c = new CaptureController({ ...defaultDriver, validate: () => validating.promise }, () => {}, deadlines);
  await c.start(); const stopped = c.onStopped(complete); await nextTurn(); await c.crash(); await stopped;
  validating.resolve(true); await nextTurn(); await c.drained();
  assert.equal(c.state, 'partial'); assert.equal(c.lastFailure, 'provider-crash');
});
test('same-turn completion and crash performs no post-crash media work', async () => {
  let validations = 0;
  const c = new CaptureController({ ...defaultDriver, validate: async () => { validations++; return true; } }, () => {}, deadlines);
  await c.start(); await Promise.all([c.onStopped(complete), c.crash()]);
  assert.equal(c.state, 'partial'); assert.equal(validations, 0);
});
test('intent persistence failure prevents all provider work without recursive write failure', async () => {
  let starts = 0, stops = 0, writes = 0;
  const c = new CaptureController({ ...defaultDriver, start: async () => { starts++; }, stop: async () => { stops++; } },
    () => { writes++; throw new Error('private-path'); }, deadlines);
  await c.start(); await c.stop();
  assert.equal(starts, 0); assert.equal(stops, 0); assert.equal(writes, 2);
  assert.equal(c.state, 'failed'); assert.equal(c.lastFailure, 'persistence-failed'); assert.equal(c.persistenceHealthy, false); assert.equal(c.mayBeActive, false);
});
test('persistence failure after start stops owned capture and retains partial state', async () => {
  let stops = 0;
  const c = new CaptureController({ ...defaultDriver, stop: async () => { stops++; } },
    s => { if (s !== 'starting') throw new Error('private-path'); }, deadlines);
  await c.start(); await c.onStopped(complete);
  assert.equal(c.state, 'partial'); assert.equal(stops, 1); assert.equal(c.persistenceHealthy, false); assert.equal(c.mayBeActive, false);
});
for (const reason of [1, 2, -1000, NaN]) {
  test(`non-success recorder reason ${reason} cannot imply a complete recording`, async () => {
    let validations = 0;
    const c = new CaptureController({ ...defaultDriver, validate: async () => { validations++; return true; } }, () => {}, deadlines);
    await c.start(); await c.onStopped({ ...complete, reason });
    assert.equal(c.state, 'partial'); assert.equal(validations, 0);
  });
}
test('malformed optional stop timestamp never reaches persistence or media logging', async () => {
  let validations = 0;
  const c = new CaptureController({ ...defaultDriver, validate: async () => { validations++; return true; } }, () => {}, deadlines);
  await c.start(); await c.onStopped({ ...complete, startTimeEpoch: 'private-payload' } as unknown as StopResult);
  assert.equal(c.state, 'partial'); assert.equal(validations, 0);
});

const config = { sourceWidth: 1280, sourceHeight: 720, preset: '720p30' as const, allowSoftwareEncoder: false };
const journal = { write: (_row: Record<string, unknown>) => {} };
function adapterFixture() {
  const builder = {
    videoSettings: {} as Record<string, unknown>, videoEncoderSettings: { type: 'obs_nvenc_h264_tex' }, audioEncoder: { type: 'ffmpeg_aac', codec: 'aac' },
    audioSettings: { inputs: [] as unknown[], outputs: [] as unknown[], applications: [] as Record<string, unknown>[] }, sources: [] as Record<string, any>[],
    addGameSource(properties: unknown) { this.sources.push({ type: 'Game', properties }); return this; },
    addApplicationAudioCapture({ processName }: { processName: string }, settings: unknown) { this.audioSettings.applications.push({ name: processName, type: 'output', ...(settings as object) }); return this; },
    build() { return this; }
  };
  let starts = 0, stops = 0, options: unknown;
  const recorder = {
    isActive: async () => false,
    queryInformation: async () => ({ video: { encoders: [{ type: 'obs_nvenc_h264_tex', codec: 'h264' }] }, audio: { encoders: [{ type: 'ffmpeg_aac', codec: 'aac' }] } }),
    createSettingsBuilder: async () => builder,
    startRecording: async (o: unknown) => { starts++; options = o; }, stopRecording: async () => { stops++; }
  };
  return { recorder, builder, counts: () => ({ starts, stops, options }) };
}
test('adapter never stops a recorder/replay that was already active', async () => {
  const f = adapterFixture(); f.recorder.isActive = async () => true;
  const c = recorderController(f.recorder as never, 222, config, 'fixture-root', journal as never, () => {}, deadlines);
  await c.start(); assert.equal(c.state, 'failed'); assert.equal(f.counts().stops, 0); assert.equal(f.counts().starts, 0);
});
test('setup cancelled while query is pending cannot later start capture', async () => {
  const f = adapterFixture(); const query = deferred<Awaited<ReturnType<typeof f.recorder.queryInformation>>>();
  const info = await f.recorder.queryInformation(); f.recorder.queryInformation = () => query.promise;
  const c = recorderController(f.recorder as never, 222, config, 'fixture-root', journal as never, () => {}, deadlines);
  const starting = c.start(); await nextTurn(); await c.stop(); await starting;
  query.resolve(info); await nextTurn(); await c.drained();
  assert.equal(f.counts().starts, 0); assert.equal(f.counts().stops, 0); assert.equal(c.mayBeActive, false); assert.equal(c.state, 'partial');
});
test('adapter issues explicit fragmented MP4 and game-exit shutdown options', async () => {
  const f = adapterFixture();
  const c = recorderController(f.recorder as never, 222, config, 'fixture-root', journal as never, () => {}, deadlines);
  await c.start();
  assert.deepEqual(f.counts().options, { filePath: path.join('fixture-root', 'capture'), fileFormat: 'fragmented_mp4', autoShutdownOnGameExit: true });
  await c.crash();
});
test('stop retains final counters once and measures explicit stop callback separately', async () => {
  const f = adapterFixture(); const rows: Record<string, unknown>[] = [];
  let listener: (result: unknown) => void = () => {};
  f.recorder.startRecording = (async (_o: unknown, _s: unknown, cb: (result: unknown) => void) => { listener = cb; }) as never;
  f.recorder.stopRecording = async () => {
    const result = { hasError: true, duration: 5000, stats: { outputSkippedFrames: 3, outputTotalFrames: 150 } };
    listener(result); listener(result);
  };
  const c = recorderController(f.recorder as never, 222, config, 'fixture-root', { write: r => rows.push(r) } as never, () => {}, deadlines);
  await c.start(); await c.stop(); await c.drained();
  const final = rows.filter(r => r.kind === 'recorder-stats' && r.phase === 'stop');
  assert.equal(final.length, 1); assert.equal(final[0].outputTotal, 150); assert.equal(final[0].outputSkipped, 3);
  assert.equal(final[0].renderTotal, null);
  const stop = rows.find(r => r.kind === 'recorder-stop');
  assert.ok(typeof stop?.requestToCallbackMs === 'number' && stop.requestToCallbackMs >= 0);
  assert.equal(c.state, 'partial'); assert.equal(c.mayBeActive, false);
});
for (const [name, mutate] of Object.entries({
  microphone: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioSettings.inputs.push({ name: 'private-device' }); },
  defaultOutput: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioSettings.outputs.push({ name: 'private-device' }); },
  wrongAudioEncoder: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioEncoder.codec = 'opus'; },
  wrongPid: (b: ReturnType<typeof adapterFixture>['builder']) => { b.sources[0].properties.gameProcess = 333; },
  overlays: (b: ReturnType<typeof adapterFixture>['builder']) => { b.sources[0].properties.captureOverlays = true; },
  silentAudio: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioSettings.applications[0].volume = 0; },
  wrongApplication: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioSettings.applications[0].name = 'Discord.exe'; },
  audioFilters: (b: ReturnType<typeof adapterFixture>['builder']) => { b.audioSettings.applications[0].filters = [{ type: 'noise_suppress_filter_v2' }]; },
  wrongDimensions: (b: ReturnType<typeof adapterFixture>['builder']) => { b.videoSettings.outputWidth = 1920; },
  unlimitedCapture: (b: ReturnType<typeof adapterFixture>['builder']) => { b.sources[0].properties.limitFramerate = false; },
  memoryCapture: (b: ReturnType<typeof adapterFixture>['builder']) => { b.sources[0].properties.sliCompatibility = true; },
  costlyEncoderDefault: (b: ReturnType<typeof adapterFixture>['builder']) => { Object.assign(b.videoEncoderSettings, { lookahead: true }); },
  mismatchedAudioBuffering: (b: ReturnType<typeof adapterFixture>['builder']) => { Object.assign(b.audioSettings, { lowLatencyAudioBuffering: true }); }
})) {
  test(`built capture graph rejects ${name}`, async () => {
    const f = adapterFixture(); f.builder.build = () => { mutate(f.builder); return f.builder; };
    await assert.rejects(prepareRecorder(f.recorder as never, 222, config, journal as never));
    assert.equal(f.counts().starts, 0);
  });
}
test('software encoder requires explicit opt-in and codec metadata must agree', async () => {
  const f = adapterFixture();
  f.recorder.queryInformation = async () => ({ video: { encoders: [{ type: 'obs_x264', codec: 'h264' }] }, audio: { encoders: [{ type: 'ffmpeg_aac', codec: 'aac' }] } });
  f.builder.videoEncoderSettings.type = 'obs_x264';
  await assert.rejects(prepareRecorder(f.recorder as never, 222, config, journal as never));
  await prepareRecorder(f.recorder as never, 222, { ...config, allowSoftwareEncoder: true }, journal as never);
});

test('a builder that selects another encoder is rejected before policy is applied', async () => {
  const f = adapterFixture(); f.builder.videoEncoderSettings.type = 'obs_x264';
  await assert.rejects(prepareRecorder(f.recorder as never, 222, config, journal as never), /encoder-selection-mismatch/);
});

const game = (pid: number) => ({ id: 5426, type: 'Game', processInfo: { pid, fullPath: 'C:\\Riot\\Game\\League of Legends.exe' } });
function trackerFixture() {
  const recorder = new EventEmitter() as EventEmitter & { registerGames: () => Promise<void> };
  recorder.registerGames = async () => {};
  return recorder;
}

test('known gameplay liveness avoids repeated PowerShell discovery and exit resumes it', async () => {
  const recorder = trackerFixture(); let scans = 0, alive = true;
  const seen: (number | undefined)[] = [];
  const tracking = await trackRecorderGame(recorder as never, p => seen.push(p), journal as never,
    { scan: async () => { scans++; return scans === 1 ? [222] : []; }, isAlive: () => alive, intervalMs: 10 });
  try {
    await new Promise(resolve => setTimeout(resolve, 45)); assert.equal(scans, 1); assert.deepEqual(seen, [222]);
    alive = false; await new Promise(resolve => setTimeout(resolve, 30)); assert.ok(scans >= 2); assert.deepEqual(seen, [222, undefined]);
  } finally { tracking.dispose(); }
});

test('recorder totals survive journaling and missing statistics stay unknown', async () => {
  const recorder = trackerFixture(); const rows: Record<string, unknown>[] = [];
  const tracking = await trackRecorderGame(recorder as never, () => {}, { write: r => rows.push(r) } as never, { scan: async () => [] });
  try {
    recorder.emit('stats', { cpuUsage: 1, memoryUsage: 50, activeFps: 30, outputSkippedFrames: 2, renderSkippedFrames: 1,
      outputTotalFrames: 1000, renderTotalFrames: 1001, averageFrameRenderTime: 0.5 });
    const row = rows.find(r => r.kind === 'recorder-stats');
    assert.equal(row?.outputTotal, 1000); assert.equal(row?.renderTotal, 1001); assert.equal(row?.renderTimeMs, 0.5);
    assert.equal(row?.availableDiskMB, null);
  } finally { tracking.dispose(); }
});
test('late attach finds one gameplay PID, ignores old exit, and disposes events/scan', async () => {
  const recorder = trackerFixture(); const seen: (number | undefined)[] = []; const rows: Record<string, unknown>[] = [];
  const tracking = await trackRecorderGame(recorder as never, p => seen.push(p), { write: r => rows.push(r) } as never, { scan: async () => [222] });
  await nextTurn(); recorder.emit('game-launched', game(333)); recorder.emit('game-exit', game(222));
  assert.deepEqual(seen, [222, 333]);
  tracking.dispose(); recorder.emit('game-launched', game(444)); recorder.emit('stats', { cpuUsage: 1 });
  assert.deepEqual(seen, [222, 333, undefined]); assert.equal(JSON.stringify(rows).includes('Riot'), false);
});
test('stale OS scan cannot overwrite newer recorder launch', async () => {
  const recorder = trackerFixture(); const seen: (number | undefined)[] = []; const scan = deferred<number[]>();
  const tracking = await trackRecorderGame(recorder as never, p => seen.push(p), journal as never, { scan: () => scan.promise });
  recorder.emit('game-launched', game(333)); scan.resolve([222]); await nextTurn();
  assert.deepEqual(seen, [333]); tracking.dispose();
});
test('disposed tracking ignores a scan completing after shutdown', async () => {
  const recorder = trackerFixture(); const seen: (number | undefined)[] = []; const scan = deferred<number[]>(); let signal: AbortSignal | undefined;
  const tracking = await trackRecorderGame(recorder as never, p => seen.push(p), journal as never, { scan: s => { signal = s; return scan.promise; } });
  tracking.dispose(); scan.resolve([222]); await nextTurn();
  assert.equal(signal?.aborted, true); assert.deepEqual(seen, []);
});
test('ambiguous process scan and malformed recorder packets cannot authorize capture', async () => {
  const recorder = trackerFixture(); const seen: (number | undefined)[] = [];
  const tracking = await trackRecorderGame(recorder as never, p => seen.push(p), journal as never, { scan: async () => [222, 333] });
  await nextTurn(); recorder.emit('game-launched', null); recorder.emit('game-launched', { ...game(222), processInfo: { pid: 222 } });
  assert.deepEqual(seen, []); tracking.dispose();
});
test('pending registration is bounded and its late events remain inert', async () => {
  const recorder = trackerFixture(); const seen: (number | undefined)[] = [];
  recorder.registerGames = never;
  await assert.rejects(trackRecorderGame(recorder as never, p => seen.push(p), journal as never, { registrationMs: 25, scan: async () => [] }), /game-registration-timeout/);
  recorder.emit('game-launched', game(222)); assert.deepEqual(seen, []);
});
test('lifetime abort cancels pending registration before a handle is available', async () => {
  const recorder = trackerFixture(); const abort = new AbortController(); let registrations = 0;
  recorder.registerGames = () => { registrations++; return never(); };
  const pending = trackRecorderGame(recorder as never, () => {}, journal as never, { signal: abort.signal, registrationMs: 1000 });
  abort.abort(); await assert.rejects(pending, /tracking-cancelled/); assert.equal(registrations, 0);
});

function box(type: string, payload = Buffer.alloc(0)) { const h = Buffer.alloc(8); h.writeUInt32BE(payload.length + 8); h.write(type, 4); return Buffer.concat([h, payload]); }
test('fragment validation rejects flat/truncated containers with bounded header reads', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-headers-'));
  try {
    const file = path.join(root, 'capture.mp4');
    await writeFile(file, Buffer.concat([box('ftyp'), box('moov'), box('moof'), box('mdat', Buffer.alloc(32))]));
    assert.equal(await isFragmentedMp4(file), true);
    await writeFile(file, Buffer.concat([box('ftyp'), box('moov'), box('mdat', Buffer.alloc(32))]));
    assert.equal(await isFragmentedMp4(file), false);
    await writeFile(file, Buffer.concat([box('ftyp'), box('moov'), box('moof'), Buffer.from([0, 0, 0, 50, 109, 100, 97, 116])]));
    assert.equal(await isFragmentedMp4(file), false);
    await writeFile(file, Buffer.concat([box('ftyp'), box('moov'), box('moof')]));
    assert.equal(await validateMedia({} as never, root, { ...complete, filePath: path.join(root, 'unrelated.mp4') }, journal as never), false);
    assert.equal(await validateMedia({} as never, root, { ...complete, filePath: path.join(root, '..', 'capture.mp4') }, journal as never), false);
  } finally { await rm(root, { recursive: true }); }
});

test('synthetic H264/AAC fMP4 validates end-to-end using explicitly supplied local ffmpeg tools',
  { skip: !process.env.REVU_FIXTURE_FFMPEG || !process.env.REVU_FIXTURE_FFPROBE }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revu-recorder-synthetic-'));
  const ffmpegPath = process.env.REVU_FIXTURE_FFMPEG!, ffprobePath = process.env.REVU_FIXTURE_FFPROBE!;
  try {
    const filePath = path.join(root, 'capture.mp4');
    await promisify(execFile)(ffmpegPath, ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30',
      '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000', '-t', '3', '-c:v', 'libx264', '-preset', 'ultrafast',
      '-g', '60', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-ac', '2', '-movflags', 'frag_keyframe+empty_moov', filePath],
      { timeout: 20_000, maxBuffer: 32_768, windowsHide: true });
    const recorder = { ffmpegPath, ffprobePath } as never;
    const result = { ...complete, filePath, duration: 3000 };
    assert.equal(await validateMedia(recorder, root, result, journal as never, undefined, config), true);
    assert.equal(await validateMedia(recorder, root, { ...result, duration: 30_000 }, journal as never, undefined, config), false);
    assert.equal(await validateMedia(recorder, root, result, journal as never, undefined, { ...config, preset: '1080p60' }), false);
  } finally { await rm(root, { recursive: true }); }
});
