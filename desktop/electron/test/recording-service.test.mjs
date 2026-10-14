import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, writeFile, rename, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { RecordingService, recordingSettings } from '../recording-service.mjs';

async function settle(service) {
  for (let i = 0; i < 10; i++) { const serial = service.serial; await serial; if (serial === service.serial) break; }
}
async function fixture(t, options = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'Revu.Recording.'));
  let time = Date.parse('2026-09-12T12:00:00Z');
  const context = { gameId: 501, isGameInProgress: true, gameTimeSeconds: 0.5, observedAt: new Date(time).toISOString(), lastEndedGameId: null,
    lastEndedGameTimeSeconds: 60.5, lastEndedObservedAt: new Date(time).toISOString(), lastEndedConfirmedAt: new Date(time).toISOString() };
  const registrations = [], calls = [];
  let callback, result;
  const driver = {
    async initialize() {}, async dispose() {},
    async start({ outputDirectory, onStopped }) {
      calls.push('start'); callback = onStopped;
      await writeFile(path.join(outputDirectory, 'capture.mp4'), 'fixture');
      result = { hasError: false, filePath: path.join(outputDirectory, 'capture.mp4'), duration: 60, startTimeEpoch: time };
      return { encoder: 'fake' };
    },
    async stop() { calls.push('stop'); callback?.(result); },
    async validate(value) { return { filePath: value.filePath, fileSize: 7, durationSeconds: value.duration,
      startedAt: new Date(value.startTimeEpoch).toISOString(), startedAtVerified: true }; },
  };
  const backend = { async request(route, request) {
    if (route === '/api/recording/context') return { ...context };
    assert.equal(route, '/api/recording/register'); registrations.push(request.body);
    return { ok: true, status: request.body.complete ? 'linked' : 'retained-partial' };
  } };
  const service = new RecordingService({ root: path.join(directory, 'Recordings'), profile: path.join(directory, 'Profile'), backend,
    now: () => time, space: async () => 10 * 1024 ** 3, deadlines: { start: 100, stop: 100, callback: 100, validate: 100 }, ...options });
  await service.initialize();
  t.after(async () => { await service.shutdown(); await rm(directory, { recursive: true, force: true }); });
  return { service, driver, context, registrations, calls, directory, advance: ms => { time += ms; }, callback: () => callback(result) };
}
async function start(f) {
  await f.service.saveSettings({ enabled: true, preset: '720p30' });
  await f.service.attachDriver(f.driver);
  f.service.onGame({ pid: 100, running: true }); await settle(f.service);
}

test('recording preferences are explicit and cannot smuggle sources or encoder flags', () => {
  assert.deepEqual(recordingSettings({ enabled: true, preset: '1080p30' }), { enabled: true, preset: '1080p30', priority: 'below-normal' });
  assert.deepEqual(recordingSettings({ enabled: true, preset: '1080p30', priority: 'normal' }), { enabled: true, preset: '1080p30', priority: 'normal' });
  for (const value of [{ enabled: 1, preset: '720p30' }, { enabled: true, preset: '4k' }, { enabled: true, preset: '720p30', microphone: true },
    { enabled: true, preset: '720p30', priority: 'high' }, { enabled: true, preset: '720p30', priority: 'realtime' },
    { enabled: true, preset: '720p30', priority: null }]) assert.throws(() => recordingSettings(value));
});

test('the chosen recorder priority reaches the helper and is kept with the recording', async t => {
  const f = await fixture(t); const original = f.driver.start, requested = [];
  f.driver.start = async args => { requested.push(args.priority); return { ...await original(args), priority: args.priority }; };
  await f.service.saveSettings({ enabled: true, preset: '720p30', priority: 'normal' });
  await f.service.attachDriver(f.driver);
  f.service.onGame({ pid: 100, running: true }); await settle(f.service);
  assert.deepEqual(requested, ['normal']);
  assert.equal(f.service.getStatus().settings.priority, 'normal');
  const disk = JSON.parse(await readFile(path.join(f.service.current.directory, 'session.json'), 'utf8'));
  assert.equal(disk.priority, 'normal'); assert.equal(disk.resolvedSettings.priority, 'normal');
  const saved = JSON.parse(await readFile(path.join(f.service.profile, 'recording-settings.json'), 'utf8'));
  assert.deepEqual(saved, { enabled: true, preset: '720p30', priority: 'normal' });
});
test('no access leaves app usable and preserves the opt-in without starting capture', async t => {
  const f = await fixture(t);
  await f.service.saveSettings({ enabled: true, preset: '1080p30' });
  assert.equal(f.service.getStatus().state, 'unavailable');
  assert.equal(f.service.getStatus().settings.enabled, true);
  assert.deepEqual(f.calls, []);
});

test('hardware detection, actual active encoder and last encoder are visible while durable settings retain the actual ID', async t => {
  for (const encoder of ['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2']) {
    const f = await fixture(t, { recordingTest: true });
    f.driver.initialize = async () => ({ encoders: [encoder], selectedEncoder: encoder });
    const original = f.driver.start;
    f.driver.start = async args => ({ ...await original(args), encoder });
    await start(f);
    assert.deepEqual(f.service.getStatus().encoders, [encoder]);
    assert.equal(f.service.getStatus().selectedEncoder, encoder);
    assert.equal(f.service.getStatus().activeEncoder, encoder);
    assert.equal(f.service.getStatus().lastEncoder, encoder);
    f.service.getStatus().encoders.push('obs_x264');
    assert.deepEqual(f.service.getStatus().encoders, [encoder]);
    const directory = f.service.current.directory;
    f.service.onGame({ pid: 100, running: false }); await settle(f.service);
    assert.equal(f.service.getStatus().activeEncoder, null);
    assert.equal(f.service.getStatus().selectedEncoder, encoder);
    assert.equal(f.service.getStatus().lastEncoder, encoder);
    const disk = JSON.parse(await readFile(path.join(directory, 'session.json'), 'utf8'));
    assert.equal(disk.resolvedSettings.encoder, encoder);
    assert.deepEqual(f.registrations, []);
  }
});

test('missing hardware explains NVIDIA, AMD and Intel support even when recording remains off', async t => {
  const f = await fixture(t);
  f.driver.initialize = async () => { throw Object.assign(new Error('unsupported'), { code: 'hardware-encoder-unavailable' }); };
  await f.service.attachDriver(f.driver);
  assert.equal(f.service.getStatus().available, false);
  assert.equal(f.service.getStatus().state, 'unavailable');
  assert.equal(f.service.getStatus().settings.enabled, false);
  assert.equal(f.service.getStatus().selectedEncoder, null);
  assert.deepEqual(f.service.getStatus().encoders, []);
  assert.match(f.service.message, /No supported hardware encoder.*NVIDIA NVENC, AMD AMF and Intel Quick Sync/);
  assert.deepEqual(f.calls, []);
});

test('unavailable Windows capture or game audio does not falsely blame the hardware encoder', async t => {
  for (const code of ['capture-unavailable', 'wgc-unavailable']) {
    const f = await fixture(t);
    f.driver.initialize = async () => { throw Object.assign(new Error('unsupported'), { code }); };
    await f.service.attachDriver(f.driver);
    assert.equal(f.service.getStatus().available, false);
    assert.equal(f.service.getStatus().state, 'unavailable');
    assert.match(f.service.message, /League window capture or game audio.*Windows PC.*Folder import/);
    assert.doesNotMatch(f.service.message, /hardware encoder|graphics driver/);
    assert.deepEqual(f.calls, []);
  }
});

test('changing to libobs clears the old provider opt-in once and preserves a later deliberate choice', async t => {
  const f = await fixture(t);
  await f.service.saveSettings({ enabled: true, preset: '1080p30' });
  await f.service.prepareProvider('libobs');
  assert.equal(f.service.settings.enabled, false); assert.equal(f.service.settings.preset, '1080p30');
  assert.equal(JSON.parse(await readFile(path.join(f.service.profile, 'recording-settings.json'))).enabled, false);
  await f.service.saveSettings({ enabled: true, preset: '1080p30' });
  await f.service.prepareProvider('libobs');
  assert.equal(f.service.settings.enabled, true);
});

test('loading window preflight retries without empty session directories', async t => {
  const f = await fixture(t);
  let ready = false;
  f.driver.prepareStart = async () => { if (!ready) throw Object.assign(new Error('not ready'), { code: 'source-window-not-ready' }); };
  await start(f);
  assert.deepEqual(await readdir(f.service.root), []);
  assert.equal(f.service.current, null); assert.equal(f.service.suppressedPid, null);
  ready = true; await f.service.tick(); await settle(f.service);
  assert.equal(f.service.state, 'recording'); assert.equal(f.calls.filter(value => value === 'start').length, 1);
});

test('a transient missing match context during preflight can recover for the same PID', async t => {
  const f = await fixture(t);
  let ready = false;
  f.driver.prepareStart = async () => { if (!ready) throw Object.assign(new Error('unknown'), { code: 'recording-context-unavailable' }); };
  await start(f);
  assert.deepEqual(await readdir(f.service.root), []);
  assert.equal(f.service.suppressedPid, null);
  ready = true; await f.service.tick(); await settle(f.service);
  assert.equal(f.service.state, 'recording');
});
test('native game launch starts once; confirmed match end validates and attaches exact game', async t => {
  const f = await fixture(t); await start(f);
  assert.equal(f.service.getStatus().recordingTest, false);
  assert.equal(Object.hasOwn(f.service.current, 'testCapture'), false);
  f.service.onGame({ pid: 100, running: true }); await settle(f.service);
  assert.equal(f.calls.filter(x => x === 'start').length, 1);
  assert.equal(f.service.state, 'recording');
  f.context.lastEndedGameId = 501; f.context.isGameInProgress = false;
  await f.service.tick(); await settle(f.service);
  assert.equal(f.service.state, 'saved'); assert.equal(f.registrations.length, 1);
  assert.equal(f.registrations[0].gameId, 501); assert.equal(f.registrations[0].complete, true);
  f.callback(); await settle(f.service); assert.equal(f.registrations.length, 1);
});

test('test capture intent is durable and validated footage saves without match registration or a game clock', async t => {
  const f = await fixture(t, { recordingTest: true });
  f.context.gameTimeSeconds = null; f.context.observedAt = null;
  const original = f.driver.start;
  f.driver.start = async args => {
    const intent = JSON.parse(await readFile(path.join(args.outputDirectory, 'session.json'), 'utf8'));
    assert.equal(intent.testCapture, true); assert.equal(intent.state, 'starting'); assert.equal(intent.complete, false);
    return original(args);
  };
  await start(f);
  assert.equal(f.service.getStatus().recordingTest, true);
  const directory = f.service.current.directory;
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  assert.equal(f.service.state, 'saved'); assert.equal(f.service.pending.size, 0);
  assert.equal(f.service.message, 'Test recording saved. Open recordings folder to play it.');
  assert.deepEqual(f.registrations, []);
  const disk = JSON.parse(await readFile(path.join(directory, 'session.json'), 'utf8'));
  assert.equal(disk.testCapture, true); assert.equal(disk.complete, false); assert.equal(disk.gameClockAnchor, null);
  assert.equal(Object.hasOwn(disk, 'registration'), false);
  f.advance(181_000); await f.service.tick();
  assert.deepEqual(f.registrations, []);
});

test('marked pending test footage is terminal after ordinary-launch recovery and cannot register from a stale queue', async t => {
  const f = await fixture(t, { recordingTest: true }); await start(f);
  const directory = f.service.current.directory;
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  const file = path.join(directory, 'session.json');
  const legacy = { ...JSON.parse(await readFile(file, 'utf8')), state: 'awaiting-match', complete: true,
    registration: { sessionId: path.basename(directory), gameId: 501, complete: true },
    registrationStatus: 'pending', retryAttempts: 10, nextRetryAt: Date.now() + 60_000 };
  await writeFile(file, JSON.stringify(legacy));
  const next = new RecordingService({ root: f.service.root, profile: f.service.profile, backend: f.service.backend });
  t.after(async () => { await next.shutdown(); });
  await next.initialize();
  assert.equal(next.getStatus().recordingTest, false); assert.equal(next.state, 'saved'); assert.equal(next.pending.size, 0);
  let disk = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(disk.state, 'saved'); assert.equal(disk.complete, false);
  assert.equal(Object.hasOwn(disk, 'registration'), false); assert.equal(Object.hasOwn(disk, 'nextRetryAt'), false);
  const stale = { ...legacy, directory };
  next.pending.set(stale.sessionId, stale);
  await next.reconcile(stale); await next.tick();
  assert.equal(next.pending.size, 0); assert.deepEqual(f.registrations, []);
  disk = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(disk.complete, false); assert.equal(Object.hasOwn(disk, 'registration'), false);
});

test('test recordings retain validation failures and interrupted captures as partial without registration', async t => {
  const invalid = await fixture(t, { recordingTest: true });
  invalid.driver.validate = async () => { throw new Error('invalid media'); };
  await start(invalid);
  invalid.service.onGame({ pid: 100, running: false }); await settle(invalid.service);
  assert.equal(invalid.service.state, 'partial'); assert.equal(invalid.service.pending.size, 0);
  assert.deepEqual(invalid.registrations, []);
  const interrupted = await fixture(t, { recordingTest: true }); await start(interrupted);
  const directory = interrupted.service.current.directory;
  await interrupted.service.saveSettings({ enabled: false, preset: '720p30' }); await settle(interrupted.service);
  const disk = JSON.parse(await readFile(path.join(directory, 'session.json'), 'utf8'));
  assert.equal(disk.state, 'partial'); assert.equal(disk.reason, 'recording-disabled'); assert.equal(disk.complete, false);
  assert.equal(interrupted.service.pending.size, 0);
  assert.deepEqual(interrupted.registrations, []);
});
test('game process exit alone never claims full-match completion', async t => {
  const f = await fixture(t); await start(f);
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  assert.equal(f.registrations.length, 0);
  f.advance(181_000); await f.service.tick();
  assert.equal(f.registrations[0].complete, false); assert.equal(f.service.state, 'partial');
});

test('successful native EOF before the eventual match end cannot become a full recording', async t => {
  const f = await fixture(t); await start(f);
  f.callback(); await settle(f.service);
  assert.equal(f.registrations.length, 0);
  f.advance(60_000);
  f.context.lastEndedGameId = 501; f.context.lastEndedGameTimeSeconds = 120.5;
  f.context.lastEndedObservedAt = f.context.lastEndedConfirmedAt = '2026-09-12T12:01:00Z';
  await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations[0].complete, false);
  assert.equal(f.service.state, 'partial');
});

test('missing or stale final game clock cannot prove full-match coverage', async t => {
  for (const observedAt of [null, '2026-09-12T11:59:40Z']) {
    const f = await fixture(t); await start(f);
    f.context.lastEndedGameId = 501; f.context.lastEndedObservedAt = observedAt;
    await f.service.tick(); await settle(f.service);
    assert.equal(f.registrations[0].complete, false);
  }
});
test('reconnect preserves separate partial segments associated with the same match', async t => {
  const f = await fixture(t); await start(f);
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  f.service.onGame({ pid: 101, running: true }); await settle(f.service);
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations.length, 2);
  assert.ok(f.registrations.every(row => row.gameId === 501 && !row.complete));
  assert.notEqual(f.registrations[0].filePath, f.registrations[1].filePath);
});
test('opting out during a match stops capture and retains footage as partial', async t => {
  const f = await fixture(t); await start(f);
  await f.service.saveSettings({ enabled: false, preset: '720p30' }); await settle(f.service);
  assert.equal(f.registrations[0].complete, false); assert.ok(f.calls.includes('stop'));
  await f.service.tick(); assert.equal(f.calls.filter(x => x === 'start').length, 1);
});
test('late startup cannot label the available tail as a full match', async t => {
  const f = await fixture(t); f.context.gameTimeSeconds = 120; await start(f);
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations[0].complete, false);
});

test('out-of-range provider timing is retained as partial with a valid registration body', async t => {
  const f = await fixture(t);
  const validate = f.driver.validate;
  f.driver.validate = async value => ({ ...await validate(value), startedAt: '2026-09-12T11:40:00Z' });
  await start(f);
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations.length, 1);
  assert.equal(f.registrations[0].complete, false);
  assert.equal(f.registrations[0].gameTimeAtVideoStart, null);
  assert.equal(f.service.state, 'partial');
});
test('a native stop timeout blocks new capture even after a new game launch', async t => {
  const f = await fixture(t, { deadlines: { start: 50, stop: 5, callback: 5, validate: 50 } }); await start(f);
  f.driver.stop = () => new Promise(() => {});
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  f.service.onGame({ pid: 101, running: true }); await settle(f.service);
  assert.equal(f.service.blocked, true); assert.equal(f.service.state, 'partial');
  assert.equal(f.calls.filter(x => x === 'start').length, 1);
});
test('registration retries reuse the exact durable body after a lost response', async t => {
  const f = await fixture(t); await start(f); const original = f.service.backend.request;
  let lost = true;
  f.service.backend.request = async (route, options) => {
    const result = await original(route, options);
    if (route.endsWith('/register') && lost) { lost = false; throw new Error('lost reply'); }
    return result;
  };
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  f.context.lastEndedGameId = null; f.advance(2000); await f.service.tick();
  assert.equal(f.registrations.length, 2); assert.deepEqual(f.registrations[0], f.registrations[1]);
  assert.equal(f.service.state, 'saved');
  const metadata = JSON.parse(await readFile(path.join(f.registrations[0].filePath, '../session.json'), 'utf8'));
  assert.equal(metadata.complete, true); assert.equal(metadata.reason, null);
  assert.equal(metadata.registration.gameTimeAtVideoStart, 0.5);
});
test('recovery preserves interrupted output and never silently registers it complete', async t => {
  const f = await fixture(t); await start(f);
  clearInterval(f.service.timer); f.service.timer = null;
  const next = new RecordingService({ root: f.service.root, profile: f.service.profile, backend: f.service.backend });
  await next.initialize();
  assert.equal(next.state, 'partial'); assert.equal(f.registrations.length, 0);
  const dirs = await readdir(f.service.root);
  const metadata = JSON.parse(await readFile(path.join(f.service.root, dirs[0], 'session.json'), 'utf8'));
  assert.equal(metadata.complete, false); assert.equal(metadata.reason, 'host-interrupted');
});
test('isolated previews cannot enable or initialize the native Recorder', async t => {
  const f = await fixture(t, { isolated: true });
  await assert.rejects(f.service.saveSettings({ enabled: true, preset: '720p30' }));
  let initialized = false;
  await f.service.attachDriver({ initialize() { initialized = true; }, dispose() {} });
  assert.equal(initialized, false);
});

test('exit, opt-out and shutdown during disk preparation cannot enter native capture', async t => {
  for (const action of ['exit', 'disable', 'shutdown']) {
    let ready, release;
    const entered = new Promise(resolve => { ready = resolve; });
    const space = new Promise(resolve => { release = resolve; });
    const f = await fixture(t, { space: () => { ready(); return space; } });
    await f.service.saveSettings({ enabled: true, preset: '720p30' });
    await f.service.attachDriver(f.driver);
    f.service.onGame({ pid: 100, running: true });
    await entered;
    let operation;
    if (action === 'exit') f.service.onGame({ pid: 100, running: false });
    else if (action === 'disable') operation = f.service.saveSettings({ enabled: false, preset: '720p30' });
    else operation = f.service.shutdown();
    release(10 * 1024 ** 3);
    await operation; await settle(f.service);
    assert.equal(f.calls.includes('start'), false, action);
    assert.equal(f.service.current, null, action);
  }
});

test('opt-out cancels an in-flight native setup and prevents its late continuation', async t => {
  const f = await fixture(t);
  let entered, rejected = false;
  const began = new Promise(resolve => { entered = resolve; });
  f.driver.start = ({ signal }) => new Promise((resolve, reject) => {
    entered();
    signal.addEventListener('abort', () => { rejected = true; reject(Object.assign(new Error('cancelled'), { code: 'recording-aborted' })); }, { once: true });
  });
  await f.service.saveSettings({ enabled: true, preset: '720p30' });
  await f.service.attachDriver(f.driver);
  f.service.onGame({ pid: 100, running: true }); await began;
  await f.service.saveSettings({ enabled: false, preset: '720p30' }); await settle(f.service);
  assert.equal(rejected, true); assert.equal(f.service.current, null);
  assert.equal(f.service.settings.enabled, false); assert.ok(f.calls.includes('stop'));
});

test('a native callback before start resolves finalizes once without announcing a live recording', async t => {
  const states = [];
  const f = await fixture(t, { onChange: value => states.push(value.state) });
  const original = f.driver.start;
  f.driver.start = async args => { const configured = await original(args); f.callback(); return configured; };
  await start(f);
  assert.equal(states.includes('recording'), false);
  assert.equal(f.service.current, null);
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations.length, 1); assert.equal(f.service.state, 'saved');
});

test('late start completion after deadline is stopped again and cannot resurrect a timed-out session', async t => {
  const f = await fixture(t, { deadlines: { start: 5, stop: 50, callback: 5, validate: 50 } });
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const original = f.driver.start;
  f.driver.start = async args => { await original(args); return pending; };
  f.driver.stop = async () => { f.calls.push('stop'); };
  await start(f);
  assert.equal(f.service.blocked, true); assert.equal(f.service.current, null);
  const before = f.calls.filter(value => value === 'stop').length;
  release({ encoder: 'fake' });
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(f.calls.filter(value => value === 'stop').length > before);
  f.callback(); await settle(f.service);
  assert.equal(f.service.current, null); assert.equal(f.registrations.length, 0);
});

test('late callbacks after an unconfirmed stop preserve partial status and never attach', async t => {
  const f = await fixture(t, { deadlines: { start: 50, stop: 5, callback: 5, validate: 50 } });
  await start(f);
  f.driver.stop = async () => {};
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  f.context.lastEndedGameId = 501; f.callback(); await settle(f.service);
  assert.equal(f.service.state, 'partial'); assert.equal(f.service.blocked, true);
  assert.equal(f.registrations.length, 0);
});

test('failed recording journal writes cannot prevent native stop or disposal', async t => {
  const f = await fixture(t); await start(f);
  let disposed = 0;
  f.driver.dispose = async () => { disposed++; };
  f.service.persist = async () => { throw Object.assign(new Error('disk full'), { code: 'ENOSPC' }); };
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  assert.ok(f.calls.includes('stop'));
  assert.equal(f.service.current, null); assert.equal(f.service.blocked, true);
  assert.equal(f.registrations.length, 0);
  await f.service.shutdown(); await f.service.shutdown();
  assert.equal(disposed, 1);
});

test('an unwritable preference file still stops the active recording when the user opts out', async t => {
  const f = await fixture(t); await start(f);
  await rename(f.service.profile, f.service.profile + '-unavailable');
  await assert.rejects(f.service.saveSettings({ enabled: false, preset: '720p30' }), { code: 'ENOENT' });
  await settle(f.service);
  assert.ok(f.calls.includes('stop')); assert.equal(f.service.current, null);
  assert.equal(f.service.settings.enabled, false); assert.equal(f.service.requestedEnabled, false);
});

test('media validation deadlines abort owned work and keep output explicitly unverified', async t => {
  const f = await fixture(t, { deadlines: { start: 50, stop: 50, callback: 50, validate: 5 } });
  await start(f);
  let cancelled = false;
  f.driver.validate = (_result, signal) => new Promise(() => {
    signal.addEventListener('abort', () => { cancelled = true; }, { once: true });
  });
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(cancelled, true); assert.equal(f.service.state, 'partial'); assert.equal(f.registrations.length, 0);
});

test('saved pending registrations resume after restart without developer access and preserve their durable meaning', async t => {
  const f = await fixture(t); await start(f);
  const original = f.service.backend.request;
  f.service.backend.request = async (route, options) => {
    if (route.endsWith('/register')) throw new Error('offline');
    return original(route, options);
  };
  f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  const pending = [...f.service.pending.values()][0];
  const originalBody = { ...pending.registration };
  clearInterval(f.service.timer); f.service.timer = null; f.service.pending.clear();
  f.context.lastEndedGameId = 999; f.service.backend.request = original;
  const next = new RecordingService({ root: f.service.root, profile: f.service.profile, backend: f.service.backend });
  t.after(async () => { await next.shutdown(); });
  await next.initialize();
  assert.ok(next.timer); assert.equal(next.driver, null);
  await next.tick();
  assert.deepEqual(f.registrations[0], originalBody);
  assert.equal(next.state, 'saved'); assert.equal(next.pending.size, 0);
  const disk = JSON.parse(await readFile(path.join(pending.directory, 'session.json'), 'utf8'));
  assert.equal(disk.complete, true); assert.equal(disk.gameTimeAtVideoStart, 0.5);
  await next.shutdown();
});

test('source window readiness retries end 30 seconds after the first launch observation', async t => {
  const f = await fixture(t);
  f.driver.start = async () => { f.calls.push('start'); throw Object.assign(new Error('window pending'), { code: 'source-window-not-ready' }); };
  await start(f); assert.equal(f.calls.filter(value => value === 'start').length, 1);
  f.advance(10_000); f.service.onGame({ pid: 100, running: true }); await settle(f.service);
  assert.equal(f.calls.filter(value => value === 'start').length, 2);
  f.advance(21_000); f.service.onGame({ pid: 100, running: true }); await settle(f.service);
  assert.equal(f.calls.filter(value => value === 'start').length, 2);
  assert.equal(f.service.suppressedPid, 100);
});

test('normal process exit does not abort the active capture signal before the provider stop callback', async t => {
  const f = await fixture(t); let signal;
  const original = f.driver.start;
  f.driver.start = args => { signal = args.signal; return original(args); };
  await start(f);
  f.driver.stop = async () => { assert.equal(signal.aborted, false); f.callback(); };
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  assert.equal(signal.aborted, false);
});

test('terminal recorder metrics and callback latency are durable without per-frame journal work', async t => {
  const f = await fixture(t); const original = f.driver.start;
  f.driver.start = args => original({ ...args, onStopped: result => args.onStopped({ ...result,
    stats: { outputSkippedFrames: 2, renderSkippedFrames: 3, outputTotalFrames: 1800 }, requestToCallbackMs: 123 }) });
  await start(f); f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  const disk = JSON.parse(await readFile(path.join(f.registrations[0].filePath, '../session.json'), 'utf8'));
  assert.deepEqual(disk.finalStats, { outputSkippedFrames: 2, renderSkippedFrames: 3, outputTotalFrames: 1800 });
  assert.equal(disk.requestToCallbackMs, 123);
});

test('per-minute repeated and lagged frame counts are saved with the recording', async t => {
  const f = await fixture(t); const original = f.driver.start;
  const perMinute = { frames: [3600, 3600], repeated: [41, 2], lagged: [3, 0] };
  f.driver.start = args => original({ ...args, onStopped: result => args.onStopped({ ...result,
    stats: { frames: 7200, laggedFrames: 3, repeatedFrames: 43, perMinute } }) });
  await start(f); f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  const disk = JSON.parse(await readFile(path.join(f.registrations[0].filePath, '../session.json'), 'utf8'));
  assert.deepEqual(disk.finalStats, { frames: 7200, laggedFrames: 3, repeatedFrames: 43, perMinute });
  assert.equal(disk.priority, 'below-normal');
});

test('an intent timestamp never substitutes for the missing provider video-start timestamp', async t => {
  const f = await fixture(t); const original = f.driver.validate;
  f.driver.validate = async value => ({ ...await original(value), startedAtVerified: false });
  await start(f); f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service);
  assert.equal(f.registrations[0].complete, false);
  assert.equal(f.registrations[0].gameTimeAtVideoStart, null);
  assert.equal(f.service.state, 'partial');
});

test('closing after the native recording has stopped does not downgrade validation already in progress', async t => {
  const f = await fixture(t);
  let entered, release;
  const validating = new Promise(resolve => { entered = resolve; });
  const pause = new Promise(resolve => { release = resolve; });
  const original = f.driver.validate;
  f.driver.validate = async value => { entered(); await pause; return original(value); };
  await start(f); f.context.lastEndedGameId = 501;
  const finishing = f.service.tick(); await validating;
  const stopping = f.service.shutdown(); release();
  await finishing; await stopping;
  assert.equal(f.registrations[0].complete, true);
});

test('a settings change cannot advertise readiness when native reuse is blocked', async t => {
  const f = await fixture(t); await start(f);
  f.service.blocked = true;
  f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  await f.service.saveSettings({ enabled: true, preset: '1080p30' });
  assert.equal(f.service.state, 'partial'); assert.match(f.service.message, /restart Revu/);
});

test('unidentified recordings leave the finalizing state after the reconciliation deadline', async t => {
  const f = await fixture(t); f.context.gameId = null;
  await start(f); f.service.onGame({ pid: 100, running: false }); await settle(f.service);
  assert.equal(f.service.state, 'finalizing');
  f.advance(181_000); await f.service.tick();
  assert.equal(f.service.state, 'partial'); assert.equal(f.service.pending.size, 0);
  assert.match(f.service.message, /could not be identified/);
});
