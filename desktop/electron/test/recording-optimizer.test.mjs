import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { link, mkdir, mkdtemp, readFile, readdir, realpath, rename, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createRecordingOptimizer, isStaleTemporary, runTool, toolTimeout } from '../recording-optimizer.mjs';

// Synthetic top-level MP4 boxes: the real mp4Layout reads these; fake tools stand in for ffmpeg.
const box = (name, payload = Buffer.alloc(0)) => {
  const header = Buffer.alloc(8); header.writeUInt32BE(8 + payload.length, 0); header.write(name, 4, 'ascii');
  return Buffer.concat([header, payload]);
};
const ftyp = () => box('ftyp', Buffer.from('isom\0\0\0\0isomiso2'));
const fragmented = media => Buffer.concat([ftyp(), box('moov', Buffer.alloc(16)), box('moof', Buffer.alloc(24)), box('mdat', media),
  box('moof', Buffer.alloc(24)), box('mdat', Buffer.from('tail'))]);
const progressive = media => Buffer.concat([ftyp(), box('mdat', Buffer.concat([media, Buffer.from('tail')])), box('moov', Buffer.alloc(40))]);
const payload = bytes => { // the media a fake framemd5 reports: every mdat body in order
  const parts = []; for (let at = 0; at + 8 <= bytes.length;) {
    const size = bytes.readUInt32BE(at); if (bytes.toString('ascii', at + 4, at + 8) === 'mdat') parts.push(bytes.subarray(at + 8, at + size)); at += size;
  } return Buffer.concat(parts).toString('hex');
};
const MEDIA = Buffer.from('h264+aac packets');
const GAME = 4242;

async function fixture(t, { session: sessionOverrides = {}, name = 'capture.mp4', bytes = fragmented(MEDIA), ...options } = {}) {
  const directory = await realpath(await mkdtemp(path.join(os.tmpdir(), 'Revu.Optimize.')));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const root = path.join(directory, 'Recordings'), sessionId = randomUUID();
  const sessionDirectory = path.join(root, sessionId), file = path.join(sessionDirectory, name);
  await mkdir(sessionDirectory, { recursive: true });
  await writeFile(file, bytes);
  const session = { sessionId, gameId: GAME, state: 'saved', complete: true, registrationStatus: 'linked', filePath: file, fileSize: bytes.length,
    registration: { sessionId, gameId: GAME, filePath: file, fileSize: bytes.length, durationSeconds: 90, complete: true }, ...sessionOverrides };
  const sessionFile = path.join(sessionDirectory, 'session.json');
  await writeFile(sessionFile, JSON.stringify(session, null, 2));
  const events = [], logs = [], relayouts = [];
  const recording = { root, current: null, game: null, pending: new Map() };
  const tools = {
    remux: (input, output) => writeFile(output, progressive(MEDIA)), // same packets, indexed layout
    packets: async input => payload(await readFile(input)),
    probe: async () => ({ streams: [{ index: 0, codec_type: 'video', codec_name: 'h264' }, { index: 1, codec_type: 'audio', codec_name: 'aac' }],
      format: { duration: '90.000' } }),
  };
  const run = async (tool, args, { onStdout, signal }) => {
    const input = args[args.indexOf('-i') + 1] ?? args.at(-1);
    if (tool === 'ffprobe') { events.push('probe'); onStdout(Buffer.from(JSON.stringify(await tools.probe(args.at(-1))))); return {}; }
    if (args.includes('framemd5')) { events.push(`packets:${path.basename(input).includes('.optimizing') ? 'copy' : 'original'}`);
      onStdout(Buffer.from(await tools.packets(input))); return {}; }
    assert.deepEqual(args.slice(0, 13), ['-hide_banner', '-nostdin', '-v', 'error', '-i', file, '-map', '0', '-c', 'copy',
      '-avoid_negative_ts', 'disabled', '-f']);
    events.push('remux'); await tools.remux(input, args.at(-1), signal); return {};
  };
  const media = { async revoke(target) {
    events.push('revoke'); assert.equal(target, file);
    assert.equal((await readFile(file)).equals(bytes), true, 'the original is untouched until the swap');
    return () => { events.push('release'); };
  } };
  const backend = { vod: { hasVod: true, gameId: GAME, filePath: file }, context: { isGameInProgress: false, phase: 'None' },
    async request(route, request = {}) {
    if (route === `/api/vod?gameId=${GAME}`) return backend.vod;
    if (route.startsWith('/api/vod?')) return { hasVod: false };
    if (route === '/api/recording/context') { if (backend.context instanceof Error) throw backend.context; return backend.context; }
    assert.equal(route, '/api/recording/relayout'); assert.equal(request.method, 'POST');
    events.push('relayout'); relayouts.push(request.body);
    assert.equal(JSON.parse(await readFile(sessionFile, 'utf8')).container, undefined, 'the sidecar is told before session.json');
    if (backend.relayoutError) throw backend.relayoutError;
    return { ok: true, sessionId, gameId: GAME, status: 'linked' };
  } };
  const optimizer = createRecordingOptimizer({ recording, backend, media, ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', run,
    log: { info: message => logs.push(message), warn: message => logs.push(message) }, space: async () => 100 * 1024 ** 3,
    now: () => Date.parse('2026-10-13T12:00:00Z'), pollMs: 20, minFragments: 1, ...options });
  const leftovers = async () => (await readdir(sessionDirectory)).filter(entry => entry.includes('.optimizing'));
  return { root, sessionId, sessionDirectory, file, bytes, sessionFile, session, recording, events, logs, relayouts, tools, media,
    backend, optimizer, leftovers };
}

test('a linked fragmented recording is verified, swapped, then recorded by the sidecar before session.json', async t => {
  const f = await fixture(t);
  assert.deepEqual(await f.optimizer.status({ gameId: GAME }),
    { eligible: true, ready: true, reason: null, fileSize: f.bytes.length, freeBytes: 100 * 1024 ** 3, requiredBytes: f.bytes.length + 4 * 1024 ** 3 });
  const result = await f.optimizer.optimize({ gameId: GAME });
  const optimized = await readFile(f.file);
  assert.deepEqual(result, { ok: true, status: 'optimized', fileSize: optimized.length, previousFileSize: f.bytes.length });
  assert.equal(payload(optimized), payload(f.bytes));
  // The file is held from page streams for the whole job, not only the swap.
  assert.deepEqual(f.events, ['revoke', 'remux', 'packets:original', 'packets:copy', 'probe', 'probe', 'release', 'relayout']);
  assert.deepEqual(f.relayouts, [{ sessionId: f.sessionId, fileSize: optimized.length }]);
  const session = JSON.parse(await readFile(f.sessionFile, 'utf8'));
  const rest = ({ fileSize, registration, container, optimizedAt, ...value }) => value;
  assert.deepEqual(rest(session), rest(f.session), 'only the size and layout fields change');
  assert.equal(session.fileSize, optimized.length);
  assert.deepEqual(session.registration, { ...f.session.registration, fileSize: optimized.length });
  assert.equal(session.container, 'progressive');
  assert.equal(session.optimizedAt, '2026-10-13T12:00:00.000Z');
  assert.deepEqual(await f.leftovers(), []);
  assert.deepEqual(await f.optimizer.status({ gameId: GAME }), { eligible: false, ready: false, reason: 'already-optimized' });
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: true, status: 'already-optimized', fileSize: optimized.length });
  assert.equal(f.events.filter(event => event === 'remux').length, 1);
});

test('only an exact linked built-in recording is eligible, and a refusal never runs a tool', async t => {
  const id = randomUUID();
  const cases = [
    ['no linked video', { vod: { hasVod: false, gameId: GAME, filePath: '' } }, 'no-recording'],
    ['another game snapshot', { vod: game => ({ hasVod: true, gameId: GAME + 1, filePath: game.file }) }, 'no-recording'],
    ['a missing file', { vod: game => ({ hasVod: true, gameId: GAME, filePath: game.file + '.gone.mp4' }) }, 'no-recording'],
    ['an Ascent file outside the root', { vod: async game => {
      const outside = path.join(path.dirname(game.root), 'Ascent.mp4'); await writeFile(outside, game.bytes);
      return { hasVod: true, gameId: GAME, filePath: outside }; } }, 'not-built-in'],
    ['a non-capture name', { name: 'clip.mp4' }, 'not-built-in'],
    ['a nested file', { vod: async game => {
      const nested = path.join(game.sessionDirectory, 'nested', 'capture.mp4'); await mkdir(path.dirname(nested)); await writeFile(nested, game.bytes);
      return { hasVod: true, gameId: GAME, filePath: nested }; } }, 'not-built-in'],
    ['a non-session directory', { vod: async game => {
      const other = path.join(game.root, 'imports', 'capture.mp4'); await mkdir(path.dirname(other)); await writeFile(other, game.bytes);
      return { hasVod: true, gameId: GAME, filePath: other }; } }, 'not-built-in'],
    ['a hard link', { prepare: game => link(game.file, path.join(game.sessionDirectory, 'capture-alias.mp4')) }, 'not-built-in'],
    ['no session metadata', { prepare: game => rm(game.sessionFile) }, 'not-built-in'],
    ['oversized session metadata', { prepare: game => writeFile(game.sessionFile, JSON.stringify({ ...game.session, pad: 'x'.repeat(70_000) })) }, 'not-built-in'],
    ['a different session id', { session: { sessionId: id } }, 'not-linked'],
    ['a session still awaiting its match', { session: { state: 'awaiting-match' } }, 'not-linked'],
    ['a video the match did not keep', { session: { registrationStatus: 'retained-existing' } }, 'not-linked'],
    ['a test capture', { session: { testCapture: true } }, 'not-linked'],
    ['another game registration', { prepare: game => writeFile(game.sessionFile,
      JSON.stringify({ ...game.session, registration: { ...game.session.registration, gameId: GAME + 1 } })) }, 'not-linked'],
    ['the active recording', { prepare: game => { game.recording.current = { sessionId: game.sessionId }; } }, 'not-linked', 'recording-active'],
    ['a pending session', { prepare: game => { game.recording.pending.set(game.sessionId, {}); } }, 'not-linked'],
    ['an unrecognized container', { bytes: Buffer.concat([ftyp(), box('mdat', MEDIA)]) }, 'not-eligible'],
    ['a short recording that already opens quickly', { minFragments: 3 }, 'opens-quickly'],
  ];
  for (const [label, { vod, prepare, ...options }, reason, code = 'not-eligible'] of cases) {
    const f = await fixture(t, options);
    if (vod) f.backend.vod = typeof vod === 'function' ? await vod(f) : vod;
    await prepare?.(f);
    assert.deepEqual(await f.optimizer.status({ gameId: GAME }), { eligible: false, ready: false, reason }, label);
    assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code }, label);
    assert.deepEqual(f.events, [], label);
    assert.equal((await readFile(f.file)).equals(f.bytes), true, label);
  }
  const f = await fixture(t);
  for (const gameId of [0, -1, 1.5, '4242', undefined])
    assert.deepEqual(await f.optimizer.optimize({ gameId }), { ok: false, code: 'not-eligible' });
});

test('a League game, an active recording, low space or closing Revu refuse before any work', async t => {
  const f = await fixture(t);
  f.recording.game = { pid: 7 };
  assert.deepEqual(await f.optimizer.status({ gameId: GAME }), { eligible: true, ready: false, reason: 'game-running',
    fileSize: f.bytes.length, freeBytes: 100 * 1024 ** 3, requiredBytes: f.bytes.length + 4 * 1024 ** 3 });
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'game-running' });
  f.recording.game = null; f.recording.current = { sessionId: randomUUID() };
  assert.equal((await f.optimizer.status({ gameId: GAME })).reason, 'recording-active');
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'recording-active' });
  // With automatic recording off nothing discovers the game process; the sidecar still sees the match.
  f.recording.current = null; f.backend.context = { isGameInProgress: true, phase: 'InProgress' };
  assert.equal((await f.optimizer.status({ gameId: GAME })).reason, 'game-running');
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'game-running' });
  f.backend.context = { isGameInProgress: false, phase: 'Reconnect' };
  assert.equal((await f.optimizer.status({ gameId: GAME })).reason, 'game-running');
  f.backend.context = new Error('sidecar busy'); // an unknown answer never blocks
  assert.equal((await f.optimizer.status({ gameId: GAME })).reason, null);
  assert.deepEqual(f.events, []);

  // The margin stays well above the recorder's own 2 GiB start reserve.
  const low = await fixture(t, { space: async () => 4 * 1024 ** 3 });
  assert.deepEqual(await low.optimizer.status({ gameId: GAME }), { eligible: true, ready: false, reason: 'not-enough-space',
    fileSize: low.bytes.length, freeBytes: 4 * 1024 ** 3, requiredBytes: low.bytes.length + 4 * 1024 ** 3 });
  assert.deepEqual(await low.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'not-enough-space' });
  assert.deepEqual(low.events, []);

  const closed = await fixture(t, { available: () => false });
  assert.deepEqual(await closed.optimizer.status({ gameId: GAME }), { eligible: false, ready: false, reason: 'unavailable' });
  assert.deepEqual(await closed.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'unavailable' });
  assert.deepEqual(closed.events, []);
});

test('any verification mismatch deletes the copy and keeps the original byte for byte', async t => {
  const mismatches = {
    'different packets': f => { f.tools.remux = (input, output) => writeFile(output, progressive(Buffer.from('h264+aac packetZ'))); },
    'a copy that is still fragmented': f => { f.tools.remux = (input, output) => writeFile(output, fragmented(MEDIA)); },
    'a different duration': f => { const probe = f.tools.probe; let calls = 0;
      f.tools.probe = async file => { const value = await probe(file); if (++calls === 2) value.format.duration = '88.0'; return value; }; },
    'a missing stream': f => { const probe = f.tools.probe; let calls = 0;
      f.tools.probe = async file => { const value = await probe(file); if (++calls === 2) value.streams.pop(); return value; }; },
    'a failed packet pass': f => { f.tools.packets = async input => { if (input.includes('.optimizing')) throw new Error('decode'); return 'x'; }; },
  };
  for (const [label, arrange] of Object.entries(mismatches)) {
    const f = await fixture(t);
    arrange(f);
    assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'verify-failed' }, label);
    assert.equal((await readFile(f.file)).equals(f.bytes), true, label);
    assert.deepEqual(await f.leftovers(), [], label);
    assert.equal(f.events.includes('relayout'), false, label);
    assert.equal(f.events.at(-1), 'release', label);
    assert.deepEqual(JSON.parse(await readFile(f.sessionFile, 'utf8')), f.session, label);
  }
  const f = await fixture(t);
  f.tools.remux = async (input, output) => { await writeFile(output, 'partial'); throw new Error('exit 1'); };
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'remux-failed' });
  assert.deepEqual(await f.leftovers(), []);
  assert.equal((await readFile(f.file)).equals(f.bytes), true);
});

test('a file held by another reader keeps the original, removes the copy and releases the media hold', async t => {
  let attempts = 0;
  const f = await fixture(t, { renameFile: async () => { attempts++; throw Object.assign(new Error('busy'), { code: 'EBUSY' }); } });
  assert.deepEqual(await f.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'recording-in-use' });
  assert.equal(attempts, 3);
  assert.deepEqual([f.events[0], f.events.at(-1)], ['revoke', 'release']);
  assert.equal(f.relayouts.length, 0);
  assert.deepEqual(await f.leftovers(), []);
  assert.equal((await readFile(f.file)).equals(f.bytes), true);

  let tries = 0;
  const transient = await fixture(t, { renameFile: async (from, to) => {
    if (++tries < 3) throw Object.assign(new Error('scan'), { code: 'EPERM' }); return rename(from, to); } });
  assert.equal((await transient.optimizer.optimize({ gameId: GAME })).status, 'optimized');
});

test('a sidecar that cannot record the new size still leaves a playable file and an honest session', async t => {
  const f = await fixture(t);
  f.backend.relayoutError = Object.assign(new Error('Backend HTTP 409'), { status: 409 });
  const result = await f.optimizer.optimize({ gameId: GAME });
  assert.equal(result.status, 'optimized');
  const session = JSON.parse(await readFile(f.sessionFile, 'utf8'));
  assert.equal(session.fileSize, result.fileSize);
  assert.equal(session.container, 'progressive');
  // The registration mirrors the sidecar receipt, which still has the old size.
  assert.deepEqual(session.registration, f.session.registration);
  assert.ok(f.logs.some(line => /keeps the old size \(409\)/.test(line)));
});

test('a League game starting mid-copy, or quitting Revu, stops the copy and removes it; one job runs at a time', async t => {
  const slow = f => { f.tools.remux = (input, output, signal) => new Promise((resolve, reject) => {
    void writeFile(output, 'partial').then(() => { f.started?.(); });
    signal.addEventListener('abort', () => reject(signal.reason), { once: true });
  }); };
  const game = await fixture(t);
  slow(game);
  const started = new Promise(resolve => { game.started = resolve; });
  const pending = game.optimizer.optimize({ gameId: GAME });
  await started;
  assert.deepEqual(await game.optimizer.optimize({ gameId: GAME }), { ok: false, code: 'busy' });
  // A page reopened on this game while its copy runs shows the work, not "another recording".
  assert.deepEqual(await game.optimizer.status({ gameId: GAME }), { eligible: true, ready: false, reason: 'working' });
  game.backend.context = { isGameInProgress: true, phase: 'InProgress' };
  assert.deepEqual(await pending, { ok: false, code: 'game-running' });
  assert.deepEqual(await game.leftovers(), []);
  assert.equal((await readFile(game.file)).equals(game.bytes), true);
  assert.deepEqual([game.events[0], game.events.at(-1)], ['revoke', 'release']);

  // A recording about to start stops the copy at once and counts its bytes as free.
  const start = await fixture(t, { space: async () => 5 * 1024 ** 3 });
  slow(start);
  const copying = new Promise(resolve => { start.started = resolve; });
  const job = start.optimizer.optimize({ gameId: GAME });
  await copying;
  assert.equal(await start.optimizer.recordingSpace(start.root), 5 * 1024 ** 3 + 'partial'.length);
  assert.deepEqual(await job, { ok: false, code: 'recording-active' });
  assert.deepEqual(await start.leftovers(), []);
  assert.equal(await start.optimizer.recordingSpace(start.root), 5 * 1024 ** 3);

  const quit = await fixture(t);
  slow(quit);
  const begun = new Promise(resolve => { quit.started = resolve; });
  const running = quit.optimizer.optimize({ gameId: GAME });
  await begun;
  await quit.optimizer.cancel();
  assert.deepEqual(await running, { ok: false, code: 'app-closing' });
  assert.deepEqual(await quit.leftovers(), []);
  assert.deepEqual(await quit.optimizer.status({ gameId: GAME }), { eligible: false, ready: false, reason: 'unavailable' });
});

test('startup restores a finished capture whose index never reached the disk, and nothing else', async t => {
  const hybridFtyp = ftyp(), init = box('moov', Buffer.alloc(16)), rest = fragmented(MEDIA).subarray(hybridFtyp.length + init.length);
  const recordingTime = Buffer.concat([hybridFtyp, box('free'), box('free', hybridFtyp), init, rest]);
  const lost = Buffer.concat([hybridFtyp, box('free'), box('mdat', Buffer.concat([hybridFtyp, init, rest]))]);
  const f = await fixture(t, { bytes: lost });
  const other = path.join(f.sessionDirectory, 'clip.mp4');
  await writeFile(other, lost);
  await f.optimizer.init();
  assert.ok((await readFile(f.file)).equals(recordingTime));
  assert.ok((await readFile(other)).equals(lost), 'only capture files are examined');
  assert.ok(f.logs.some(line => line.includes(`Restored the playable layout of recording ${f.sessionId}`)));
  const failing = await fixture(t, { repair: async () => { throw Object.assign(new Error('locked'), { code: 'EBUSY' }); } });
  await failing.optimizer.init();
  assert.ok(failing.logs.some(line => /could not be checked for a lost index \(EBUSY\)/.test(line)));
});

test('startup removes only this feature\'s abandoned copies inside session folders', async t => {
  const f = await fixture(t);
  const id = randomUUID();
  const removed = [`capture.mp4.${id}.optimizing`, `CAPTURE-2.MP4.${randomUUID().toUpperCase()}.optimizing`];
  const kept = ['capture.mp4.not-a-uuid.optimizing', `clip.mp4.${id}.optimizing`, `capture.mp4.${id}.optimizing.bak`,
    `capture.mp4.${id}.tmp`, `capture.mkv.${id}.optimizing`, 'capture.mp4.01234567-89ab-1def-8123-456789abcdef.optimizing'];
  for (const name of [...removed, ...kept]) await writeFile(path.join(f.sessionDirectory, name), 'x');
  await mkdir(path.join(f.sessionDirectory, `capture.mp4.${randomUUID()}.optimizing`));
  const elsewhere = [path.join(f.root, `capture.mp4.${id}.optimizing`), path.join(f.root, 'imports', `capture.mp4.${id}.optimizing`)];
  await mkdir(path.join(f.root, 'imports'));
  for (const file of elsewhere) await writeFile(file, 'x');
  await f.optimizer.init();
  const left = await readdir(f.sessionDirectory);
  for (const name of removed) assert.equal(left.includes(name), false, name);
  for (const name of [...kept, 'capture.mp4', 'session.json']) assert.equal(left.includes(name), true, name);
  assert.equal(left.filter(name => name.endsWith('.optimizing')).length, kept.filter(name => name.endsWith('.optimizing')).length + 1);
  for (const file of elsewhere) assert.ok((await stat(file)).isFile(), file);
  assert.equal(isStaleTemporary(`capture.mp4.${id}.optimizing`), true);
  assert.equal(isStaleTemporary(`capture.mp4.${id}.optimizing\\..\\x`), false);
});

test('the tool runner streams stdout, reports failures and settles only after the child exits', async t => {
  const chunks = [];
  await runTool(process.execPath, ['-e', 'process.stdout.write("frame-1\\n"); process.stdout.write("frame-2\\n")'],
    { onStdout: chunk => chunks.push(chunk) });
  assert.equal(Buffer.concat(chunks).toString(), 'frame-1\nframe-2\n');
  await assert.rejects(runTool(process.execPath, ['-e', 'console.error("bad input"); process.exit(3)']),
    error => error.exitCode === 3 && /bad input/.test(error.stderr));
  await assert.rejects(runTool(process.execPath, ['-e', 'process.stdout.write("x".repeat(100))'],
    { onStdout: () => { throw new Error('too much output'); } }), /too much output/);
  const abort = new AbortController();
  const running = runTool(process.execPath, ['-e', 'setTimeout(() => {}, 30000)'], { signal: abort.signal });
  setTimeout(() => abort.abort(), 50);
  await assert.rejects(running, { name: 'AbortError' });
  await assert.rejects(runTool(path.join(os.tmpdir(), `missing-${randomUUID()}.exe`), []), { code: 'ENOENT' });
  assert.ok(toolTimeout(4 * 1024 ** 3) > toolTimeout(1024 ** 3));
});
