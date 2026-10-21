import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import { createLibobsRecorder, rankedRecordingContext, recordingTestContext, retryableProbeFailure } from '../libobs-recorder.mjs';

const context = { queueId: 420, phase: 'InProgress' };
const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
function childProcess() {
  const child = new EventEmitter();
  Object.assign(child, { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), killed: false });
  child.send = value => child.stdout.write(`${JSON.stringify(value)}\n`);
  child.close = (code = 0) => { child.stdin.destroy(); child.stdout.end(); child.stderr.end(); child.emit('close', code); };
  child.kill = () => { child.killed = true; child.close(1); };
  return child;
}
async function fixture(t, options = {}) {
  const { capabilities = { type: 'capabilities', protocol: 1, available: true,
    encoders: ['obs_nvenc_h264_tex'], selectedEncoder: 'obs_nvenc_h264_tex' }, ...driverOptions } = options;
  const temporary = path.resolve('../artifacts/libobs-tests');
  await mkdir(temporary, { recursive: true });
  const directory = await mkdtemp(path.join(temporary, 'revu-libobs-'));
  const calls = [], games = [], failures = [], stopped = [];
  let child;
  const driver = createLibobsRecorder({ helperPath: path.join(directory, 'revu-recorder.exe'),
    ffmpegPath: path.join(directory, 'ffmpeg.exe'), ffprobePath: path.join(directory, 'ffprobe.exe'),
    getContext: async () => context, onGame: value => games.push(value), onUnavailable: value => failures.push(value),
    queryHelper: async (_file, args) => { calls.push(args); return { stdout: JSON.stringify(args[0] === '--probe'
      ? capabilities
      : { type: 'games', games: [{ pid: 123, width: 1920, height: 1080 }] }) }; },
    spawnHelper: (_file, args, settings) => { calls.push({ args, settings }); child = childProcess(); return child; },
    pollMs: 10, deadlines: { start: 200, stop: 100, heartbeat: 100 }, ...driverOptions });
  await driver.initialize();
  t.after(async () => { await driver.dispose().catch(() => {}); await rm(directory, { recursive: true, force: true }); });
  const start = async (encoder = 'obs_nvenc_h264_tex', reported = {}) => {
    const previous = child;
    const pending = driver.start({ pid: 123, preset: '720p30', outputDirectory: directory,
      onStopped: result => stopped.push(result) });
    while (!child || child === previous) await wait(1);
    child.send({ type: 'started', encoder, width: 1280, height: 720, fps: 30, bitrate: 5000, sourceWidth: 1920, sourceHeight: 1080,
      priority: 'below-normal', gpuPriority: 'below-normal', startedAtVerified: true, startedAt: new Date().toISOString(), ...reported });
    return pending;
  };
  return { driver, directory, calls, games, failures, stopped, start, child: () => child };
}

test('automatic libobs discovery requires an observed ranked game phase', async t => {
  for (const value of [null, { queueId: 450, phase: 'InProgress' }, { gameId: 9876, queueId: 0, phase: 'InProgress' },
    { queueId: 420, phase: 'Lobby' }]) {
    const f = await fixture(t, { shouldDiscover: () => true, getContext: async () => value });
    await wait(25);
    assert.deepEqual(f.calls, [['--probe']]);
    await assert.rejects(f.driver.start({ pid: 123, outputDirectory: f.directory }), {
      code: value === null ? 'recording-context-unavailable' : 'recording-queue-unsupported' });
  }
  for (const phase of ['GameStart', 'InProgress', 'Reconnect']) assert.equal(rankedRecordingContext({ queueId: 420, phase }), true);
});

test('explicit test scope accepts a current League game without assuming a practice queue ID', async t => {
  for (const phase of ['GameStart', 'InProgress', 'Reconnect'])
    assert.equal(recordingTestContext({ gameId: 9876, queueId: null, phase }), true);
  const f = await fixture(t, { recordingTest: true, shouldDiscover: () => true,
    getContext: async () => ({ gameId: 9876, queueId: null, phase: 'InProgress' }) });
  await wait(25);
  assert.deepEqual(f.games, [{ pid: 123, width: 1920, height: 1080, running: true }]);
  await f.driver.prepareStart({ pid: 123 });
  assert.equal((await f.start()).encoder, 'obs_nvenc_h264_tex');
  f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
  f.child().close();
});

test('test scope requires a positive current game identity and an active phase', async t => {
  for (const gameId of [undefined, null, 0, -1, '9876', Infinity]) {
    const f = await fixture(t, { recordingTest: true, shouldDiscover: () => true,
      getContext: async () => ({ gameId, queueId: 0, phase: 'InProgress' }) });
    await wait(25);
    assert.deepEqual(f.calls, [['--probe']]);
    await assert.rejects(f.driver.prepareStart({ pid: 123 }), { code: 'recording-context-unavailable' });
    await assert.rejects(f.driver.start({ pid: 123, outputDirectory: f.directory }), { code: 'recording-context-unavailable' });
  }
  const inactive = await fixture(t, { recordingTest: true, shouldDiscover: () => true,
    getContext: async () => ({ gameId: 9876, queueId: 0, phase: 'Lobby' }) });
  await wait(25);
  assert.deepEqual(inactive.calls, [['--probe']]);
  await assert.rejects(inactive.driver.prepareStart({ pid: 123 }), { code: 'recording-queue-unsupported' });
  await assert.rejects(inactive.driver.start({ pid: 123, outputDirectory: inactive.directory }), { code: 'recording-queue-unsupported' });
});

test('test scope still requires the exact League PID and an explicit boolean authorization', async t => {
  const practice = { gameId: 9876, queueId: 0, phase: 'InProgress' };
  const f = await fixture(t, { recordingTest: true, getContext: async () => practice });
  await assert.rejects(f.driver.prepareStart({ pid: 124 }), { code: 'source-window-not-ready' });
  await assert.rejects(f.driver.start({ pid: 0, outputDirectory: f.directory }), { code: 'game-process-invalid' });
  assert.equal(f.child(), undefined);
  const unauthorized = await fixture(t, { recordingTest: 'true', shouldDiscover: () => true, getContext: async () => practice });
  await wait(25);
  assert.deepEqual(unauthorized.calls, [['--probe']]);
  await assert.rejects(unauthorized.driver.prepareStart({ pid: 123 }), { code: 'recording-queue-unsupported' });
});

test('preflight rejects an unready window before spawning capture', async t => {
  const f = await fixture(t, { queryHelper: async (_file, args) => ({ stdout: JSON.stringify(args[0] === '--probe'
    ? { type: 'capabilities', protocol: 1, available: true, encoders: ['obs_nvenc_h264_tex'], selectedEncoder: 'obs_nvenc_h264_tex' }
    : { type: 'games', games: [{ pid: 123, width: 0, height: 0 }] }) }) });
  await assert.rejects(f.driver.prepareStart({ pid: 123 }), { code: 'source-window-not-ready' });
  assert.equal(f.child(), undefined);
});

test('discovery uses the bounded native helper only when recording is opted in', async t => {
  const disabled = await fixture(t); await wait(25);
  assert.deepEqual(disabled.calls, [['--probe']]);
  const enabled = await fixture(t, { shouldDiscover: () => true }); await wait(25);
  assert.ok(enabled.calls.some(args => args[0] === '--discover'));
  assert.deepEqual(enabled.games, [{ pid: 123, width: 1920, height: 1080, running: true }]);
});

test('no discovery helper is launched while a recording is running', async t => {
  const f = await fixture(t, { shouldDiscover: () => true });
  const discoveries = () => f.calls.filter(value => Array.isArray(value) && value[0] === '--discover').length;
  while (!discoveries()) await wait(5);
  await f.start();
  const during = discoveries(); await wait(60);
  assert.equal(discoveries(), during);
  f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
  f.child().close();
  const deadline = Date.now() + 2000;
  while (discoveries() === during && Date.now() < deadline) await wait(5);
  assert.ok(discoveries() > during, 'discovery resumes once the recording helper has exited');
});

test('recording always launches game first and a helper that is not game first is stopped', async t => {
  const f = await fixture(t);
  const refused = f.driver.start({ pid: 123, priority: 'normal', outputDirectory: f.directory });
  while (!f.child()) await wait(1);
  assert.deepEqual(f.calls.find(value => value.args).args.slice(-2), ['--priority', 'below-normal'], 'callers cannot raise it');
  f.child().send({ type: 'started', encoder: 'obs_nvenc_h264_tex', width: 1280, height: 720, fps: 30,
    priority: 'normal', gpuPriority: 'normal', startedAtVerified: true, startedAt: new Date().toISOString() });
  await assert.rejects(refused, { code: 'recorder-protocol-invalid' });
  assert.equal(f.child().killed, true);
});

test('per-minute frame stats are kept when well formed and dropped otherwise', async t => {
  const perMinute = { frames: [3600, 1200], repeated: [40, 1], lagged: [2, 0] };
  for (const [sent, expected] of [
    [perMinute, perMinute],
    [{ ...perMinute, lagged: [2] }, undefined],
    [{ ...perMinute, repeated: [40, -1] }, undefined],
    [{ ...perMinute, frames: [3600, 1.5] }, undefined],
    [{ frames: Array(301).fill(1), repeated: Array(301).fill(0), lagged: Array(301).fill(0) }, undefined],
    ['many', undefined],
  ]) {
    const f = await fixture(t); await f.start();
    f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0,
      stats: { frames: 4800, laggedFrames: 2, repeatedFrames: 41, perMinute: sent } });
    while (!f.stopped.length) await wait(1);
    f.child().close();
    const { stats } = f.stopped[0];
    assert.equal(stats.repeatedFrames, 41);
    assert.deepEqual(stats.perMinute, expected);
  }
});

test('reported capture pacing is kept when plausible and becomes null otherwise without failing the recording', async t => {
  for (const [reported, expected] of [
    [{ captureMinIntervalHns: 166666 }, 166666], [{ captureMinIntervalHns: 10_000_000 }, 10_000_000],
    [{ captureMinIntervalHns: null }, null], [{}, null],
    ...[0, -166666, 166666.5, '166666', 10_000_001, true].map(value => [{ captureMinIntervalHns: value }, null]),
  ]) {
    const f = await fixture(t);
    const settings = await f.start('obs_nvenc_h264_tex', reported);
    assert.equal(settings.captureMinIntervalHns, expected);
    f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
    while (!f.stopped.length) await wait(1);
    f.child().close();
    assert.equal(f.stopped[0].hasError, false); assert.deepEqual(f.failures, []);
  }
});

test('helper receives only host-owned scope and stop waits for process closure', async t => {
  const f = await fixture(t); const settings = await f.start();
  assert.equal(settings.encoder, 'obs_nvenc_h264_tex');
  const launch = f.calls.find(value => value.args);
  assert.deepEqual(launch.args, ['--record', '--pid', '123', '--output', path.join(f.directory, 'capture.mp4'), '--preset', '720p30',
    '--priority', 'below-normal']);
  assert.equal(settings.priority, 'below-normal'); assert.equal(settings.gpuPriority, 'below-normal');
  assert.equal(launch.settings.shell, false); assert.equal(launch.settings.detached, false); assert.equal(launch.settings.windowsHide, true);
  const child = f.child();
  let input = ''; child.stdin.on('data', chunk => { input += chunk; });
  let resolved = false;
  const stop = f.driver.stop().then(() => { resolved = true; });
  child.send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
  await wait(1);
  assert.equal(resolved, false); assert.equal(f.stopped.length, 1);
  assert.ok(f.stopped[0].startTimeEpoch > 0);
  assert.deepEqual(JSON.parse(input.trim()), { command: 'stop' });
  child.close(); await stop;
  assert.deepEqual(f.failures, []);
});

test('a software encoder from the helper is rejected and the process is killed', async t => {
  for (const recordingTest of [false, true]) {
    const f = await fixture(t, { recordingTest, deadlines: { stop: 10 },
      getContext: async () => ({ gameId: 9876, queueId: recordingTest ? null : 420, phase: 'InProgress' }) });
    const pending = f.driver.start({ pid: 123, outputDirectory: f.directory });
    while (!f.child()) await wait(1);
    f.child().send({ type: 'started', encoder: 'obs_x264', width: 1280, height: 720, fps: 30 });
    await assert.rejects(pending, { code: 'recorder-protocol-invalid' });
    assert.equal(f.child().killed, true);
  }
});

test('helper crash completes once as a failure and cannot claim verified media', async t => {
  const f = await fixture(t); await f.start();
  f.child().close(1);
  assert.equal(f.stopped.length, 1); assert.equal(f.stopped[0].hasError, true);
  await assert.rejects(f.driver.validate(f.stopped[0]), { code: 'recording-incomplete' });
  assert.deepEqual(f.failures, ['recorder-process-exited']);
});

test('a window lost during native startup can retry without disabling the provider', async t => {
  const f = await fixture(t);
  const pending = f.driver.start({ pid: 123, outputDirectory: f.directory });
  while (!f.child()) await wait(1);
  f.child().send({ type: 'error', code: 'source-window-not-ready' });
  f.child().close(1);
  await assert.rejects(pending, { code: 'source-window-not-ready' });
  assert.deepEqual(f.failures, []);
  await f.start();
  f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
  f.child().close();
});

test('heartbeats keep an occluded capture alive even when frame counts do not advance', async t => {
  const f = await fixture(t, { deadlines: { heartbeat: 25 } }); await f.start();
  for (let count = 0; count < 5; count++) { f.child().send({ type: 'heartbeat', frames: 3 }); await wait(10); }
  assert.equal(f.child().killed, false);
  await wait(45);
  assert.equal(f.child().killed, true); assert.ok(f.failures.includes('recorder-heartbeat-lost'));
});

test('missing hardware support leaves recorder unavailable without spawning capture', async () => {
  const driver = createLibobsRecorder({ helperPath: path.resolve('revu-recorder.exe'), ffmpegPath: path.resolve('ffmpeg.exe'),
    ffprobePath: path.resolve('ffprobe.exe'), queryHelper: async () => ({ stdout: JSON.stringify({
      type: 'capabilities', protocol: 1, available: false, encoders: [], selectedEncoder: null, code: 'hardware-encoder-unavailable' }) }) });
  await assert.rejects(driver.initialize(), { code: 'hardware-encoder-unavailable' });
  await driver.dispose();
});

test('hardware probe permits bounded vendor driver checks without lengthening process discovery', async t => {
  for (const deadlines of [{}, { probe: 2500, query: 1500 }]) {
    const queries = [];
    const f = await fixture(t, { deadlines, queryHelper: async (_file, args, options) => {
      queries.push({ args, timeout: options.timeout });
      return { stdout: JSON.stringify(args[0] === '--probe'
        ? { type: 'capabilities', protocol: 1, available: true, encoders: ['obs_nvenc_h264_tex'], selectedEncoder: 'obs_nvenc_h264_tex' }
        : { type: 'games', games: [{ pid: 123, width: 1920, height: 1080 }] }) };
    } });
    await f.driver.prepareStart({ pid: 123 });
    assert.deepEqual(queries, [{ args: ['--probe'], timeout: deadlines.probe ?? 25_000 },
      { args: ['--discover'], timeout: deadlines.query ?? 10_000 }]);
  }
  for (const deadlines of [{ probe: 0 }, { probe: NaN }, { probe: '25000' }, { probe: 120_001 }, { query: 0 }]) {
    assert.throws(() => createLibobsRecorder({ helperPath: path.resolve('revu-recorder.exe'), ffmpegPath: path.resolve('ffmpeg.exe'),
      ffprobePath: path.resolve('ffprobe.exe'), deadlines }), { code: 'recorder-options-invalid' });
  }
});

test('detected NVIDIA, AMD and Intel hardware selections reach capture without renderer encoder arguments', async t => {
  for (const encoder of ['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2']) {
    const f = await fixture(t, { capabilities: { type: 'capabilities', protocol: 1, available: true,
      encoders: [encoder], selectedEncoder: encoder } });
    const detected = await f.driver.initialize();
    assert.deepEqual(detected, { encoders: [encoder], selectedEncoder: encoder });
    detected.encoders.push('obs_x264'); detected.selectedEncoder = 'obs_x264';
    assert.deepEqual(await f.driver.initialize(), { encoders: [encoder], selectedEncoder: encoder });
    assert.equal((await f.start(encoder)).encoder, encoder);
    assert.ok(!f.calls.find(value => value.args).args.includes('--encoder'));
    f.child().send({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });
    f.child().close();
  }
});

test('capability protocol rejects unsupported, software, duplicate or unlisted selected encoders', async t => {
  const base = { type: 'capabilities', protocol: 1, available: true,
    encoders: ['obs_nvenc_h264_tex'], selectedEncoder: 'obs_nvenc_h264_tex' };
  for (const changes of [
    { encoders: ['obs_x264'], selectedEncoder: 'obs_x264' },
    { encoders: ['obs_nvenc_h264_tex', 'unknown_encoder'] },
    { encoders: ['obs_nvenc_h264_tex', 'obs_nvenc_h264_tex'] },
    { selectedEncoder: 'h264_texture_amf' }, { selectedEncoder: null },
    { available: false }, { encoders: [] }, { protocol: 2 },
  ]) {
    const driver = createLibobsRecorder({ helperPath: path.resolve('revu-recorder.exe'), ffmpegPath: path.resolve('ffmpeg.exe'),
      ffprobePath: path.resolve('ffprobe.exe'), queryHelper: async () => ({ stdout: JSON.stringify({ ...base, ...changes }) }) });
    t.after(() => driver.dispose());
    await assert.rejects(driver.initialize(), { code: 'recorder-protocol-invalid' });
  }
});

test('a started vendor that differs from the detected selection is rejected even when listed as hardware', async t => {
  const f = await fixture(t, { capabilities: { type: 'capabilities', protocol: 1, available: true,
    encoders: ['obs_nvenc_h264_tex', 'h264_texture_amf'], selectedEncoder: 'obs_nvenc_h264_tex' } });
  await assert.rejects(f.start('h264_texture_amf'), { code: 'recorder-protocol-invalid' });
  assert.equal(f.child().killed, true);
  assert.ok(f.failures.includes('recorder-protocol-invalid'));
});

const cleanStop = f => ({ type: 'stopped', hasError: false, filePath: path.join(f.directory, 'capture.mp4'), duration: 60_000, splitCount: 0 });

test('capture that never became active at startup skips that match; three matches in a row turn recording off', async t => {
  const f = await fixture(t);
  const miss = async () => {
    const previous = f.child();
    const pending = f.driver.start({ pid: 123, outputDirectory: f.directory });
    while (!f.child() || f.child() === previous) await wait(1);
    f.child().send({ type: 'error', code: 'capture-unavailable' });
    f.child().close(1);
    await assert.rejects(pending, { code: 'capture-unavailable' });
  };
  await miss(); await miss();
  assert.deepEqual(f.failures, [], 'League minimized or still settling costs only that match');
  await f.start();
  f.child().send(cleanStop(f)); f.child().close();
  await miss(); await miss();
  assert.deepEqual(f.failures, [], 'a recording that started clears the count');
  await miss();
  assert.deepEqual(f.failures, ['capture-unavailable']);
});

test('a slow exit after a clean stopped result is teardown: the helper is ended and recording stays on', async t => {
  const f = await fixture(t, { deadlines: { start: 200, stop: 100, teardown: 20, heartbeat: 100 } });
  await f.start();
  const child = f.child();
  const stopping = f.driver.stop();
  child.send(cleanStop(f));
  await stopping;
  assert.equal(child.killed, true);
  assert.equal(f.stopped.length, 1); assert.equal(f.stopped[0].hasError, false);
  assert.deepEqual(f.failures, []);
});

test('a non-zero exit right after a clean stopped result is logged, not treated as a fault', async t => {
  const lines = [];
  const f = await fixture(t, { log: line => lines.push(line) });
  await f.start();
  const stopping = f.driver.stop();
  f.child().send(cleanStop(f)); f.child().close(124);
  await stopping;
  assert.equal(f.stopped[0].hasError, false);
  assert.deepEqual(f.failures, []);
  assert.ok(lines.some(line => /exit code 124/.test(line)));
});

test('a stop the helper never confirms is still a fault', async t => {
  // A long heartbeat budget, so the stop deadline is what ends the helper.
  const f = await fixture(t, { deadlines: { start: 200, stop: 50, heartbeat: 5000 } });
  await f.start();
  await assert.rejects(f.driver.stop(), { code: 'recorder-stop-failed' });
  assert.equal(f.child().killed, true);
  assert.equal(f.stopped[0].hasError, true);
  assert.ok(f.failures.includes('recorder-stop-failed'));
});

test('a probe that timed out or could not launch is retried; hardware and protocol answers are final', () => {
  for (const code of ['recording-timeout', 'recorder-unavailable', 'ETIMEDOUT', 'EPERM', undefined])
    assert.equal(retryableProbeFailure(code), true, String(code));
  for (const code of ['hardware-encoder-unavailable', 'capture-unavailable', 'wgc-unavailable', 'recorder-protocol-invalid', 'ENOENT'])
    assert.equal(retryableProbeFailure(code), false, code);
});
