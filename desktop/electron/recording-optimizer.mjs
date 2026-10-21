import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { lstat, open, readFile, readdir, realpath, rename, stat, statfs, unlink } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { mp4Summary, restoreFragmentedLayout } from './recorder-media.mjs';
import { atomicJson } from './recording-store.mjs';
import { commandRequest } from './routing.mjs';

// "Speed up": a linked built-in recording saved as a fragmented MP4 is rewritten losslessly
// into an indexed MP4 at the same path. The renderer names only a game; the path comes
// from the sidecar's own VOD snapshot, and the original is replaced only after the copy
// is verified packet for packet. Every failure keeps the original untouched.
const GiB = 1024 ** 3;
// Well above the recorder's 2 GiB start reserve: a finished copy (slightly larger than the
// original) plus other programs' writes must never push a new match below it.
const MARGIN = 4 * GiB;
// About 8 minutes at two fragments a second; shorter recordings already open in about a second.
export const MIN_FRAGMENTS = 1000;
// Settings checks at most this many linked sessions, newest first, each against its game's snapshot.
export const LIST_LIMIT = 50;
// Settings asks again every 10 s while a game, a recording, a speed-up or a full drive holds its
// rows. A row that cannot be offered reuses what a full check found for an unchanged session for this long.
const CHECK_TTL = 60_000;
const UUID = '[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const SESSION = new RegExp(`^${UUID}$`, 'i');
const CAPTURE = /^capture[^\\/]*\.mp4$/i;
const STALE = new RegExp(`^capture[^\\\\/]*\\.mp4\\.${UUID}\\.optimizing$`, 'i');
const LINKED = new Set(['linked', 'linked-partial']);
const CODES = new Set(['not-eligible', 'recording-active', 'game-running', 'not-enough-space', 'recording-in-use',
  'verify-failed', 'remux-failed', 'app-closing', 'unavailable', 'busy']);
const GAME_PHASES = new Set(['GameStart', 'InProgress', 'Reconnect']);
const failure = code => Object.assign(new Error(code), { code });
const samePath = (a, b) => typeof a === 'string' && typeof b === 'string' && path.isAbsolute(a)
  && path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
// Each pass reads (and the remux writes) the whole file; generous, so a slow disk never cuts a good copy short.
export const toolTimeout = bytes => 180_000 + Math.ceil(bytes / GiB) * 120_000;
export const isStaleTemporary = name => STALE.test(name);

/** One child process. stdout streams to onStdout; stderr keeps 8 KiB. Settles only after exit. */
export function runTool(tool, args, { signal, timeout, onStdout } = {}) {
  return new Promise((resolve, reject) => {
    let failed = null, stderr = '';
    const child = spawn(tool, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], signal, timeout });
    child.stdout.on('data', chunk => {
      if (failed) return;
      try { onStdout?.(chunk); } catch (error) { failed = error; child.kill(); }
    });
    child.stderr.on('data', chunk => { if (stderr.length < 8192) stderr += chunk.toString('utf8').slice(0, 8192 - stderr.length); });
    child.once('error', error => { failed ??= error; if (child.pid === undefined) reject(failed); });
    child.once('close', (code, killed) => {
      if (!failed && code === 0) resolve({ stderr });
      else reject(failed ?? Object.assign(new Error(`Tool exited with ${code ?? killed}`), { exitCode: code, stderr }));
    });
  });
}

export function createRecordingOptimizer({ recording, backend, media, ffmpegPath, ffprobePath, log = {},
  available = () => true, run = runTool, inspect = mp4Summary, repair = restoreFragmentedLayout, now = () => Date.now(),
  renameFile = rename, pollMs = 1000, minFragments = MIN_FRAGMENTS, listLimit = LIST_LIMIT,
  readText = file => readFile(file, 'utf8'),
  space = async directory => { const fs = await statfs(directory); return fs.bavail * fs.bsize; } }) {
  let job = null, closing = false;
  const summaries = new Map(), checks = new Map(), metadata = new Map();
  // The recorder rewrites an active or pending session's session.json by renaming a new copy over
  // it. On Windows even a read handle makes that rename fail, so such a session is never opened here.
  const recorderOwns = sessionId => recording.current?.sessionId === sessionId || !!recording.pending?.has?.(sessionId);
  const info = message => log.info?.(message), warn = message => log.warn?.(message);
  const layout = async (file, signal) => (await inspect(file, signal))?.layout ?? null;
  const interrupted = () => !available() || closing ? 'app-closing'
    : recording.current ? 'recording-active' : recording.game ? 'game-running' : null;
  // recording.game exists only while automatic recording discovers ranked games. The sidecar
  // sees any League game; an unknown answer never blocks.
  const inGame = async () => {
    try {
      const context = await backend.request('/api/recording/context', { timeoutMs: 2500 });
      return context?.isGameInProgress === true || GAME_PHASES.has(context?.phase);
    } catch { return false; }
  };
  const blocked = async () => interrupted() ?? (await inGame() ? 'game-running' : null);

  // The linked built-in session behind a game's VOD, or { reason } when it is not one. Only the
  // Settings list passes the snapshot it already read and a cached layout; status and a speed-up read afresh.
  async function candidate(gameId, signal, { vod, summarize = inspect } = {}) {
    if (!Number.isSafeInteger(gameId) || gameId <= 0) return { reason: 'not-eligible' };
    if (vod === undefined) {
      const request = commandRequest('get_vod', { gameId });
      vod = await backend.request(request.route, { timeoutMs: request.timeoutMs });
    }
    if (!vod?.hasVod || Number(vod.gameId) !== gameId || typeof vod.filePath !== 'string' || !path.isAbsolute(vod.filePath))
      return { reason: 'no-recording' };
    let root, file;
    try { root = await realpath(recording.root); file = await realpath(vod.filePath); } catch { return { reason: 'no-recording' }; }
    const relative = path.relative(root, file), [sessionId, name, ...rest] = relative.split(path.sep);
    if (!samePath(file, vod.filePath) || path.isAbsolute(relative) || rest.length || !SESSION.test(sessionId) || !CAPTURE.test(name ?? ''))
      return { reason: 'not-built-in' };
    const directory = path.join(root, sessionId), sessionFile = path.join(directory, 'session.json');
    const video = await lstat(file);
    if (!video.isFile() || video.nlink !== 1 || !Number.isSafeInteger(video.size) || video.size < 24) return { reason: 'not-built-in' };
    if (recorderOwns(sessionId)) return { reason: 'not-linked' };
    let session;
    try {
      const stats = await lstat(sessionFile);
      if (!stats.isFile() || stats.size > 64 * 1024) return { reason: 'not-built-in' };
      session = JSON.parse(await readText(sessionFile));
    } catch { return { reason: 'not-built-in' }; }
    if (session?.sessionId !== sessionId || session.testCapture === true || !['saved', 'partial'].includes(session.state)
        || !LINKED.has(session.registrationStatus) || session.registration?.sessionId !== sessionId
        || session.registration.gameId !== gameId || !samePath(session.registration.filePath, file)
        || recorderOwns(sessionId)) return { reason: 'not-linked' };
    const found = { file, directory, sessionId, sessionFile, fileSize: video.size };
    const summary = await summarize(file, signal, video);
    if (summary?.layout === 'progressive') return { ...found, reason: 'already-optimized' };
    if (summary?.layout !== 'fragmented') return { reason: 'not-eligible' };
    return summary.fragments < minFragments ? { reason: 'opens-quickly' } : found;
  }

  async function status({ gameId } = {}) {
    if (!available() || closing) return { eligible: false, ready: false, reason: 'unavailable' };
    // A page opened while this game's own copy runs shows that work, not another job.
    if (job && job.gameId === gameId) return { eligible: true, ready: false, reason: 'working' };
    let found;
    try { found = await candidate(gameId); } catch { return { eligible: false, ready: false, reason: 'unavailable' }; }
    if (found.reason) return { eligible: false, ready: false, reason: found.reason };
    const freeBytes = await space(found.directory).catch(() => null), requiredBytes = found.fileSize + MARGIN;
    const reason = job ? 'busy' : await blocked() ?? (freeBytes === null ? 'unavailable' : freeBytes < requiredBytes ? 'not-enough-space' : null);
    return { eligible: true, ready: !reason, reason, fileSize: found.fileSize, freeBytes, requiredBytes };
  }

  // While Settings waits it lists the same files every few seconds. A file's layout changes only
  // with its size or write time, so the list reads each layout once; a speed-up never uses this.
  async function cachedSummary(file, signal, video) {
    const key = `${file}|${video.size}|${video.mtimeMs}`;
    if (!summaries.has(key)) {
      const value = await inspect(file, signal);
      if (summaries.size >= 256) summaries.delete(summaries.keys().next().value);
      summaries.set(key, value);
    }
    return summaries.get(key);
  }

  // Linked built-in sessions that may still be fragmented, newest first, one per game. session.json
  // only narrows the search; candidate() decides from the game's own snapshot, as a speed-up does.
  // A session.json is read again only after its size or write time changes. The running speed-up's
  // own session keeps what was read before it started: its bookkeeping rewrites that file.
  async function linkedSessions(root, working) {
    let entries;
    try { entries = await readdir(root, { withFileTypes: true }); } catch { return []; }
    const found = [], present = new Set();
    for (const entry of entries.filter(value => value.isDirectory() && SESSION.test(value.name)).slice(0, 1000)) {
      if (recorderOwns(entry.name)) continue;
      const directory = path.join(root, entry.name), sessionFile = path.join(directory, 'session.json');
      present.add(entry.name);
      try {
        if ((await realpath(directory)).toLowerCase() !== directory.toLowerCase()) continue;
        const stats = await lstat(sessionFile);
        if (!stats.isFile() || stats.size > 64 * 1024) continue;
        const key = `${entry.name}|${stats.size}|${stats.mtimeMs}`, known = metadata.get(entry.name);
        let session = known && (known.key === key || entry.name === job?.sessionId) ? known.session : undefined;
        if (session === undefined) {
          if (recorderOwns(entry.name)) continue;
          const value = JSON.parse(await readText(sessionFile)), gameId = value?.registration?.gameId;
          session = value?.sessionId === entry.name && value.testCapture !== true && ['saved', 'partial'].includes(value.state)
            && LINKED.has(value.registrationStatus) && Number.isSafeInteger(gameId) && gameId > 0
            ? { gameId, progressive: value.container === 'progressive', started: Date.parse(value.startedAt), fileSize: value.fileSize }
            : null;
          metadata.set(entry.name, { key, session });
        }
        if (!session || (session.progressive && session.gameId !== working)) continue;
        found.push({ gameId: session.gameId, key: metadata.get(entry.name).key, fileSize: session.fileSize,
          started: Number.isFinite(session.started) ? session.started : stats.mtimeMs });
      } catch { /* unreadable metadata is never a candidate */ }
    }
    for (const name of metadata.keys()) if (!present.has(name)) metadata.delete(name);
    const seen = new Set();
    return found.sort((a, b) => b.started - a.started).filter(value => !seen.has(value.gameId) && seen.add(value.gameId))
      .slice(0, listLimit);
  }

  // One session's row, or null when its game is not one a speed-up would accept. A full check reads
  // the game's snapshot and runs candidate(). A row that cannot be offered right now (held, or too
  // big for the drive) reuses an unchanged session's check for CHECK_TTL; a row that could be offered
  // is always checked afresh. The running copy's file is never opened here: a handle would block its swap.
  async function describe({ gameId, key, fileSize }, working, offerable) {
    const cached = checks.get(gameId);
    if (cached?.key === key && now() - cached.at < CHECK_TTL && !offerable(cached.item)) return cached.item;
    const request = commandRequest('get_vod', { gameId });
    const vod = await backend.request(request.route, { timeoutMs: request.timeoutMs });
    const text = value => typeof value === 'string' ? value.trim().slice(0, 120) : '';
    let item = null;
    if (vod?.hasVod && Number(vod.gameId) === gameId) {
      const found = gameId === working ? { fileSize: Number.isSafeInteger(fileSize) ? fileSize : 0 }
        : await candidate(gameId, undefined, { vod, summarize: cachedSummary });
      if (!found.reason) item = { gameId, championName: text(vod.championName), enemyChampion: text(vod.enemyChampion),
        datePlayed: text(vod.datePlayed), durationSeconds: Math.max(0, Math.round(Number(vod.gameDurationSeconds) || 0)),
        fileSize: found.fileSize };
    }
    if (gameId !== working) checks.set(gameId, { key, at: now(), item });
    return item;
  }

  // Settings: every linked built-in recording that still opens slowly, with what its row shows.
  // The game id is the only handle; a path never leaves the host. Readiness is always fresh.
  async function list() {
    const none = { available: false, blocked: null, working: null, items: [] };
    if (!available() || closing) return none;
    let root;
    try { root = await realpath(recording.root); } catch { return { ...none, available: true }; }
    const working = job?.gameId ?? null, sessions = await linkedSessions(root, working);
    const listed = new Set(sessions.map(value => value.gameId));
    for (const gameId of checks.keys()) if (!listed.has(gameId)) checks.delete(gameId);
    const reason = await blocked(), freeBytes = await space(root).catch(() => null);
    const offerable = item => working === null && !reason && freeBytes !== null && (!item || freeBytes >= item.fileSize + MARGIN);
    const items = new Array(sessions.length);
    let next = 0;
    const check = async () => {
      while (next < sessions.length) {
        const index = next++;
        try {
          const item = await describe(sessions[index], working, offerable);
          if (!item) continue;
          const requiredBytes = item.fileSize + MARGIN;
          const why = item.gameId === working ? 'working' : working !== null ? 'busy' : reason
            ?? (freeBytes === null ? 'unavailable' : freeBytes < requiredBytes ? 'not-enough-space' : null);
          items[index] = { ...item, requiredBytes, ready: !why, reason: why };
        } catch { /* a game whose snapshot or file cannot be read right now is left out of this answer */ }
      }
    };
    await Promise.all(Array.from({ length: 4 }, check));
    return { available: true, blocked: reason, working, items: items.filter(Boolean) };
  }

  // Remove only this feature's abandoned copies: <capture*.mp4>.<uuid>.optimizing regular files.
  async function sweep(directory) {
    let entries;
    try { entries = await readdir(directory, { withFileTypes: true }); } catch { return []; }
    for (const entry of entries.filter(value => value.isFile() && STALE.test(value.name)).slice(0, 100)) {
      try { await unlink(path.join(directory, entry.name)); info(`Removed an unfinished speed-up copy in ${path.basename(directory)}`); }
      catch (error) { warn(`An unfinished speed-up copy could not be removed (${error.code || 'error'})`); }
    }
    return entries;
  }
  // Startup also restores a finished capture whose index never reached the disk (a power cut
  // right after a match, a full disk). The repair touches only that exact header signature.
  async function init() {
    if (!available() || job) return;
    let root, entries;
    try { root = await realpath(recording.root); entries = await readdir(root, { withFileTypes: true }); } catch { return; }
    for (const entry of entries.filter(value => value.isDirectory() && SESSION.test(value.name)).slice(0, 1000)) {
      const directory = path.join(root, entry.name);
      try { if ((await realpath(directory)).toLowerCase() !== directory.toLowerCase()) continue; } catch { continue; }
      for (const capture of (await sweep(directory)).filter(value => value.isFile() && CAPTURE.test(value.name)).slice(0, 16)) {
        try { if (await repair(path.join(directory, capture.name))) info(`Restored the playable layout of recording ${entry.name}`); }
        catch (error) { warn(`Recording ${entry.name} could not be checked for a lost index (${error.code || 'error'})`); }
      }
    }
  }

  async function verify(original, copy, signal, timeout) {
    if (await layout(copy, signal).catch(() => null) !== 'progressive') throw failure('verify-failed');
    // Siblings stop as soon as one pass fails; settle only after both processes exit.
    const inner = new AbortController(), linked = AbortSignal.any([signal, inner.signal]);
    const both = async tasks => {
      const results = await Promise.allSettled(tasks.map(task => task().catch(error => { inner.abort(); throw error; })));
      const rejected = results.find(result => result.status === 'rejected');
      if (rejected) throw signal.aborted ? signal.reason : failure('verify-failed');
      return results.map(result => result.value);
    };
    // framemd5 lists every packet's stream, dts, pts, duration, size and payload hash.
    const packets = file => async () => {
      const hash = createHash('sha256');
      await run(ffmpegPath, ['-hide_banner', '-nostdin', '-v', 'error', '-i', file, '-map', '0', '-c', 'copy', '-f', 'framemd5', '-'],
        { signal: linked, timeout, onStdout: chunk => hash.update(chunk) });
      return hash.digest('hex');
    };
    const probe = file => async () => {
      const chunks = []; let bytes = 0;
      await run(ffprobePath, ['-v', 'error', '-show_entries', 'format=duration:stream=index,codec_type,codec_name', '-of', 'json', file],
        { signal: linked, timeout: 30_000, onStdout: chunk => {
          bytes += chunk.length; if (bytes > 65_536) throw failure('verify-failed'); chunks.push(chunk);
        } });
      return JSON.parse(Buffer.concat(chunks).toString('utf8'));
    };
    const [before, after] = await both([packets(original), packets(copy)]);
    const probes = await both([probe(original), probe(copy)]);
    const shape = value => Array.isArray(value?.streams) && value.streams.length
      ? value.streams.map(stream => `${stream.index}:${stream.codec_type}:${stream.codec_name}`).join() : null;
    const [first, second] = probes.map(value => Number(value?.format?.duration));
    if (before !== after || !shape(probes[0]) || shape(probes[0]) !== shape(probes[1]) || !Number.isFinite(first)
        || !Number.isFinite(second) || Math.abs(first - second) > Math.max(0.5, first * 0.001)) throw failure('verify-failed');
  }

  // NTFS replaces atomically. A reader without delete sharing (a clip job, the sidecar) blocks it.
  async function swap(copy, file) {
    for (let attempt = 1; ; attempt++) {
      try { await renameFile(copy, file); return; }
      catch (error) {
        if (!['EBUSY', 'EPERM', 'EACCES'].includes(error.code)) throw failure('remux-failed');
        if (attempt >= 3) throw failure('recording-in-use');
        await delay(250);
      }
    }
  }

  async function bookkeeping({ sessionId, sessionFile }, fileSize) {
    let registered = false;
    try {
      const result = await backend.request('/api/recording/relayout', { method: 'POST', body: { sessionId, fileSize }, timeoutMs: 10_000 });
      registered = result?.ok === true;
    } catch (error) { warn(`The sped-up recording ${sessionId} plays, but its match record keeps the old size (${error.status ?? 'unavailable'})`); }
    try {
      const session = JSON.parse(await readText(sessionFile));
      if (session?.sessionId !== sessionId) throw new Error('Recording metadata changed');
      Object.assign(session, { fileSize, container: 'progressive', optimizedAt: new Date(now()).toISOString() });
      // The registration mirrors the sidecar receipt; it changes only when the receipt did.
      if (registered && session.registration) session.registration.fileSize = fileSize;
      await atomicJson(sessionFile, session);
    } catch { warn(`The sped-up recording ${sessionId} plays, but its session file keeps the old size`); }
  }

  async function perform(gameId, signal) {
    const started = now();
    let found;
    try { found = await candidate(gameId, signal); } catch { return { ok: false, code: 'not-eligible' }; }
    if (found.reason === 'already-optimized') return { ok: true, status: 'already-optimized', fileSize: found.fileSize };
    if (found.reason) return { ok: false, code: 'not-eligible' };
    await sweep(found.directory);
    if (await space(found.directory).catch(() => 0) < found.fileSize + MARGIN) return { ok: false, code: 'not-enough-space' };
    const reason = () => CODES.has(signal.reason?.code) ? signal.reason.code : 'app-closing';
    if (signal.aborted) return { ok: false, code: reason() };
    const { file, fileSize } = found, copy = `${file}.${randomUUID()}.optimizing`, timeout = toolTimeout(fileSize);
    job.copy = copy; job.sessionId = found.sessionId;
    // No page streams the file while it is copied and replaced; a reopened page shows the work instead.
    const release = await media.revoke(file);
    // A League game, a new recording or closing Revu stops the work: the disk belongs to capture.
    const local = new AbortController(), stop = code => { if (!local.signal.aborted) local.abort(failure(code)); };
    const forward = () => stop(reason());
    signal.addEventListener('abort', forward, { once: true });
    if (signal.aborted) forward();
    let checking = false;
    const watch = setInterval(() => {
      if (checking || local.signal.aborted) return;
      checking = true;
      void blocked().then(code => { if (code) stop(code); }).finally(() => { checking = false; });
    }, pollMs);
    let remuxed, verified;
    try {
      try {
        await run(ffmpegPath, ['-hide_banner', '-nostdin', '-v', 'error', '-i', file, '-map', '0', '-c', 'copy',
          '-avoid_negative_ts', 'disabled', '-f', 'mp4', copy], { signal: local.signal, timeout });
        const handle = await open(copy, 'r+');
        try { await handle.sync(); } finally { await handle.close(); } // durable before it can replace the original
      } catch { throw local.signal.aborted ? local.signal.reason : failure('remux-failed'); }
      remuxed = now();
      await verify(file, copy, local.signal, timeout);
      verified = now();
      const code = await blocked();
      if (code || local.signal.aborted) throw local.signal.reason ?? failure(code);
      await swap(copy, file);
    } catch (error) {
      await unlink(copy).catch(() => {});
      // Whatever was running when a game, a recording or quitting stopped it, report that reason.
      const code = local.signal.aborted ? local.signal.reason.code : CODES.has(error?.code) ? error.code : 'remux-failed';
      warn(`Speeding up ${found.sessionId} stopped (${code}); the original recording was kept`);
      return { ok: false, code };
    } finally { clearInterval(watch); signal.removeEventListener('abort', forward); release(); }
    const optimized = await stat(file).then(value => value.size, () => null);
    if (optimized !== null) await bookkeeping(found, optimized);
    info(`Sped up recording ${found.sessionId}: ${fileSize} to ${optimized} bytes; remux ${remuxed - started} ms, `
      + `verify ${verified - remuxed} ms, total ${now() - started} ms`);
    return { ok: true, status: 'optimized', fileSize: optimized, previousFileSize: fileSize };
  }

  async function optimize({ gameId } = {}) {
    if (!available() || closing) return { ok: false, code: 'unavailable' };
    if (job) return { ok: false, code: 'busy' };
    let finish;
    job = { gameId, copy: null, sessionId: null, abort: new AbortController(), done: new Promise(resolve => { finish = resolve; }) };
    try {
      const code = await blocked();
      return code ? { ok: false, code } : await perform(gameId, job.abort.signal);
    } finally { job = null; checks.delete(gameId); finish(); }
  }

  // Quit stops a running copy at once and waits only for its cleanup.
  function cancel() { closing = true; job?.abort.abort(failure('app-closing')); return job?.done ?? Promise.resolve(); }

  // The recorder measures space only to start or keep a recording. A copy stops at once, and
  // its bytes, deleted as it stops, count as free: a speed-up never costs a match its recording.
  async function recordingSpace(directory) {
    const free = await space(directory), copy = job?.copy;
    job?.abort.abort(failure('recording-active'));
    return free + (copy ? await stat(copy).then(value => value.size, () => 0) : 0);
  }

  return { status, list, optimize, init, cancel, recordingSpace, get running() { return !!job; } };
}
