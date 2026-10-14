import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { link, mkdir, mkdtemp, readFile, readdir, realpath, rename, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createRecordingOptimizer, isStaleTemporary, LIST_LIMIT, MIN_FRAGMENTS, runTool, toolTimeout } from '../recording-optimizer.mjs';
import { mp4Summary } from '../recorder-media.mjs';

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

// Settings lists sessions from disk. Each extra session gets its own game snapshot here.
function listing(f) {
  const vods = new Map(), asked = [], request = f.backend.request;
  f.backend.request = async (route, options) => {
    const game = /^\/api\/vod\?gameId=(\d+)$/.exec(route);
    if (game) asked.push(Number(game[1]));
    if (game && vods.has(Number(game[1]))) return vods.get(Number(game[1]));
    return request(route, options);
  };
  f.backend.vod = { ...f.backend.vod, championName: 'Ahri', enemyChampion: 'Zed', datePlayed: 'Oct 13, 21:40', gameDurationSeconds: 1920 };
  const add = async ({ gameId, bytes = fragmented(MEDIA), session = {}, vod } = {}) => {
    const sessionId = randomUUID(), directory = path.join(f.root, sessionId), file = path.join(directory, 'capture.mp4');
    await mkdir(directory, { recursive: true });
    await writeFile(file, bytes);
    await writeFile(path.join(directory, 'session.json'), JSON.stringify({ sessionId, gameId, state: 'saved', complete: true,
      registrationStatus: 'linked', filePath: file, fileSize: bytes.length,
      registration: { sessionId, gameId, filePath: file, fileSize: bytes.length, durationSeconds: 90, complete: true }, ...session }));
    vods.set(gameId, typeof vod === 'function' ? await vod(file) : vod ?? { hasVod: true, gameId, filePath: file, championName: 'Lux',
      enemyChampion: '', datePlayed: 'Oct 12, 20:15', gameDurationSeconds: 1500 });
    return { sessionId, directory, file, bytes };
  };
  return { add, asked, vods };
}
const GiB = 1024 ** 3;

test('Settings lists each linked recording that still opens slowly, newest first, with what its row shows and no path', async t => {
  assert.equal(MIN_FRAGMENTS, 1000);
  assert.equal(LIST_LIMIT, 50);
  const f = await fixture(t, { session: { startedAt: '2026-10-13T19:40:00.000Z' } });
  const l = listing(f);
  const newer = await l.add({ gameId: 5001, session: { startedAt: '2026-10-14T09:00:00.000Z' } });
  const result = await f.optimizer.list();
  assert.deepEqual(result, { available: true, blocked: null, working: null, items: [
    { gameId: 5001, championName: 'Lux', enemyChampion: '', datePlayed: 'Oct 12, 20:15', durationSeconds: 1500,
      fileSize: newer.bytes.length, requiredBytes: newer.bytes.length + 4 * GiB, ready: true, reason: null },
    { gameId: GAME, championName: 'Ahri', enemyChampion: 'Zed', datePlayed: 'Oct 13, 21:40', durationSeconds: 1920,
      fileSize: f.bytes.length, requiredBytes: f.bytes.length + 4 * GiB, ready: true, reason: null },
  ] });
  const wire = JSON.stringify(result);
  for (const secret of [f.root, f.sessionId, newer.sessionId, 'capture', ':\\']) assert.equal(wire.includes(secret), false, secret);
  assert.deepEqual(f.events, [], 'listing never runs a tool or holds a file');
  assert.equal((await f.optimizer.optimize({ gameId: GAME })).status, 'optimized', 'a listed game is one a speed-up accepts');
  assert.deepEqual((await f.optimizer.list()).items.map(item => item.gameId), [5001]);
});

test('indexed, short, test, pending, unlinked, duplicate and replaced recordings are never listed', async t => {
  const f = await fixture(t, { minFragments: 2 });
  const l = listing(f);
  const oneFragment = Buffer.concat([ftyp(), box('moov', Buffer.alloc(16)), box('moof', Buffer.alloc(24)), box('mdat', MEDIA)]);
  const excluded = [
    { gameId: 6001, bytes: progressive(MEDIA) }, // already indexed (sped up before its session recorded the layout)
    { gameId: 6002, session: { container: 'progressive' } }, // a clean 4.0.1 save: never even asked about
    { gameId: 6003, bytes: oneFragment }, // too short to open slowly
    { gameId: 6004, session: { testCapture: true } },
    { gameId: 6005, session: { state: 'awaiting-match' } },
    { gameId: 6006, session: { registrationStatus: 'retained-existing' } },
    { gameId: 6007, session: { registrationStatus: 'pending' } },
    { gameId: 6008, vod: async () => { // the match now plays an Ascent file
      const outside = path.join(path.dirname(f.root), 'Ascent.mp4'); await writeFile(outside, f.bytes);
      return { hasVod: true, gameId: 6008, filePath: outside }; } },
    { gameId: 6009, vod: { hasVod: false, gameId: 6009, filePath: '' } },
  ];
  for (const value of excluded) await l.add(value);
  const pending = await l.add({ gameId: 6010 });
  f.recording.pending.set(pending.sessionId, {});
  await l.add({ gameId: GAME, session: { startedAt: '2020-01-01T00:00:00.000Z' } }); // an older second session of the same game
  await mkdir(path.join(f.root, 'imports')); // not a session folder
  const result = await f.optimizer.list();
  assert.deepEqual(result.items.map(item => item.gameId), [GAME]);
  for (const gameId of [6002, 6004, 6005, 6006, 6007]) assert.equal(l.asked.includes(gameId), false, `${gameId} is filtered by session.json alone`);
  assert.equal(l.asked.filter(gameId => gameId === GAME).length, 1, 'one row and one snapshot read per game');
  assert.deepEqual(f.events, []);
});

test('a game, a recording or a full drive holds every row and clears on the next check; closing hides the list', async t => {
  let free = 100 * GiB;
  const f = await fixture(t, { space: async () => free });
  f.recording.game = { pid: 7 };
  let result = await f.optimizer.list();
  assert.equal(result.blocked, 'game-running');
  assert.deepEqual(result.items.map(({ ready, reason }) => ({ ready, reason })), [{ ready: false, reason: 'game-running' }]);
  f.recording.game = null; f.recording.current = { sessionId: randomUUID() };
  assert.equal((await f.optimizer.list()).items[0].reason, 'recording-active');
  f.recording.current = null; f.backend.context = { isGameInProgress: true, phase: 'InProgress' };
  assert.equal((await f.optimizer.list()).blocked, 'game-running', 'the sidecar sees a game automatic recording skipped');
  f.backend.context = { isGameInProgress: false, phase: 'None' }; free = 4 * GiB;
  result = await f.optimizer.list();
  assert.deepEqual(result.items.map(({ ready, reason, requiredBytes }) => ({ ready, reason, requiredBytes })),
    [{ ready: false, reason: 'not-enough-space', requiredBytes: f.bytes.length + 4 * GiB }]);
  assert.equal(result.blocked, null);
  free = 100 * GiB;
  assert.deepEqual((await f.optimizer.list()).items.map(item => item.ready), [true]);
  const closed = await fixture(t, { available: () => false });
  assert.deepEqual(await closed.optimizer.list(), { available: false, blocked: null, working: null, items: [] });
  const quitting = await fixture(t);
  await quitting.optimizer.cancel();
  assert.equal((await quitting.optimizer.list()).available, false);
});

test('while a speed-up runs the list shows that game working and the rest waiting, without opening its file', async t => {
  const inspected = [], opened = [];
  const f = await fixture(t, { inspect: async (file, signal) => { inspected.push(file); return mp4Summary(file, signal); },
    readText: async file => { opened.push(file); return readFile(file, 'utf8'); } });
  const l = listing(f);
  await l.add({ gameId: 7001 });
  assert.equal((await f.optimizer.list()).items.length, 2);
  let started, finish;
  const copying = new Promise(resolve => { started = resolve; });
  f.tools.remux = (input, output) => new Promise(resolve => { finish = () => resolve(writeFile(output, progressive(MEDIA))); started(); });
  const job = f.optimizer.optimize({ gameId: GAME });
  await copying;
  inspected.length = 0; opened.length = 0;
  const during = await f.optimizer.list();
  assert.equal(during.working, GAME);
  assert.deepEqual(during.items.map(({ gameId, ready, reason }) => ({ gameId, ready, reason })).sort((a, b) => a.gameId - b.gameId),
    [{ gameId: GAME, ready: false, reason: 'working' }, { gameId: 7001, ready: false, reason: 'busy' }]);
  assert.equal(inspected.includes(f.file), false, 'an open handle on the file being replaced would block its swap');
  assert.equal(opened.includes(f.sessionFile), false, 'nor on the session.json its bookkeeping rewrites');
  finish();
  assert.equal((await job).status, 'optimized');
  const after = await f.optimizer.list();
  assert.equal(after.working, null);
  assert.deepEqual(after.items.map(item => [item.gameId, item.ready]), [[7001, true]], 'a sped-up recording leaves the list');
});

test('held re-checks reuse a check from the last minute, read a layout only after its file changes, and stop at the newest 50', async t => {
  let reads = 0, clock = Date.parse('2026-10-14T12:00:00.000Z');
  const f = await fixture(t, { listLimit: 2, now: () => clock, session: { startedAt: '2019-10-14T11:00:00.000Z' },
    inspect: async (file, signal) => { reads++; return mp4Summary(file, signal); } });
  const l = listing(f);
  const first = await l.add({ gameId: 8001, session: { startedAt: '2026-10-14T10:00:00.000Z' } });
  const second = await l.add({ gameId: 8002, session: { startedAt: '2026-10-14T11:00:00.000Z' } });
  await l.add({ gameId: 8003, session: { startedAt: '2020-10-14T11:00:00.000Z' } });
  assert.deepEqual((await f.optimizer.list()).items.map(item => item.gameId), [8002, 8001]);
  assert.equal(reads, 2);
  assert.deepEqual([...l.asked].sort(), [8001, 8002], 'older sessions past the limit are never asked about');
  // A game in progress: Settings asks every 10 s. Readiness is fresh; held rows keep their check without the sidecar's snapshots.
  f.recording.game = { pid: 9 };
  const held = await f.optimizer.list();
  assert.deepEqual(held.items.map(item => [item.gameId, item.ready, item.reason, item.championName]),
    [[8002, false, 'game-running', 'Lux'], [8001, false, 'game-running', 'Lux']]);
  assert.equal(l.asked.length, 2, 'no snapshot is read again while every row is held');
  assert.equal(reads, 2);
  const session = path.join(second.directory, 'session.json');
  await writeFile(session, JSON.stringify({ ...JSON.parse(await readFile(session, 'utf8')), note: 'changed' }));
  await f.optimizer.list();
  assert.deepEqual(l.asked.slice(2), [8002], 'a changed session.json is checked again at once');
  // Nothing holds the rows: each one that could be offered is checked against its snapshot again.
  f.recording.game = null;
  await f.optimizer.list();
  assert.deepEqual(l.asked.slice(3).sort(), [8001, 8002]);
  assert.equal(reads, 2, 'an unchanged file is not walked again');
  await f.optimizer.status({ gameId: 8001 });
  assert.equal(reads, 3, 'status and a speed-up always read the file afresh');
  assert.equal(l.asked.length, 6, 'and the snapshot');
  f.recording.game = { pid: 9 };
  await f.optimizer.list();
  assert.equal(l.asked.length, 6, 'held again within the minute: the fresh checks are reused');
  clock += 61_000;
  await writeFile(first.file, Buffer.concat([fragmented(MEDIA), box('free')]));
  await f.optimizer.list();
  assert.equal(l.asked.length, 8, 'a minute later even held rows are checked against their snapshots again');
  assert.equal(reads, 4, 'a file that changed size is walked again');
});

test('a row that could be offered is always checked afresh, so a game deleted a moment ago is not offered', async t => {
  let free = 100 * GiB;
  const f = await fixture(t, { space: async () => free });
  const l = listing(f);
  await l.add({ gameId: 8101 });
  const games = async () => (await f.optimizer.list()).items.map(item => item.gameId).sort((a, b) => a - b);
  f.recording.game = { pid: 3 };
  assert.deepEqual(await games(), [GAME, 8101]);
  l.vods.set(8101, { hasVod: false, gameId: 8101 }); // removed from the match library while Settings stays open
  assert.deepEqual(await games(), [GAME, 8101], 'a held row may keep its check from the last minute');
  f.recording.game = null; free = 1 * GiB;
  assert.deepEqual(await games(), [GAME, 8101], 'so may a row the drive has no room for');
  free = 100 * GiB;
  assert.deepEqual(await games(), [GAME], 'a row that could be offered is checked afresh');
});

test('listing never opens the session.json of a recording in progress or awaiting its match, nor an unchanged one twice', async t => {
  const opened = [];
  const f = await fixture(t, { readText: async file => { opened.push(file); return readFile(file, 'utf8'); } });
  const l = listing(f);
  const active = await l.add({ gameId: 9101 }), waiting = await l.add({ gameId: 9102 });
  const metadata = value => path.join(value.directory, 'session.json');
  // The recorder renames a new session.json over these while they run; on Windows any open handle fails that rename.
  f.recording.current = { sessionId: active.sessionId };
  f.recording.pending.set(waiting.sessionId, {});
  assert.deepEqual((await f.optimizer.list()).items.map(item => item.gameId), [GAME]);
  assert.deepEqual(await f.optimizer.status({ gameId: 9101 }), { eligible: false, ready: false, reason: 'not-linked' });
  for (const value of [active, waiting]) assert.equal(opened.includes(metadata(value)), false);
  assert.equal(l.asked.includes(9102), false, 'a pending session is not even asked about');
  // Every 10 s while that recording runs, nothing unchanged is opened again.
  opened.length = 0;
  await f.optimizer.list(); await f.optimizer.list();
  assert.deepEqual(opened, []);
  // Saved and linked, they are read like any other.
  f.recording.current = null; f.recording.pending.clear();
  assert.deepEqual((await f.optimizer.list()).items.map(item => item.gameId).sort((a, b) => a - b), [GAME, 9101, 9102]);
  for (const value of [active, waiting]) assert.equal(opened.includes(metadata(value)), true);
});

test('the 1,000-fragment line holds: about 8 minutes is listed, anything shorter already opens quickly', async t => {
  const fragments = count => Buffer.concat([ftyp(), box('moov', Buffer.alloc(16)),
    ...Array.from({ length: count }, (_, index) => [box('moof', Buffer.alloc(24)), box('mdat', Buffer.from(`packet ${index}`))]).flat()]);
  for (const [count, listed] of [[MIN_FRAGMENTS, true], [MIN_FRAGMENTS - 1, false]]) {
    const f = await fixture(t, { bytes: fragments(count), minFragments: MIN_FRAGMENTS });
    listing(f);
    assert.deepEqual((await f.optimizer.list()).items.map(item => item.gameId), listed ? [GAME] : [], String(count));
    assert.deepEqual(await f.optimizer.status({ gameId: GAME }), listed
      ? { eligible: true, ready: true, reason: null, fileSize: f.bytes.length, freeBytes: 100 * GiB, requiredBytes: f.bytes.length + 4 * GiB }
      : { eligible: false, ready: false, reason: 'opens-quickly' }, String(count));
  }
});
