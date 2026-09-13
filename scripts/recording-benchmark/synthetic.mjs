import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, existsSync, writeFileSync, readFileSync, statSync, createWriteStream } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resourceSummary } from './analyze.mjs';

// Developer experiment: generated test pattern/sine only. This is NOT game capture or an Overwolf benchmark.
const directory = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(Array.from({ length: (process.argv.length - 2) / 2 }, (_, i) => [process.argv[2 + i * 2], process.argv[3 + i * 2]]));
const output = path.resolve(args['--output'] ?? 'artifacts/recording-benchmark/synthetic');
const seconds = Number(args['--seconds'] ?? 15);
if (!Number.isInteger(seconds) || seconds < 8 || seconds > 120) throw new Error('--seconds must be an integer from 8 to 120.');
if (existsSync(output)) throw new Error('Choose a new --output directory; existing evidence is never overwritten.');
mkdirSync(output, { recursive: true });
const ffmpeg = args['--ffmpeg'] ?? 'ffmpeg';
const ffprobe = args['--ffprobe'] ?? 'ffprobe';
const powershell = args['--powershell'] ?? 'pwsh';
const nvenc = ['-c:v', 'h264_nvenc', '-preset', 'p3', '-tune', 'hq', '-rc', 'cbr', '-multipass', 'disabled', '-rc-lookahead', '0', '-spatial-aq', '0', '-temporal-aq', '0', '-bf', '2'];
const versions = { ffmpeg: execFileSync(ffmpeg, ['-version'], { encoding: 'utf8', windowsHide: true }).split('\n')[0],
  ffprobe: execFileSync(ffprobe, ['-version'], { encoding: 'utf8', windowsHide: true }).split('\n')[0], node: process.version };
writeFileSync(path.join(output, 'experiment.json'), JSON.stringify({ generatedAt: new Date().toISOString(), versions, seconds,
  source: 'CPU-generated 1920x1080 60fps yuv420p testsrc2 and 48kHz sine. Both inputs paced in real time.',
  inferenceLimits: 'No League, game capture hook, GPU-resident frames, contention workload, or Overwolf package. FFmpeg uploads CPU frames; settings are not asserted equivalent to libobs. Counters include existing background applications.' }, null, 2));

async function run(name, preset, { abrupt = false, fragmented = true, encoder = 'nvenc' } = {}) {
  const runPath = path.join(output, name); mkdirSync(runPath);
  const movie = path.join(runPath, 'generated.mp4');
  const command = ['-hide_banner', '-loglevel', 'warning', '-stats_period', '1', '-progress', 'pipe:1', '-filter_threads', '1',
    '-re', '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=60', '-re', '-f', 'lavfi', '-i', 'sine=frequency=1000:sample_rate=48000', '-map', '0:v:0', '-map', '1:a:0'];
  if (preset) {
    const [width, height, fps, bitrate] = preset;
    command.push('-vf', `fps=${fps},scale=${width}:${height}:flags=bilinear`, ...(encoder === 'amf' ? ['-c:v', 'h264_amf', '-quality', 'speed', '-rc', 'cbr'] : nvenc), '-b:v', `${bitrate}k`, '-maxrate', `${bitrate}k`, '-bufsize', `${bitrate * 2}k`, '-g', String(fps * 2),
      '-c:a', 'aac', '-b:a', '128k', '-ar', '48000', '-ac', '2', ...(fragmented ? ['-movflags', '+frag_keyframe+empty_moov+default_base_moof'] : []), '-n', movie);
  } else command.push('-c:v', 'wrapped_avframe', '-c:a', 'pcm_s16le', '-f', 'null', '-');
  const started = performance.now(); const startedUtcMs = Date.now(); let requestedStopMs = null; let requestedStopUtcMs = null;
  const child = spawn(ffmpeg, command, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const progressFile = createWriteStream(path.join(runPath, 'progress.txt')); child.stdout.pipe(progressFile);
  const errorFile = createWriteStream(path.join(runPath, 'ffmpeg.log')); child.stderr.pipe(errorFile);
  const completed = new Promise(resolve => { child.on('error', error => resolve({ error: error.message })); child.on('close', (code, signal) => resolve({ code, signal, endedMs: performance.now() })); });
  child.stdin.on('error', () => {});
  if (!child.pid) throw new Error('FFmpeg did not launch.');
  const sampler = spawn(powershell, ['-NoProfile', '-NonInteractive', '-File', path.join(directory, 'collect.ps1'), '-OutputDirectory', runPath,
    '-DurationSeconds', String(seconds), '-RootProcessIds', String(child.pid), '-Mode', preset ? 'synthetic-enabled' : 'synthetic-disabled'], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let samplerError = ''; sampler.stderr.on('data', data => { samplerError += data; });
  const samplingComplete = new Promise(resolve => { sampler.on('error', error => resolve({ error: error.message })); sampler.on('close', code => resolve({ code, stderr: samplerError })); });
  const samplerDeadline = setTimeout(() => sampler.kill(), (seconds + 30) * 1000);
  const stopTimer = setTimeout(() => { requestedStopMs = performance.now(); requestedStopUtcMs = Date.now(); if (abrupt) child.kill(); else child.stdin.write('q\n'); }, seconds * 1000);
  const deadline = setTimeout(() => child.kill(), (seconds + 15) * 1000);
  const status = await completed; clearTimeout(stopTimer); clearTimeout(deadline);
  const samplingStatus = await samplingComplete; clearTimeout(samplerDeadline);
  let media = null;
  if (existsSync(movie)) {
    media = { bytes: statSync(movie).size, probe: null, decode: null };
    try { media.probe = JSON.parse(execFileSync(ffprobe, ['-v', 'error', '-show_entries', 'format=duration,size:stream=codec_name,codec_type,width,height,avg_frame_rate,start_time,duration,nb_frames', '-of', 'json', movie], { encoding: 'utf8', windowsHide: true, timeout: 15000 })); } catch (error) { media.probeError = error.stderr?.toString().slice(0, 1000) ?? error.message; }
    try { execFileSync(ffmpeg, ['-v', 'error', '-xerror', '-threads', '2', '-i', movie, '-f', 'null', '-'], { encoding: 'utf8', windowsHide: true, timeout: 30000 }); media.decode = 'Full file passed ffmpeg -xerror decode'; } catch (error) { media.decode = error.stderr?.toString().slice(0, 1000) ?? error.message; }
  }
  const samplesFile = path.join(runPath, 'resources.jsonl');
  const rows = existsSync(samplesFile) ? readFileSync(samplesFile, 'utf8').trim().split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
  const activeRows = requestedStopUtcMs === null ? [] : rows.filter(row => Date.parse(row.utc) >= startedUtcMs + 2000 &&
    Date.parse(row.utc) + row.acquisitionMs <= requestedStopUtcMs && row.acquisitionMs <= 3000 &&
    row.processes.some(p => p.owned) && row.processes.filter(p => p.owned).every(p => Number.isFinite(p.cpuPercentMachine)));
  const result = { name, preset, encoder: preset ? encoder : null, fragmented, abrupt, command, status, samplingStatus, startedUtcMs, requestedStopUtcMs,
    elapsedSeconds: (status.endedMs - started) / 1000, stopRequestToExitMs: requestedStopMs === null ? null : status.endedMs - requestedStopMs,
    finalizationCaveat: 'Graceful q-to-process-exit includes encoder drain, muxer finalization and input loop response; it is not Overwolf stop-to-playable time. Abrupt runs have no finalization measurement.',
    media, resourceCoverage: { rawSamples: rows.length, activeSamples: activeRows.length, qualified: activeRows.length >= 5,
      note: 'All resource fields share the active-child window, excluding first 2s and queries taking >3s or crossing stop request. Fewer than 5 samples is invalid. Instrumentation overhead/ambient workload still prevent game-performance inference.' },
    resources: resourceSummary(activeRows) };
  if (abrupt) result.stopRequestToExitMs = null;
  writeFileSync(path.join(runPath, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ name, exit: status.code, sizeBytes: media?.bytes ?? null, stopMs: result.stopRequestToExitMs, cpu: result.resources?.ownedCpuPercentMachine.mean }));
  await sleep(1500);
  return result;
}
const results = [];
// ABBA sequence per preset reduces simple time/order bias. Synthetic clips are intentionally short.
for (const [label, preset] of [['720p30', [1280, 720, 30, 4000]], ['1080p30', [1920, 1080, 30, 6000]], ['1080p60', [1920, 1080, 60, 10000]]]) {
  for (const [index, enabled] of [false, true, true, false].entries()) results.push(await run(`${label}-${index + 1}-${enabled ? 'enabled' : 'disabled'}`, enabled ? preset : null));
}
results.push(await run('amf-720p30', [1280, 720, 30, 4000], { encoder: 'amf' }));
results.push(await run('fragmented-mp4-abrupt', [1280, 720, 30, 4000], { abrupt: true }));
results.push(await run('regular-mp4-abrupt', [1280, 720, 30, 4000], { abrupt: true, fragmented: false }));
writeFileSync(path.join(output, 'results.json'), JSON.stringify(results, null, 2));
