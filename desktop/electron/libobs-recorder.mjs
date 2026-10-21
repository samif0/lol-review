import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { realpath, stat, readdir } from 'node:fs/promises';
import path from 'node:path';
import { RECORDING_PRESETS, checkRecordingAbort, recorderError } from './recorder-policy.mjs';
import { validateRecorderMedia, runRecorderTool } from './recorder-media.mjs';

const execute = promisify(execFile);
const hardware = new Set(['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2']);
const transientStartup = new Set(['source-window-not-ready', 'recording-cancelled', 'game-process-unavailable',
  'game-process-invalid', 'recording-context-unavailable']);
// Capture that never became active before a recording started costs only that match:
// League minimized or still settling while the sources started. Repeated on this many
// matches in a row, it is a lasting fault and turns recording off.
const CAPTURE_START_MISS_LIMIT = 3;
// Hardware answers and protocol violations are deterministic. A probe that timed
// out, could not launch or exited early can succeed on a later attempt.
const definitiveProbe = new Set(['hardware-encoder-unavailable', 'capture-unavailable', 'wgc-unavailable',
  'recorder-protocol-invalid', 'recording-tools-unavailable', 'recorder-options-invalid', 'recorder-disposed', 'ENOENT']);
export const retryableProbeFailure = code => !definitiveProbe.has(code);
const validPid = value => Number.isSafeInteger(value) && value > 0;
const finite = value => typeof value === 'number' && Number.isFinite(value);
// Revu always records game first: the helper's CPU and GPU priority stay below League's.
// There is no user setting; the helper must confirm this priority when it starts.
const GAME_FIRST = 'below-normal';
const gpuClasses = new Set(['idle', 'below-normal', 'normal', 'above-normal', 'high', 'realtime', 'unknown']);
// Window capture pacing the helper applied, in hundred-nanosecond units; null where Windows keeps
// its own (an older helper omits it). It only labels the recording, so a bad value never fails one.
const captureInterval = value => Number.isSafeInteger(value) && value > 0 && value <= 10_000_000 ? value : null;
const minuteSeries = value => Array.isArray(value) && value.length > 0 && value.length <= 300
  && value.every(count => Number.isSafeInteger(count) && count >= 0 && count <= 1_000_000);
// Per movie minute: encoded frames, frames repeated by a capture hold, and render ticks the
// helper missed. A malformed series is dropped; the recording itself is unaffected.
function frameStats(stats) {
  if (!stats || typeof stats !== 'object') return null;
  const { perMinute, ...totals } = stats, series = ['frames', 'repeated', 'lagged'];
  return series.every(key => minuteSeries(perMinute?.[key]) && perMinute[key].length === perMinute.frames.length)
    ? { ...totals, perMinute: Object.fromEntries(series.map(key => [key, [...perMinute[key]]])) } : totals;
}
export const rankedRecordingContext = context => context?.queueId === 420
  && ['GameStart', 'InProgress', 'Reconnect'].includes(context.phase);
export const recordingTestContext = context => validPid(context?.gameId)
  && ['GameStart', 'InProgress', 'Reconnect'].includes(context.phase);

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  // A native exit can reject before the caller reaches its next await.
  void promise.catch(() => {});
  return { promise, resolve, reject };
}
async function deadline(operation, milliseconds, code, signal) {
  checkRecordingAbort(signal);
  let timer, abort;
  try {
    return await Promise.race([operation, new Promise((_, reject) => {
      timer = setTimeout(() => reject(recorderError(code)), milliseconds);
      abort = () => reject(recorderError('recording-aborted'));
      signal?.addEventListener('abort', abort, { once: true });
    })]);
  } finally { clearTimeout(timer); signal?.removeEventListener('abort', abort); }
}

/** A separate, pinned libobs process owns all graphics/audio code. No DLL is
 * loaded into Electron or the sidecar. Helper arguments and media paths are
 * host-owned; the renderer never supplies a process, source or encoder.
 */
export function createLibobsRecorder({ helperPath, ffmpegPath, ffprobePath,
  onGame = () => {}, onUnavailable = () => {}, onDetectionIssue = () => {},
  shouldDiscover = () => false, getContext = async () => null,
  recordingTest = false,
  spawnHelper = spawn, queryHelper = execute, runTool = runRecorderTool,
  pollMs = 2000, deadlines = {}, log = () => {} } = {}) {
  if (![helperPath, ffmpegPath, ffprobePath].every(value => typeof value === 'string' && path.isAbsolute(value)))
    throw recorderError('recording-tools-unavailable');
  // The helper drains for at most 6 s after a stop request; `stop` bounds the wait
  // for its final result with margin. After a result, process exit is teardown only.
  const limits = { probe: 25_000, query: 10_000, start: 30_000, stop: 8000, teardown: 3000, heartbeat: 7000, ...deadlines };
  if (!finite(pollMs) || pollMs < 1 || Object.values(limits).some(value => !finite(value) || value < 1 || value > 120_000))
    throw recorderError('recorder-options-invalid');
  const cwd = path.dirname(helperPath);
  const testMode = recordingTest === true;
  const supportedContext = testMode ? recordingTestContext : rankedRecordingContext;
  const knownContext = context => typeof context?.phase === 'string'
    && (testMode ? validPid(context.gameId) : Number.isInteger(context.queueId));
  const lifetime = new AbortController(), contexts = new WeakMap();
  let initialized = false, disposed = false, polling = false, timer, session, identified, capabilities, captureMisses = 0;
  const copyCapabilities = () => capabilities ? { ...capabilities, encoders: [...capabilities.encoders] } : null;
  const notify = (callback, value) => {
    try { void Promise.resolve(callback(value)).catch(() => {}); } catch { /* The host owns consumer errors. */ }
  };
  const query = async (args, signal = lifetime.signal) => {
    checkRecordingAbort(signal);
    const { stdout } = await queryHelper(helperPath, args, { cwd, signal, timeout: args[0] === '--probe' ? limits.probe : limits.query,
      windowsHide: true, maxBuffer: 128 * 1024, encoding: 'utf8' });
    checkRecordingAbort(signal);
    const lines = stdout.trim().split(/\r?\n/);
    if (lines.length !== 1 || lines[0].length > 64 * 1024) throw recorderError('recorder-protocol-invalid');
    return JSON.parse(lines[0]);
  };
  const identify = game => {
    const next = game?.pid;
    if (next === identified) return;
    const previous = identified; identified = next;
    if (previous !== undefined) notify(onGame, { pid: previous, running: false });
    if (next !== undefined) notify(onGame, { ...game, running: true });
  };
  const poll = async () => {
    // A running recording watches its own game process and window. Launching discovery
    // helpers on every poll would only add work while the match is being played.
    if (disposed || !initialized || polling || !shouldDiscover() || (session && !session.exited)) return;
    polling = true;
    try {
      const context = await getContext();
      if (disposed) return;
      // Once recording, a stale/failed LCU observation is unknown, not an exit.
      // Test scope requires explicit launch authorization and a current game.
      // Ordinary launches enumerate League processes only during ranked games.
      if (!session && !supportedContext(context)) return;
      const value = await query(['--discover']);
      if (disposed) return;
      if (value?.type !== 'games' || !Array.isArray(value.games) || value.games.length > 16
          || value.games.some(game => !validPid(game?.pid))) throw recorderError('recorder-protocol-invalid');
      if (value.games.length > 1) { notify(onDetectionIssue, 'game-process-ambiguous'); return; }
      identify(value.games[0]);
    } catch (error) {
      if (!disposed) notify(onDetectionIssue, error.code ?? 'game-process-inspection-unavailable');
    } finally { polling = false; }
  };
  const finish = (owned, raw) => {
    if (owned.completed) return;
    owned.completed = true; clearInterval(owned.watchdog);
    owned.signal?.removeEventListener('abort', owned.abort);
    owned.child.stdin?.end();
    const valid = raw?.hasError === false && raw.filePath === owned.filePath
      && finite(raw.duration) && raw.duration > 0 && raw.splitCount === 0;
    owned.cleanStop = valid;
    const result = { ...raw, ...(raw?.stats ? { stats: frameStats(raw.stats) } : {}), hasError: !valid, filePath: owned.filePath,
      duration: finite(raw?.duration) ? raw.duration : 0, splitCount: 0,
      ...(owned.startedAtVerified ? { startTimeEpoch: owned.startTimeEpoch } : { startTimeEpoch: undefined }),
      ...(!valid ? { error: 'recording-provider-error' } : {}),
      requestToCallbackMs: owned.stopRequestedAt === undefined ? null : performance.now() - owned.stopRequestedAt };
    contexts.set(result, owned);
    owned.stopped.resolve(result);
    if (!owned.startedReceived) owned.started.reject(recorderError(raw?.code ?? 'recorder-start-failed'));
    notify(owned.onStopped, result);
  };
  const terminate = owned => {
    if (!owned.exited) { try { owned.child.kill(); } catch { /* The close event is still authoritative. */ } }
  };
  const stopOwned = async owned => {
    if (!owned || owned.exited) return;
    if (owned.stopOperation) return owned.stopOperation;
    owned.stopRequestedAt ??= performance.now();
    owned.stopOperation = (async () => {
      if (!owned.completed && owned.child.stdin?.writable)
        owned.child.stdin.write(`${JSON.stringify({ command: 'stop' })}\n`);
      try {
        // The final stopped/error line confirms the stop; process exit is not required for it.
        await deadline(owned.stopped.promise, limits.stop, 'recorder-stop-failed');
      } catch (error) { terminate(owned); notify(onUnavailable, 'recorder-stop-failed'); throw error; }
      try { await deadline(owned.closed.promise, limits.teardown, 'recorder-teardown-slow'); }
      catch {
        // A slow exit after the final result is teardown, not a recording fault.
        owned.teardownKilled = true; log('Recorder teardown was slow; ending the helper after its final result.\n');
        terminate(owned);
        await deadline(owned.closed.promise, limits.teardown, 'recorder-teardown-slow').catch(() => {});
      }
    })();
    return owned.stopOperation;
  };
  return {
    async initialize() {
      if (disposed) throw recorderError('recorder-disposed');
      if (initialized) return copyCapabilities();
      const value = await query(['--probe']);
      if (disposed) throw recorderError('recorder-disposed');
      if (value?.type !== 'capabilities' || value.protocol !== 1 || typeof value.available !== 'boolean'
          || !Array.isArray(value.encoders) || value.encoders.length > hardware.size
          || value.encoders.some(name => !hardware.has(name)) || new Set(value.encoders).size !== value.encoders.length
          || (value.available ? !value.encoders.includes(value.selectedEncoder) : value.selectedEncoder !== null))
        throw recorderError('recorder-protocol-invalid');
      if (!value.available) throw recorderError(value.code ?? 'hardware-encoder-unavailable');
      capabilities = { encoders: [...value.encoders], selectedEncoder: value.selectedEncoder };
      initialized = true;
      timer = setInterval(() => { void poll(); }, pollMs); timer.unref();
      void poll();
      return copyCapabilities();
    },
    async prepareStart({ pid } = {}) {
      let context;
      try { context = await getContext(); }
      catch { throw recorderError('recording-context-unavailable'); }
      if (!knownContext(context))
        throw recorderError('recording-context-unavailable');
      if (!supportedContext(context)) throw recorderError('recording-queue-unsupported');
      let value;
      // A slow, failed or truncated inspection run says nothing about this game; it can be retried.
      try { value = await query(['--discover']); }
      catch (error) {
        if (error?.code === 'recording-aborted') throw error;
        throw recorderError('game-process-inspection-unavailable');
      }
      if (value?.type !== 'games' || !Array.isArray(value.games)) throw recorderError('recorder-protocol-invalid');
      if (value.games.length > 1) throw recorderError('game-process-ambiguous');
      const game = value.games.find(value => value.pid === pid);
      if (!game || ![game.width, game.height].every(number => Number.isInteger(number) && number >= 64))
        throw recorderError('source-window-not-ready');
    },
    async start({ pid, preset = '720p30', outputDirectory, signal, onStopped = () => {} } = {}) {
      checkRecordingAbort(signal);
      if (disposed || !initialized) throw recorderError('recorder-unavailable');
      if (session && !session.exited) throw recorderError('recorder-busy');
      if (!validPid(pid)) throw recorderError('game-process-invalid');
      if (!Object.hasOwn(RECORDING_PRESETS, preset)) throw recorderError('recording-preset-invalid');
      if (typeof outputDirectory !== 'string' || !path.isAbsolute(outputDirectory)) throw recorderError('recording-path-invalid');
      let context;
      try { context = await getContext(); } catch { throw recorderError('recording-context-unavailable'); }
      if (!knownContext(context)) throw recorderError('recording-context-unavailable');
      if (!supportedContext(context)) throw recorderError('recording-queue-unsupported');
      checkRecordingAbort(signal);
      const root = await realpath(outputDirectory);
      if (root.toLowerCase() !== path.resolve(outputDirectory).toLowerCase() || !(await stat(root)).isDirectory()
          || (await readdir(root)).some(name => /^capture.*\.mp4$/i.test(name))) throw recorderError('recording-path-invalid');
      checkRecordingAbort(signal);
      if (disposed || !initialized) throw recorderError('recorder-unavailable');
      if (session && !session.exited) throw recorderError('recorder-busy');
      const filePath = path.join(root, 'capture.mp4');
      const child = spawnHelper(helperPath, ['--record', '--pid', String(pid), '--output', filePath, '--preset', preset,
        '--priority', GAME_FIRST],
        { cwd, windowsHide: true, detached: false, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
      const owned = session = { child, root, filePath, pid, signal, onStopped,
        started: deferred(), stopped: deferred(), closed: deferred(), lastHeartbeat: performance.now(), completed: false, exited: false };
      let buffer = '', stderrBytes = 0;
      owned.abort = () => { void stopOwned(owned).catch(() => {}); };
      signal?.addEventListener('abort', owned.abort, { once: true });
      child.stdin.on('error', () => {});
      child.stderr.on('data', chunk => {
        // Native logs never contain stdout protocol data and remain bounded.
        if (stderrBytes < 64 * 1024) { stderrBytes += chunk.length; log(chunk.toString('utf8').slice(0, 2048)); }
      });
      const protocolFailure = () => {
        owned.started.reject(recorderError('recorder-protocol-invalid'));
        terminate(owned); notify(onUnavailable, 'recorder-protocol-invalid');
      };
      child.stdout.setEncoding('utf8');
      child.stdout.on('data', chunk => {
        if (owned.completed) return;
        buffer += chunk;
        if (buffer.length > 64 * 1024) { protocolFailure(); return; }
        let at;
        while ((at = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, at).trim(); buffer = buffer.slice(at + 1);
          if (!line) continue;
          let value;
          try { value = JSON.parse(line); } catch { protocolFailure(); return; }
          if (value?.type === 'heartbeat') owned.lastHeartbeat = performance.now();
          else if (value?.type === 'started' && !owned.startedReceived) {
            if (value.encoder !== capabilities.selectedEncoder || ![value.width, value.height, value.fps].every(number => Number.isInteger(number) && number > 0)
                || value.fps !== RECORDING_PRESETS[preset].fps || value.priority !== GAME_FIRST
                || value.width > RECORDING_PRESETS[preset].width || value.height > RECORDING_PRESETS[preset].height) {
              protocolFailure(); return;
            }
            owned.video = { outputWidth: value.width, outputHeight: value.height, fps: value.fps };
            const epoch = Date.parse(value.startedAt);
            owned.startedAtVerified = value.startedAtVerified === true && Number.isFinite(epoch)
              && epoch <= Date.now() + 2000 && epoch >= Date.now() - limits.start - 5000;
            owned.startTimeEpoch = owned.startedAtVerified ? epoch : undefined;
            owned.startedReceived = true; owned.lastHeartbeat = performance.now(); captureMisses = 0; owned.started.resolve(value);
            // Heartbeat means the helper event loop is alive, not that a
            // minimized window must keep yielding fresh captured frames.
            owned.watchdog = setInterval(() => {
              if (!owned.completed && performance.now() - owned.lastHeartbeat > limits.heartbeat) {
                terminate(owned); notify(onUnavailable, 'recorder-heartbeat-lost');
              }
            }, Math.min(1000, limits.heartbeat)); owned.watchdog.unref();
          } else if (value?.type === 'stopped') finish(owned, value);
          else if (value?.type === 'error') {
            owned.errorCode = value.code;
            owned.started.reject(recorderError(value.code ?? 'recorder-start-failed'));
            finish(owned, { hasError: true, code: value.code, duration: 0 });
          } else { protocolFailure(); return; }
        }
      });
      child.on('error', () => { finish(owned, { hasError: true, duration: 0 }); });
      child.on('close', code => {
        owned.exited = true; owned.closed.resolve();
        if (!owned.completed) { finish(owned, { hasError: true, duration: 0 }); notify(onUnavailable, 'recorder-process-exited'); }
        // After a clean stopped result, or once Revu ended a slow exit, a non-zero
        // code reflects teardown only. Failed starts and crashes still disable recording.
        else if (code !== 0 && (owned.cleanStop || (owned.teardownKilled && owned.startedReceived)))
          log(`Recorder teardown ended with exit code ${code} after its final result.\n`);
        else if (code !== 0 && !owned.startedReceived && owned.errorCode === 'capture-unavailable') {
          if (++captureMisses >= CAPTURE_START_MISS_LIMIT) notify(onUnavailable, 'capture-unavailable');
          else log(`League capture did not become active before recording started (${captureMisses} in a row); this match is skipped.\n`);
        } else if (code !== 0 && (owned.startedReceived || !transientStartup.has(owned.errorCode)))
          notify(onUnavailable, 'recorder-process-exited');
        if (session === owned) session = undefined;
      });
      try {
        const started = await deadline(owned.started.promise, limits.start, 'recorder-start-failed', signal);
        checkRecordingAbort(signal);
        return { encoder: started.encoder, width: started.width, height: started.height, fps: started.fps,
          bitrate: started.bitrate, sourceWidth: started.sourceWidth, sourceHeight: started.sourceHeight,
          priority: started.priority, gpuPriority: gpuClasses.has(started.gpuPriority) ? started.gpuPriority : 'unknown',
          captureMinIntervalHns: captureInterval(started.captureMinIntervalHns) };
      } catch (error) { await stopOwned(owned).catch(() => {}); throw error; }
    },
    stop() { return stopOwned(session); },
    validate(result, signal) {
      return validateRecorderMedia({ ffmpegPath, ffprobePath }, contexts.get(result), result, signal, runTool);
    },
    async dispose() {
      if (disposed) return;
      disposed = true; initialized = false; clearInterval(timer); lifetime.abort();
      await stopOwned(session);
    },
  };
}
