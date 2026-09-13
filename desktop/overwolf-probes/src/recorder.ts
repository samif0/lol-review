import type {} from '@overwolf/ow-electron-packages-types';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { open, realpath, stat } from 'node:fs/promises';
import path from 'node:path';
import { CaptureController, type StopResult, type CaptureState, type CaptureDeadlines } from './controller.js';
import { LEAGUE_EXE, dimensions, inside, isLeague } from './policy.js';
import type { Journal } from './journal.js';
import { PRESETS, selectEncoder, encoderPolicy, type RecorderConfig } from './recording-policy.js';
export type { RecorderConfig } from './recording-policy.js';

type Recorder = overwolf.packages.OverwolfPackageManager['recorder'];
const run = promisify(execFile);
type RecorderStats = NonNullable<Parameters<NonNullable<Parameters<Recorder['startRecording']>[2]>>[0]['stats']>;
const metric = (value: unknown) => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
export function writeRecorderStats(stats: RecorderStats, journal: Journal, phase: 'sample' | 'stop') {
  journal.write({ kind: 'recorder-stats', phase, cpu: metric(stats.cpuUsage), memoryMB: metric(stats.memoryUsage), fps: metric(stats.activeFps),
    outputSkipped: metric(stats.outputSkippedFrames), renderSkipped: metric(stats.renderSkippedFrames),
    outputTotal: metric(stats.outputTotalFrames), renderTotal: metric(stats.renderTotalFrames),
    renderTimeMs: metric(stats.averageFrameRenderTime), availableDiskMB: metric(stats.availableDiskSpace) });
}

export async function prepareRecorder(recorder: Recorder, pid: number, config: RecorderConfig, journal: Journal, signal?: AbortSignal) {
  signal?.throwIfAborted();
  if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error('invalid-game-pid');
  if (!Object.hasOwn(PRESETS, config.preset) || typeof config.allowSoftwareEncoder !== 'boolean') throw new Error('invalid-capture-config');
  const expectedVideo = { ...dimensions(config.sourceWidth, config.sourceHeight, config.preset),
    colorFormat: 'NV12' as const, colorSpec: '709' as const, colorRange: 'Partial' as const };
  const info = await recorder.queryInformation(true);
  signal?.throwIfAborted();
  const chosen = selectEncoder(info.video, config);
  const expectedEncoder = encoderPolicy(chosen, config.preset);
  if (!info.audio.encoders.some(e => e.type === 'ffmpeg_aac' && e.codec === 'aac')) throw new Error('required-encoder-unavailable');
  const builder = await recorder.createSettingsBuilder({ videoEncoder: chosen.type, audioEncoder: 'ffmpeg_aac',
    includeDefaultAudioSources: false, separateAudioTracks: false });
  signal?.throwIfAborted();
  if (builder.videoEncoderSettings.type !== chosen.type) throw new Error('encoder-selection-mismatch');
  builder.videoSettings = { ...builder.videoSettings, ...expectedVideo };
  Object.assign(builder.videoEncoderSettings, expectedEncoder);
  const expectedAudio = { sampleRate: 48000 as const, speakerLayer: 'SPEAKERS_STEREO' as const, lowLatencyAudioBuffering: false };
  Object.assign(builder.audioSettings, expectedAudio);
  builder.addGameSource({ gameProcess: pid, captureCursor: true, captureOverlays: false, limitFramerate: true, sliCompatibility: false });
  builder.addApplicationAudioCapture({ processName: LEAGUE_EXE }, { volume: 1, filters: [] });
  const settings = builder.build();
  if (settings.videoEncoderSettings.type !== chosen.type || settings.audioEncoder?.type !== 'ffmpeg_aac' ||
      settings.audioEncoder.codec !== 'aac') throw new Error('encoder-selection-mismatch');
  if (Object.entries(expectedVideo).some(([key, value]) => settings.videoSettings[key as keyof typeof expectedVideo] !== value) ||
      Object.entries(expectedEncoder).some(([key, value]) => (settings.videoEncoderSettings as unknown as Record<string, unknown>)[key] !== value)) throw new Error('video-settings-mismatch');
  if (Object.entries(expectedAudio).some(([key, value]) => settings.audioSettings[key as keyof typeof expectedAudio] !== value)) throw new Error('audio-settings-mismatch');
  const source = settings.sources[0];
  const application = settings.audioSettings.applications[0];
  if (settings.audioSettings.inputs.length !== 0 || settings.audioSettings.outputs.length !== 0 ||
      settings.audioSettings.applications.length !== 1 || application.name !== LEAGUE_EXE || application.type !== 'output' ||
      (application.volume ?? 1) !== 1 || (application.filters?.length ?? 0) !== 0 || settings.sources.length !== 1 || source.type !== 'Game' ||
      source.properties?.gameProcess !== pid || source.properties?.captureOverlays !== false ||
      source.properties?.captureCursor !== true || source.properties?.limitFramerate !== true ||
      source.properties?.sliCompatibility !== false) throw new Error('capture-scope-mismatch');
  journal.write({ kind: 'capture-policy', encoder: chosen.type, software: chosen.type === 'obs_x264',
    defaultAudio: false, microphone: false, captureOverlays: false, fps: settings.videoSettings.fps ?? 0,
    width: settings.videoSettings.outputWidth ?? 0, height: settings.videoSettings.outputHeight ?? 0,
    preset: config.preset, limitFramerate: true, adapterAffinityVerified: false, nativeSettingsVerified: false });
  journal.write({ kind: 'encoder-policy', ...expectedEncoder });
  return settings;
}

/** Inspect bounded top-level headers; never load the match-sized mdat payload into memory. */
export async function isFragmentedMp4(file: string, signal?: AbortSignal): Promise<boolean> {
  const handle = await open(file, 'r');
  try {
    const size = (await handle.stat()).size;
    const header = Buffer.alloc(16);
    let offset = 0, ftyp = false, moov = false, fragment = false, media = false;
    // A two-hour 2-second-keyframe recording needs fewer than 8192 top-level boxes.
    for (let count = 0; offset < size && count < 16_384; count++) {
      signal?.throwIfAborted();
      if (size - offset < 8 || (await handle.read(header, 0, 8, offset)).bytesRead !== 8) return false;
      let boxSize = header.readUInt32BE(0), headerSize = 8;
      const type = header.toString('ascii', 4, 8);
      if (boxSize === 1) {
        if ((await handle.read(header, 8, 8, offset + 8)).bytesRead !== 8) return false;
        const extended = header.readBigUInt64BE(8);
        if (extended > BigInt(Number.MAX_SAFE_INTEGER)) return false;
        boxSize = Number(extended); headerSize = 16;
      } else if (boxSize === 0) boxSize = size - offset;
      if (boxSize < headerSize || boxSize > size - offset) return false;
      if (type === 'ftyp') ftyp = true;
      if (type === 'moov') moov = true;
      if (type === 'moof') fragment = true;
      if (type === 'mdat' && boxSize > headerSize) media = true;
      offset += boxSize;
    }
    return offset === size && ftyp && moov && fragment && media;
  } finally { await handle.close(); }
}

/** Decode samples plus ffprobe are preliminary checks, not packaged-player or full-match proof. */
export async function validateMedia(recorder: Recorder, root: string, result: StopResult, journal: Journal, signal?: AbortSignal,
  config?: RecorderConfig): Promise<boolean> {
  signal?.throwIfAborted();
  if (!result.filePath || !path.isAbsolute(result.filePath) || !inside(root, result.filePath) ||
      !/^capture[^\\/]*\.mp4$/i.test(path.basename(result.filePath)) || path.relative(path.resolve(root), path.dirname(path.resolve(result.filePath))) !== '') return false;
  const file = await realpath(result.filePath);
  const actualRoot = await realpath(root);
  const metadata = await stat(file);
  if (!inside(actualRoot, file) || !metadata.isFile() || metadata.size < 24 || metadata.size > 20 * 1024 ** 3 ||
      !await isFragmentedMp4(file, signal)) return false;
  signal?.throwIfAborted();
  const { stdout } = await run(recorder.ffprobePath, ['-v', 'error', '-show_entries',
    'format=duration,format_name:stream=codec_type,codec_name,width,height,avg_frame_rate,channels', '-of', 'json', file],
    { timeout: 10_000, maxBuffer: 32_768, windowsHide: true, signal });
  const probe = JSON.parse(stdout) as { format?: { duration?: string; format_name?: string };
    streams?: { codec_type: string; codec_name: string; width?: number; height?: number; avg_frame_rate?: string; channels?: number }[] };
  const duration = Number(probe.format?.duration);
  const video = probe.streams?.filter(s => s.codec_type === 'video') ?? [];
  const audio = probe.streams?.filter(s => s.codec_type === 'audio') ?? [];
  if (!Number.isFinite(duration) || duration <= 0 || !Number.isFinite(result.duration) ||
      Math.abs(duration - result.duration! / 1000) > Math.max(2, duration * 0.02) ||
      !probe.format?.format_name?.split(',').includes('mp4') || probe.streams?.length !== 2 ||
      video.length !== 1 || video[0].codec_name !== 'h264' || audio.length !== 1 || audio[0].codec_name !== 'aac' ||
      !Number.isSafeInteger(audio[0].channels) || audio[0].channels! < 1 || audio[0].channels! > 2) return false;
  if (config) {
    const expected = dimensions(config.sourceWidth, config.sourceHeight, config.preset);
    const fraction = video[0].avg_frame_rate?.split('/').map(Number);
    const fps = fraction?.length === 2 ? fraction[0] / fraction[1] : NaN;
    if (video[0].width !== expected.outputWidth || video[0].height !== expected.outputHeight ||
        !Number.isFinite(fps) || Math.abs(fps - expected.fps) > 1) return false;
  }
  for (const at of [0, duration / 2, Math.max(0, duration - 2)]) {
    signal?.throwIfAborted();
    await run(recorder.ffmpegPath, ['-v', 'error', '-xerror', '-ss', String(at), '-i', file,
      '-map', '0:v:0', '-map', '0:a:0', '-t', '0.2', '-f', 'null', '-'],
      { timeout: 10_000, maxBuffer: 16_384, windowsHide: true, signal });
  }
  signal?.throwIfAborted();
  journal.write({ kind: 'media-probe', durationSeconds: duration, decodeSamples: 3, fragmentedMp4: true, packagedPlayerVerified: false,
    stopDurationMs: result.duration ?? 0, startTimeEpoch: Number.isFinite(result.startTimeEpoch) && result.startTimeEpoch! > 0 ? result.startTimeEpoch! : 0 });
  return true;
}

export function recorderController(recorder: Recorder, pid: number, config: RecorderConfig, root: string, journal: Journal,
  persist: (state: CaptureState) => void, deadlines?: Partial<CaptureDeadlines>): CaptureController {
  let startIssued = false;
  let stopRequestedAt: number | undefined, stopMetadataWritten = false;
  const controller = new CaptureController({
    start: async (onStopped, signal) => {
      signal.throwIfAborted();
      if (await recorder.isActive()) throw new Error('recorder-already-active');
      signal.throwIfAborted();
      const settings = await prepareRecorder(recorder, pid, config, journal, signal);
      signal.throwIfAborted();
      startIssued = true;
      await recorder.startRecording({ filePath: path.join(root, 'capture'), fileFormat: 'fragmented_mp4',
        autoShutdownOnGameExit: true }, settings, result => {
          const stoppedAt = performance.now();
          // Retain the unthrottled final counters even when they arrive within a
          // second of the last sample. Metadata failure must not lose the stop.
          try {
            if (!stopMetadataWritten) {
              stopMetadataWritten = true;
              if (result?.stats) writeRecorderStats(result.stats, journal, 'stop');
              journal.write({ kind: 'recorder-stop', requestToCallbackMs: stopRequestedAt === undefined ? null : stoppedAt - stopRequestedAt,
                durationMs: metric(result?.duration) });
            }
          } catch { void controller.crash(); }
          onStopped(result);
        });
    },
    // A rejected isActive/setup check must never stop someone else's recording/replay.
    stop: async () => {
      if (startIssued) { stopRequestedAt ??= performance.now(); await recorder.stopRecording(); }
    },
    validate: (result, signal) => validateMedia(recorder, root, result, journal, signal, config)
  }, persist, deadlines);
  return controller;
}

/** Fixed executable filter; only matching PIDs leave PowerShell. No command lines or paths are logged. */
export async function scanLeagueProcesses(signal?: AbortSignal): Promise<number[]> {
  if (process.platform !== 'win32') throw new Error('windows-process-scan-required');
  const script = "$ErrorActionPreference='Stop'; @((Get-CimInstance Win32_Process -Filter \"Name = 'League of Legends.exe'\" -Property Name,ProcessId) | Where-Object { $_.Name -ieq 'League of Legends.exe' } | ForEach-Object { [int]$_.ProcessId }) | ConvertTo-Json -Compress";
  const powershell = path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const { stdout } = await run(powershell, ['-NoProfile', '-NonInteractive', '-Command', script],
    { timeout: 3000, maxBuffer: 4096, windowsHide: true, signal });
  const value: unknown = stdout.trim() ? JSON.parse(stdout) : [];
  const pids = Array.isArray(value) ? value : [value];
  if (pids.length > 16 || pids.some(pid => !Number.isSafeInteger(pid) || pid <= 0)) throw new Error('invalid-process-metadata');
  return [...new Set(pids as number[])];
}
export interface RecorderTracking { dispose(): void }
export interface RecorderTrackingOptions {
  signal?: AbortSignal;
  scan?: (signal: AbortSignal) => Promise<number[]>;
  intervalMs?: number;
  registrationMs?: number;
  /** Cheap check for an already identified gameplay PID; discovery still checks executable metadata. */
  isAlive?: (pid: number) => boolean;
}
export function processIsAlive(pid: number): boolean {
  try { process.kill(pid, 0); return true; }
  catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ESRCH') return false;
    throw new Error('process-liveness-unavailable');
  }
}
export async function trackRecorderGame(recorder: Recorder, detected: (pid: number | undefined) => void, journal: Journal,
  options: RecorderTrackingOptions = {}): Promise<RecorderTracking> {
  let disposed = false, current: number | undefined, revision = 0, scanning = false, scanFailed = false;
  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let lastStats = -Infinity;
  const abort = new AbortController();
  const dispose = () => {
    if (disposed) return;
    disposed = true; abort.abort(); clearInterval(pollTimer);
    options.signal?.removeEventListener('abort', dispose);
    // The documented API has no off/removeListener; the three handlers below become inert.
    try { if (current !== undefined) detected(undefined); } catch { /* Inert even if the caller cannot persist. */ }
    current = undefined;
  };
  options.signal?.addEventListener('abort', dispose, { once: true });
  if (options.signal?.aborted) { dispose(); throw new Error('tracking-cancelled'); }
  const update = (next: number | undefined, source: string) => {
    if (disposed || next === current) return;
    journal.write({ kind: next === undefined ? 'game-exit' : 'game-launched', gameplayProcess: true, source });
    current = next; detected(next);
  };
  const guard = (operation: () => void) => { if (!disposed) try { operation(); } catch { dispose(); } };
  const validGame = (game: unknown): game is { id: number; type: string; processInfo: { pid: number; fullPath: string } } => {
    if (!game || typeof game !== 'object') return false;
    const candidate = game as { id: number; type: string; processInfo?: { pid: number; fullPath: string } };
    return typeof candidate.processInfo?.fullPath === 'string' && isLeague(candidate);
  };
  try {
    recorder.on('game-launched', game => guard(() => {
      if (!validGame(game)) return;
      revision++; update(game.processInfo.pid, 'recorder-event');
    }));
    recorder.on('game-exit', game => guard(() => {
      if (!validGame(game) || game.processInfo.pid !== current) return;
      revision++; update(undefined, 'recorder-event');
    }));
    recorder.on('stats', stats => guard(() => {
      if (!stats || performance.now() - lastStats < 1000) return;
      lastStats = performance.now();
      writeRecorderStats(stats, journal, 'sample');
    }));
  } catch (error) { dispose(); throw error; }
  const scan = options.scan ?? scanLeagueProcesses;
  const isAlive = options.isAlive ?? processIsAlive;
  const poll = async () => {
    if (disposed || scanning) return;
    scanning = true;
    const atRevision = revision;
    try {
      // Avoid launching PowerShell/WMI every five seconds while the known game
      // remains alive. Native launch/exit events handle replacement; after exit,
      // clear the old identity before querying for a new gameplay process.
      if (current !== undefined) {
        if (isAlive(current)) return;
        guard(() => update(undefined, 'os-process-liveness'));
      }
      const pids = await scan(abort.signal);
      if (disposed || revision !== atRevision) return;
      if (!Array.isArray(pids) || pids.length > 16 || pids.some(pid => !Number.isSafeInteger(pid) || pid <= 0)) throw new Error('invalid-process-metadata');
      // Multiple gameplay processes are ambiguous; never guess which process the user intends.
      guard(() => { update(pids.length === 1 ? pids[0] : undefined, 'os-process-metadata'); scanFailed = false; });
    } catch {
      if (!disposed && revision === atRevision) guard(() => {
        update(undefined, 'os-process-metadata');
        if (!scanFailed) journal.write({ kind: 'process-scan-unavailable' });
        scanFailed = true;
      });
    } finally { scanning = false; }
  };
  let timeout: ReturnType<typeof setTimeout> | undefined;
  try {
    await new Promise<void>((resolve, reject) => {
      const onAbort = () => reject(new Error('tracking-cancelled'));
      abort.signal.addEventListener('abort', onAbort, { once: true });
      const finish = (error?: Error) => { clearTimeout(timeout); abort.signal.removeEventListener('abort', onAbort); error ? reject(error) : resolve(); };
      timeout = setTimeout(() => finish(new Error('game-registration-timeout')), options.registrationMs ?? 5000);
      Promise.resolve().then(() => {
        abort.signal.throwIfAborted();
        return recorder.registerGames({ gamesIds: [5426], all: false, includeUnsupported: false });
      })
        .then(() => finish(), () => finish(new Error('game-registration-failed')));
      if (abort.signal.aborted) onAbort();
    });
    if (disposed) throw new Error('tracking-cancelled');
    pollTimer = setInterval(() => { void poll(); }, options.intervalMs ?? 5000);
    pollTimer.unref();
    void poll();
    return { dispose };
  } catch (error) { dispose(); throw error; }
  finally { clearTimeout(timeout); }
}
