import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { open, realpath, stat } from 'node:fs/promises';
import path from 'node:path';
import { checkRecordingAbort, recorderError, RecorderAdapterError } from './recorder-policy.mjs';

export const runRecorderTool = promisify(execFile);
export function insideRecordingDirectory(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative !== '' && !path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`);
}

/** Bounded memory/header work even for a match-sized mdat payload. */
export async function fragmentedMp4(file, signal) {
  const handle = await open(file, 'r');
  try {
    const { size } = await handle.stat();
    const header = Buffer.alloc(16);
    let offset = 0, initialization = false, type = false, fragment = false, media = false;
    for (let count = 0; offset < size && count < 262_144; count++) {
      checkRecordingAbort(signal);
      if (size - offset < 8 || (await handle.read(header, 0, 8, offset)).bytesRead !== 8) return false;
      let bytes = header.readUInt32BE(0), headerBytes = 8;
      const name = header.toString('ascii', 4, 8);
      if (bytes === 1) {
        if ((await handle.read(header, 8, 8, offset + 8)).bytesRead !== 8) return false;
        const extended = header.readBigUInt64BE(8);
        if (extended > BigInt(Number.MAX_SAFE_INTEGER)) return false;
        bytes = Number(extended); headerBytes = 16;
      } else if (bytes === 0) bytes = size - offset;
      if (bytes < headerBytes || bytes > size - offset) return false;
      if (name === 'ftyp') type = true;
      if (name === 'moov') initialization = true;
      if (name === 'moof') fragment = true;
      if (name === 'mdat' && bytes > headerBytes) media = true;
      offset += bytes;
    }
    return offset === size && type && initialization && fragment && media;
  } finally { await handle.close(); }
}

const positive = value => typeof value === 'number' && Number.isFinite(value) && value > 0;
const numeric = value => (typeof value === 'number' || (typeof value === 'string' && value.trim() !== '')) && Number.isFinite(Number(value)) ? Number(value) : null;
function decodedBothStreams(stderr) {
  // A successful process exit also occurs when one mapped stream is already at
  // EOF. Require actual decoded frames from both filters, not codec declarations
  // or the null muxer's progress summary. Disable video pixel checksums below.
  if (typeof stderr !== 'string') return false;
  return /^\[Parsed_showinfo_\d+ @ [^\]\r\n]+\]\s+n:\s*\d+\s+pts:/m.test(stderr)
    && /^\[Parsed_ashowinfo_\d+ @ [^\]\r\n]+\]\s+n:\s*\d+\s+pts:[^\r\n]*\bnb_samples:\s*[1-9]\d*\b/m.test(stderr);
}
export async function validateRecorderMedia(recorder, context, result, signal, run = runRecorderTool) {
  try {
    checkRecordingAbort(signal);
    if (!context || !result || result.hasError !== false || result.error ||
        (result.reason !== undefined && result.reason !== 0) || (result.splitCount !== undefined && result.splitCount !== 0) ||
        !positive(result.duration) || typeof result.filePath !== 'string' || !path.isAbsolute(result.filePath) ||
        !/^capture[^\\/]*\.mp4$/i.test(path.basename(result.filePath)) ||
        path.relative(context.root, path.dirname(path.resolve(result.filePath))) !== '' ||
        !insideRecordingDirectory(context.root, path.resolve(result.filePath))) throw recorderError('recording-incomplete');
    if (result.startTimeEpoch !== undefined && (!positive(result.startTimeEpoch) || !Number.isFinite(new Date(result.startTimeEpoch).getTime())))
      throw recorderError('recording-incomplete');
    const file = await realpath(result.filePath);
    const root = await realpath(context.root);
    checkRecordingAbort(signal);
    if (root !== context.root || !insideRecordingDirectory(root, file) || path.relative(root, path.dirname(file)) !== '')
      throw recorderError('recording-path-invalid');
    const metadata = await stat(file);
    if (!metadata.isFile() || metadata.size < 24 || !Number.isSafeInteger(metadata.size) || !await fragmentedMp4(file, signal))
      throw recorderError('recording-media-invalid');
    if (![recorder.ffmpegPath, recorder.ffprobePath].every(value => typeof value === 'string' && path.isAbsolute(value)))
      throw recorderError('recording-tools-unavailable');
    const { stdout } = await run(recorder.ffprobePath, ['-v', 'error', '-show_entries',
      'format=duration,format_name:stream=codec_type,codec_name,width,height,avg_frame_rate,channels,sample_rate', '-of', 'json', file],
    { signal, timeout: 10_000, maxBuffer: 32_768, windowsHide: true });
    checkRecordingAbort(signal);
    const information = JSON.parse(stdout);
    const streams = information.streams;
    const video = streams?.find(value => value.codec_type === 'video');
    const audio = streams?.find(value => value.codec_type === 'audio');
    const duration = numeric(information.format?.duration);
    const fraction = video?.avg_frame_rate?.split('/').map(Number);
    const fps = fraction?.length === 2 ? fraction[0] / fraction[1] : NaN;
    if (!Array.isArray(streams) || streams.length !== 2 || video?.codec_name !== 'h264' || audio?.codec_name !== 'aac' ||
        audio.channels !== 2 || numeric(audio.sample_rate) !== 48000 ||
        video.width !== context.video.outputWidth || video.height !== context.video.outputHeight ||
        !Number.isFinite(fps) || Math.abs(fps - context.video.fps) > 1 || !positive(duration) ||
        Math.abs(duration - result.duration / 1000) > Math.max(2, duration * 0.02) ||
        !information.format?.format_name?.split(',').includes('mp4')) throw recorderError('recording-media-invalid');
    for (const at of [0, duration / 2, Math.max(0, duration - 2)]) {
      checkRecordingAbort(signal);
      const { stderr } = await run(recorder.ffmpegPath, ['-hide_banner', '-nostats', '-v', 'info', '-xerror', '-threads', '2',
        '-ss', String(at), '-i', file, '-map', '0:v:0', '-map', '0:a:0',
        '-vf', 'showinfo=checksum=0', '-af', 'ashowinfo', '-t', '0.2', '-f', 'null', '-'],
      { signal, timeout: 10_000, maxBuffer: 65_536, windowsHide: true });
      if (!decodedBothStreams(stderr)) throw recorderError('recording-media-invalid');
    }
    checkRecordingAbort(signal);
    return { filePath: file, fileSize: metadata.size, durationSeconds: duration, startedAtVerified: result.startTimeEpoch !== undefined,
      ...(result.startTimeEpoch === undefined ? {} : { startedAt: new Date(result.startTimeEpoch).toISOString() }) };
  } catch (error) {
    checkRecordingAbort(signal);
    throw error instanceof RecorderAdapterError ? error : recorderError('recording-media-invalid');
  }
}
