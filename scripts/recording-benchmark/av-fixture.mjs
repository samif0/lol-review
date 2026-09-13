import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';

// Offline generated media only. No screen, window, game, microphone or system-audio capture.
const options = {};
for (let index = 2; index < process.argv.length; index += 2) {
  const key = process.argv[index];
  if (!['--ffmpeg', '--ffprobe', '--output', '--encoder'].includes(key) || !process.argv[index + 1]) {
    throw new Error('Usage: node av-fixture.mjs [--ffmpeg path] [--ffprobe path] [--output new-directory] [--encoder nvenc|amf|qsv|x264]');
  }
  options[key] = process.argv[index + 1];
}
const encoder = options['--encoder'] ?? 'nvenc';
const encoders = {
  nvenc: ['-c:v', 'h264_nvenc', '-preset', 'p3', '-tune', 'hq', '-rc', 'cbr', '-multipass', 'disabled', '-rc-lookahead', '0', '-spatial-aq', '0', '-temporal-aq', '0', '-bf', '2'],
  amf: ['-c:v', 'h264_amf', '-quality', 'speed', '-rc', 'cbr'],
  qsv: ['-c:v', 'h264_qsv', '-preset', 'faster', '-look_ahead', '0'],
  x264: ['-c:v', 'libx264', '-preset', 'ultrafast'],
};
if (!encoders[encoder]) throw new Error('Unsupported encoder. No encoder fallback is automatic.');
const output = path.resolve(options['--output'] ?? `artifacts/recording-benchmark/av-fixture-${new Date().toISOString().replace(/[:.]/g, '-')}`);
if (existsSync(output)) throw new Error('Choose a new output directory; existing evidence is never overwritten.');
mkdirSync(output, { recursive: true });
const sampleRate = 48000;
const frameRate = 30;
const pulseSeconds = [1, 5, 9];
const commands = [];
const limitations = [
  'Synthetic codec/mux timestamp test only; no League, Overwolf capture, WASAPI, real-time device clocks, contention or long-match drift measurement.',
  'The source is generated and encoded offline, not paced in real time. Encoding timing is not a recording-performance benchmark.',
  'Video onset is the first decoded frame above a mean-luma threshold. Fixture pulses align to 30 FPS frames; arbitrary real events have up to one-frame timing ambiguity.',
  'Audio onset uses non-overlapping 1 ms RMS windows with three consecutive above-threshold windows. AAC pre-echo/threshold effects can move the measured onset.',
  'Results subtract the measured lossless-source offset and preserve decoded media timestamps, including container shifts and encoder priming.',
  'A 120 ms audio-delay positive control checks that the analysis detects a known error. A ten-second fixture cannot establish hourly A/V drift.',
];
function executable(name) {
  if (existsSync(name)) return path.resolve(name);
  const resolved = execFileSync(process.platform === 'win32' ? 'where.exe' : 'which', [name], { encoding: 'utf8', windowsHide: true, timeout: 10000 }).trim().split(/\r?\n/)[0];
  if (!resolved || !existsSync(resolved)) throw new Error(`Cannot resolve executable: ${name}`);
  return resolved;
}
async function identity(executablePath) {
  const hash = createHash('sha256');
  for await (const block of createReadStream(executablePath)) hash.update(block);
  return { path: executablePath, sha256: hash.digest('hex'), bytes: statSync(executablePath).size,
    versionOutput: execFileSync(executablePath, ['-version'], { encoding: 'utf8', windowsHide: true, timeout: 10000 }).trim() };
}
function execute(binary, arguments_, label, { binaryOutput = false } = {}) {
  commands.push({ label, executable: binary, arguments: arguments_ });
  try {
    const value = execFileSync(binary, arguments_, { encoding: binaryOutput ? null : 'utf8', windowsHide: true, timeout: 60000, maxBuffer: 16 * 1024 * 1024 });
    return value;
  } catch (error) {
    throw new Error(`${label}: ${error.stderr?.toString().slice(-4000) || error.message}`);
  }
}
function save(name, value) { writeFileSync(path.join(output, name), JSON.stringify(value, null, 2)); }
function timestamp(frame, label) {
  const value = frame.best_effort_timestamp_time ?? frame.pts_time;
  if (value === null || value === undefined || (typeof value === 'string' && value.trim() === '') || !['string', 'number'].includes(typeof value)) {
    throw new Error(`${label} frame lacks a timestamp; timing is unverified.`);
  }
  const seconds = Number(value);
  if (!Number.isFinite(seconds)) throw new Error(`${label} frame has an invalid timestamp; timing is unverified.`);
  return seconds;
}

let ffmpeg;
let ffprobe;
const result = { generatedAt: new Date().toISOString(), kind: 'synthetic-av-codec-mux-fixture', output, status: 'unverified', encoder,
  fixture: { durationSeconds: 10, width: 1280, height: 720, frameRate, sampleRate, pulseSeconds, pulseDurationSeconds: 0.1,
    audioFrequencyHz: 1000, audioAmplitude: 0.7, videoMeanLumaThreshold: 128, audioRmsThreshold: 0.1,
    audioRmsWindowSamples: 48, requiredConsecutiveAudioWindows: 3 },
  limitations, source: null, encoded: [], commands };

function analyzeAudio(raw, frames) {
  if (raw.length % 4 !== 0) throw new Error('Decoded float audio contains a partial sample.');
  const samples = raw.length / 4;
  const runs = [];
  let cumulative = 0;
  for (const frame of frames) {
    const count = Number(frame.nb_samples);
    const pts = timestamp(frame, 'Audio');
    if (!Number.isInteger(count) || count < 1) throw new Error('Audio frame lacks valid sample count.');
    runs.push({ begin: cumulative, end: cumulative + count, pts }); cumulative += count;
  }
  if (cumulative !== samples) throw new Error(`Audio sample mapping mismatch: raw ${samples}, probed ${cumulative}. Timing is unverified.`);
  const window = result.fixture.audioRmsWindowSamples;
  const loud = [];
  for (let start = 0; start + window <= samples; start += window) {
    let power = 0;
    for (let index = start; index < start + window; index++) power += raw.readFloatLE(index * 4) ** 2;
    loud.push(Math.sqrt(power / window) >= result.fixture.audioRmsThreshold);
  }
  const onsets = [];
  let active = false;
  for (let index = 0; index < loud.length - 2; index++) {
    if (!active && loud[index] && loud[index + 1] && loud[index + 2]) {
      const sample = index * window;
      const run = runs.find(item => sample >= item.begin && sample < item.end);
      if (!run) throw new Error('Audio onset has no timestamp mapping.');
      onsets.push({ sample, seconds: run.pts + (sample - run.begin) / sampleRate }); active = true;
    } else if (active && !loud[index] && !loud[index + 1] && !loud[index + 2]) active = false;
  }
  return { samples, onsets, decodedFrames: frames.length };
}
function analyze(movie, label) {
  const probe = JSON.parse(execute(ffprobe, ['-v', 'error', '-show_entries', 'format=duration,size,start_time:stream=index,codec_name,codec_type,sample_rate,channels,start_time,duration,width,height,avg_frame_rate', '-of', 'json', movie], `${label}-stream-probe`));
  const videoFrames = JSON.parse(execute(ffprobe, ['-v', 'error', '-select_streams', 'v:0', '-show_frames', '-show_entries', 'frame=best_effort_timestamp_time,pts_time', '-of', 'json', movie], `${label}-video-frame-probe`)).frames;
  const pixels = execute(ffmpeg, ['-xerror', '-v', 'error', '-threads', '2', '-i', movie, '-map', '0:v:0', '-an', '-filter_threads', '1', '-vf', 'scale=1:1:flags=area,format=gray', '-fps_mode', 'passthrough', '-f', 'rawvideo', 'pipe:1'], `${label}-decode-video`, { binaryOutput: true });
  if (pixels.length !== videoFrames.length) throw new Error(`${label}: video frame count mismatch; timing is unverified.`);
  const videoOnsets = [];
  let wasWhite = false;
  for (let index = 0; index < pixels.length; index++) {
    const white = pixels[index] >= result.fixture.videoMeanLumaThreshold;
    if (white && !wasWhite) {
      const seconds = timestamp(videoFrames[index], 'Video');
      videoOnsets.push({ frame: index, seconds });
    }
    wasWhite = white;
  }
  const audioFrames = JSON.parse(execute(ffprobe, ['-v', 'error', '-select_streams', 'a:0', '-show_frames', '-show_entries', 'frame=best_effort_timestamp_time,pts_time,nb_samples', '-of', 'json', movie], `${label}-audio-frame-probe`)).frames;
  const audioRaw = execute(ffmpeg, ['-xerror', '-v', 'error', '-threads', '2', '-i', movie, '-map', '0:a:0', '-vn', '-ar', String(sampleRate), '-ac', '1', '-c:a', 'pcm_f32le', '-f', 'f32le', 'pipe:1'], `${label}-decode-audio`, { binaryOutput: true });
  const audio = analyzeAudio(audioRaw, audioFrames);
  save(`${label}-frame-timestamps.json`, { video: videoFrames, audio: audioFrames });
  if (videoOnsets.length !== pulseSeconds.length || audio.onsets.length !== pulseSeconds.length) {
    throw new Error(`${label}: expected three distinct pulses, found ${videoOnsets.length} video/${audio.onsets.length} audio. Missing pulses are not zero offset.`);
  }
  const pulses = pulseSeconds.map((expected, index) => ({ expectedSourceSeconds: expected, video: videoOnsets[index], audio: audio.onsets[index],
    audioMinusVideoMs: (audio.onsets[index].seconds - videoOnsets[index].seconds) * 1000 }));
  return { path: movie, bytes: statSync(movie).size, probe, decodedVideoFrames: videoFrames.length, decodedAudioSamples: audio.samples, pulses };
}

try {
  ffmpeg = executable(options['--ffmpeg'] ?? 'ffmpeg');
  ffprobe = executable(options['--ffprobe'] ?? 'ffprobe');
  result.binaries = { ffmpeg: await identity(ffmpeg), ffprobe: await identity(ffprobe), node: process.version };
  try { result.gpu = execute(executable('nvidia-smi'), ['--query-gpu=name,driver_version,pci.bus_id', '--format=csv,noheader'], 'gpu-identity').trim(); }
  catch (error) { result.gpu = null; result.gpuIdentityError = error.message; }
  const source = path.join(output, 'lossless-source.mkv');
  const gate = pulseSeconds.map(time => `between(t,${time},${time + 0.1})`).join('+');
  const sourceArguments = ['-hide_banner', '-loglevel', 'error', '-filter_threads', '1', '-f', 'lavfi', '-i',
    `color=c=black:size=1280x720:rate=30:duration=10,drawbox=color=white:t=fill:enable='${gate}'`,
    '-f', 'lavfi', '-i', `aevalsrc=exprs='if(${gate},0.7*sin(2*PI*1000*t),0)':s=48000:d=10`,
    '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'ffv1', '-level', '3', '-g', '1', '-threads', '2', '-pix_fmt', 'yuv420p',
    '-c:a', 'pcm_s16le', '-ar', '48000', '-ac', '2', '-n', source];
  execute(ffmpeg, sourceArguments, 'generate-lossless-source');
  result.source = analyze(source, 'source');
  for (const pulse of result.source.pulses) {
    if (Math.abs(pulse.video.seconds - pulse.expectedSourceSeconds) > 0.002 || Math.abs(pulse.audio.seconds - pulse.expectedSourceSeconds) > 0.002) {
      throw new Error('Source pulses are not aligned to expected fixture times within 2 ms.');
    }
  }
  for (const delayMs of [0, 120]) {
    const label = delayMs === 0 ? 'h264-aac' : 'positive-control-audio-delay-120ms';
    const movie = path.join(output, `${label}.mp4`);
    const encodeArguments = ['-hide_banner', '-loglevel', 'error', '-threads', '2', '-i', source, '-map', '0:v:0', '-map', '0:a:0',
      ...(delayMs ? ['-af', `adelay=${delayMs}:all=1`] : []), ...encoders[encoder], '-pix_fmt', 'yuv420p', '-b:v', '5000k', '-maxrate', '5000k',
      '-bufsize', '10000k', '-g', '60', '-c:a', 'aac', '-b:a', '128k', '-ar', '48000', '-ac', '2',
      '-movflags', '+frag_keyframe+empty_moov+default_base_moof', '-n', movie];
    try {
      execute(ffmpeg, encodeArguments, `${label}-encode`);
      const measured = analyze(movie, label);
      measured.injectedAudioDelayMs = delayMs;
      measured.offsetConvention = 'Positive = audio later than video; source baseline subtracted.';
      measured.pulses = measured.pulses.map((pulse, index) => ({ ...pulse,
        sourceCorrectedAudioMinusVideoMs: pulse.audioMinusVideoMs - result.source.pulses[index].audioMinusVideoMs }));
      measured.sourceCorrectedMeanOffsetMs = measured.pulses.reduce((sum, pulse) => sum + pulse.sourceCorrectedAudioMinusVideoMs, 0) / measured.pulses.length;
      measured.firstToLastOffsetChangeMs = measured.pulses.at(-1).sourceCorrectedAudioMinusVideoMs - measured.pulses[0].sourceCorrectedAudioMinusVideoMs;
      measured.matchesInjectedDelayWithin5ms = measured.pulses.every(pulse => Math.abs(pulse.sourceCorrectedAudioMinusVideoMs - delayMs) <= 5);
      measured.status = 'measured'; result.encoded.push(measured);
    } catch (error) { result.encoded.push({ path: movie, injectedAudioDelayMs: delayMs, status: 'unverified', error: error.message }); }
  }
  result.positiveControlPassed = result.encoded.find(run => run.injectedAudioDelayMs === 120)?.matchesInjectedDelayWithin5ms === true;
  result.status = result.encoded.every(run => run.status === 'measured') && result.positiveControlPassed ? 'measured' : 'incomplete';
} catch (error) { result.error = error.message; }
finally {
  save('result.json', result);
  console.log(JSON.stringify({ output, status: result.status, positiveControlPassed: result.positiveControlPassed ?? null,
    offsets: result.encoded.map(run => ({ injectedDelayMs: run.injectedAudioDelayMs, meanOffsetMs: run.sourceCorrectedMeanOffsetMs ?? null, status: run.status })) }, null, 2));
  if (result.status !== 'measured') process.exitCode = 1;
}
