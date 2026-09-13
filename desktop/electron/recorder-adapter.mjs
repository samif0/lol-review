import path from 'node:path';
import { realpath, readdir, stat } from 'node:fs/promises';
import { LEAGUE_ID, LEAGUE_EXE, RECORDING_PRESETS, RecorderAdapterError, recorderError, checkRecordingAbort,
  sourceDimensions, selectHardwareEncoder, hardwareEncoderSettings, configureGameCapture } from './recorder-policy.mjs';
import { inspectLeagueWindow, scanLeaguePids, leaguePidAlive } from './recorder-windows.mjs';
import { validateRecorderMedia, runRecorderTool } from './recorder-media.mjs';

export { RECORDING_PRESETS } from './recorder-policy.mjs';

function bounded(operation, signal, category, timeoutMs) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = action => { if (!settled) { settled = true; clearTimeout(timer); signal?.removeEventListener('abort', aborted); action(); } };
    const aborted = () => finish(() => reject(recorderError('recording-aborted')));
    const timer = setTimeout(() => finish(() => reject(recorderError(category))), timeoutMs);
    Promise.resolve(operation).then(value => finish(() => resolve(value)), error => finish(() => reject(error instanceof RecorderAdapterError ? error : recorderError(category))));
    signal?.addEventListener('abort', aborted, { once: true });
    if (signal?.aborted) aborted();
  });
}
const validPid = value => Number.isSafeInteger(value) && value > 0;
const safeNumber = value => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
const validGame = value => value?.id === LEAGUE_ID && value.type === 'Game' && validPid(value.processInfo?.pid) &&
  typeof value.processInfo.fullPath === 'string' && path.win32.basename(value.processInfo.fullPath).toLowerCase() === LEAGUE_EXE.toLowerCase();
function stoppedResult(value) {
  const malformed = !value || value.hasError !== false || value.error || !(typeof value.duration === 'number' && Number.isFinite(value.duration) && value.duration > 0) ||
    (value.reason !== undefined && !Number.isInteger(value.reason)) ||
    (value.splitCount !== undefined && (!Number.isInteger(value.splitCount) || value.splitCount < 0)) ||
    (value.startTimeEpoch !== undefined && (!Number.isFinite(value.startTimeEpoch) || value.startTimeEpoch <= 0));
  const result = { hasError: Boolean(malformed), filePath: typeof value?.filePath === 'string' ? value.filePath : '',
    duration: safeNumber(value?.duration),
    ...(safeNumber(value?.startTimeEpoch) === null ? {} : { startTimeEpoch: value.startTimeEpoch }),
    ...(Number.isInteger(value?.reason) ? { reason: value.reason } : {}),
    ...(Number.isInteger(value?.splitCount) ? { splitCount: value.splitCount } : {}),
    ...(malformed ? { error: 'recording-provider-error' } : {}) };
  if (value?.stats && typeof value.stats === 'object') result.stats = Object.fromEntries([
    'cpuUsage', 'memoryUsage', 'activeFps', 'outputSkippedFrames', 'renderSkippedFrames', 'outputTotalFrames', 'renderTotalFrames',
    'averageFrameRenderTime', 'availableDiskSpace'].map(key => [key, safeNumber(value.stats[key])]));
  return result;
}

/** Native capture only; the host coordinator owns durable session/match state.
 * initialize requires an activated Recorder package. No diagnostic entry point,
 * developer credentials, Overlay, software encoder or display fallback is used.
 * inspectGame/scanGames/isAlive/runTool are injected only for isolated tests.
 */
export function createOverwolfRecorder({ recorder, onGame = () => {}, onUnavailable = () => {}, onDetectionIssue = () => {}, shouldDiscover = () => true,
  inspectGame = inspectLeagueWindow, scanGames = scanLeaguePids, isAlive = leaguePidAlive,
  runTool = runRecorderTool, pollMs = 5000, deadlines = {} } = {}) {
  const limits = { registration: 5000, setup: 10_000, start: 15_000, stop: 5000, ...deadlines };
  if (!Number.isFinite(pollMs) || pollMs < 1 || Object.values(limits).some(value => !Number.isFinite(value) || value < 1 || value > 120_000))
    throw recorderError('recorder-options-invalid');
  let disposed = false, initialized = false, initialization;
  let identified, revision = 0, polling = false, timer, session;
  const lifetime = new AbortController();
  const contexts = new WeakMap();
  const unavailable = code => { try { Promise.resolve(onUnavailable(code)).catch(() => {}); } catch { /* Consumer owns its failure state. */ } };
  const detectionIssue = code => { try { Promise.resolve(onDetectionIssue(code)).catch(() => {}); } catch { /* Detection can retry independently. */ } };
  const publish = value => {
    if (disposed) return;
    try { Promise.resolve(onGame(value)).catch(() => unavailable('recorder-consumer-failed')); }
    catch { unavailable('recorder-consumer-failed'); }
  };
  const identify = pid => {
    if (identified === pid) return;
    const prior = identified; identified = pid;
    if (!initialized) return;
    if (prior !== undefined) publish({ pid: prior, running: false });
    if (pid !== undefined) publish({ pid, running: true });
  };
  const poll = async () => {
    if (disposed || !initialized || polling) return;
    try { if (!shouldDiscover()) return; }
    catch { detectionIssue('game-process-inspection-unavailable'); return; }
    polling = true;
    const generation = revision;
    try {
      if (identified !== undefined) {
        if (isAlive(identified)) return;
        identify(undefined);
      }
      const pids = await bounded(scanGames(lifetime.signal), lifetime.signal, 'game-process-inspection-unavailable', limits.setup);
      if (disposed || generation !== revision) return;
      if (!Array.isArray(pids) || pids.length > 16 || pids.some(pid => !validPid(pid))) throw recorderError('game-process-inspection-unavailable');
      identify(pids.length === 1 ? pids[0] : undefined);
      if (pids.length > 1) detectionIssue('game-process-ambiguous');
    } catch (error) {
      // A failed query is unknown, not a confirmed process exit or package
      // failure. Keep an identified game until native exit or liveness proves it
      // exited, and let later discovery recover without disabling recording.
      if (!disposed && generation === revision) detectionIssue(error.code ?? 'game-process-inspection-unavailable');
    } finally { polling = false; }
  };
  const stopOwned = async owned => {
    if (!owned?.issued || owned.completed) return;
    if (owned.stopOperation) return owned.stopOperation;
    owned.stopRequestedAt ??= performance.now();
    const nativeStop = Promise.resolve().then(() => recorder.stopRecording());
    owned.stopOperation = bounded(Promise.race([nativeStop, owned.finished]), undefined, 'recorder-stop-failed', limits.stop);
    return owned.stopOperation;
  };
  const cancelOwned = owned => {
    owned.cancelled = true; owned.abort.abort();
    void stopOwned(owned).catch(() => unavailable('recorder-stop-failed'));
  };
  return {
    initialize() {
      if (disposed) return Promise.reject(recorderError('recorder-disposed'));
      if (initialization) return initialization;
      initialization = (async () => {
        try {
          if (!recorder || !['on', 'registerGames', 'queryInformation', 'createSettingsBuilder', 'isActive', 'startRecording', 'stopRecording']
            .every(name => typeof recorder[name] === 'function')) throw recorderError('recorder-unavailable');
          recorder.on('game-launched', game => {
            if (disposed || !validGame(game)) return;
            revision++; identify(game.processInfo.pid);
          });
          recorder.on('game-exit', game => {
            if (disposed || !validGame(game) || game.processInfo.pid !== identified) return;
            revision++; identify(undefined);
          });
          await bounded(Promise.resolve().then(() => recorder.registerGames({ gamesIds: [LEAGUE_ID], all: false, includeUnsupported: false })),
            lifetime.signal, 'recorder-registration-failed', limits.registration);
          checkRecordingAbort(lifetime.signal);
          initialized = true;
          if (identified !== undefined) publish({ pid: identified, running: true });
          timer = setInterval(() => { void poll(); }, pollMs); timer.unref();
          void poll();
        } catch (error) {
          if (!disposed) unavailable(error.code ?? 'recorder-registration-failed');
          throw error instanceof RecorderAdapterError ? error : recorderError('recorder-registration-failed');
        }
      })();
      return initialization;
    },
    async start({ pid, preset = '720p30', outputDirectory, signal, onStopped = () => {} } = {}) {
      if (disposed || !initialized) throw recorderError(disposed ? 'recorder-disposed' : 'recorder-unavailable');
      if (session && !session.completed) throw recorderError('recorder-busy');
      if (!validPid(pid)) throw recorderError('game-process-invalid');
      if (!Object.hasOwn(RECORDING_PRESETS, preset)) throw recorderError('recording-preset-invalid');
      if (typeof outputDirectory !== 'string' || !path.isAbsolute(outputDirectory)) throw recorderError('recording-path-invalid');
      const owned = session = { abort: new AbortController(), issued: false, completed: false, cancelled: false };
      owned.finished = new Promise(resolve => { owned.finish = resolve; });
      const combined = AbortSignal.any([lifetime.signal, owned.abort.signal, ...(signal ? [signal] : [])]);
      const onAbort = () => cancelOwned(owned);
      signal?.addEventListener('abort', onAbort, { once: true });
      const setup = operation => bounded(operation, combined, 'recorder-setup-failed', limits.setup);
      try {
        checkRecordingAbort(combined);
        const window = await setup(inspectGame(pid, combined));
        checkRecordingAbort(combined);
        if (window?.pid !== pid) throw recorderError('game-process-invalid');
        const video = sourceDimensions(window.width, window.height, preset);
        // Client-rectangle metadata is not proof of the game's internal render
        // resolution. Resolution changes during this capture are not yet tracked.
        publish({ pid, running: true, width: window.width, height: window.height });
        owned.root = await setup(realpath(outputDirectory));
        const directory = await setup(stat(owned.root));
        if (!directory.isDirectory() || (await setup(readdir(owned.root))).some(name => /^capture.*\.mp4$/i.test(name)))
          throw recorderError('recording-path-invalid');
        checkRecordingAbort(combined);
        if (await setup(recorder.isActive())) throw recorderError('recorder-already-active');
        checkRecordingAbort(combined);
        const information = await setup(recorder.queryInformation(true));
        checkRecordingAbort(combined);
        const selected = selectHardwareEncoder(information);
        const encoder = hardwareEncoderSettings(selected, preset);
        const builder = await setup(recorder.createSettingsBuilder({ videoEncoder: selected.type, audioEncoder: 'ffmpeg_aac',
          includeDefaultAudioSources: false, separateAudioTracks: false }));
        checkRecordingAbort(combined);
        const settings = configureGameCapture(builder, { pid, video, encoder });
        owned.video = video;
        const callback = raw => {
          if (owned.completed) return;
          const stoppedAt = performance.now();
          owned.completed = true;
          owned.finish();
          signal?.removeEventListener('abort', onAbort);
          const result = stoppedResult(raw);
          result.requestToCallbackMs = owned.stopRequestedAt === undefined ? null : stoppedAt - owned.stopRequestedAt;
          contexts.set(result, owned);
          try { Promise.resolve(onStopped(result)).catch(() => unavailable('recorder-consumer-failed')); }
          catch { unavailable('recorder-consumer-failed'); }
        };
        checkRecordingAbort(combined);
        const nativeStart = Promise.resolve().then(() => {
          checkRecordingAbort(combined);
          owned.issued = true;
          return recorder.startRecording({ filePath: path.join(owned.root, 'capture'),
            fileFormat: 'fragmented_mp4', autoShutdownOnGameExit: true }, settings, callback);
        });
        const startSettled = () => {
          if (owned.cancelled && !owned.completed) {
            // A provider may finish starting after an earlier stop resolved.
            // Keep ownership until its own completion callback; never stop a new session.
            const previousStop = owned.stopOperation;
            void Promise.resolve(previousStop).catch(() => {}).then(() => {
              if (owned.completed) return;
              owned.stopOperation = undefined;
              return stopOwned(owned);
            }).catch(() => unavailable('recorder-stop-failed'));
          }
        };
        void nativeStart.then(startSettled, startSettled);
        try { await bounded(Promise.race([nativeStart, owned.finished]), combined, 'recorder-start-failed', limits.start); }
        catch (error) { if (!owned.completed) throw error; }
        if (!owned.completed) checkRecordingAbort(combined);
        return { encoder: selected.type, width: video.outputWidth, height: video.outputHeight, fps: video.fps,
          bitrate: encoder.bitrate, sourceWidth: window.width, sourceHeight: window.height };
      } catch (error) {
        cancelOwned(owned);
        if (!owned.issued) {
          owned.completed = true;
          signal?.removeEventListener('abort', onAbort);
          if (session === owned) session = undefined;
        }
        throw error instanceof RecorderAdapterError ? error : recorderError('recorder-setup-failed');
      }
    },
    async stop() {
      if (!session) return;
      session.cancelled = true; session.abort.abort();
      await stopOwned(session);
    },
    validate(result, signal) {
      return validateRecorderMedia(recorder, result && typeof result === 'object' ? contexts.get(result) : undefined, result, signal, runTool);
    },
    dispose() {
      if (disposed) return;
      disposed = true; initialized = false; clearInterval(timer); lifetime.abort();
      if (session && !session.completed) cancelOwned(session);
      // Recorder has no documented off/removeListener. Registration handlers above
      // become inert; an owned pending stop callback can still finish host draining.
    },
  };
}
