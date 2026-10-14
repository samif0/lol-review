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

// Top-level boxes from offset to EOF; null for truncation or a box past EOF.
async function walkBoxes(handle, size, offset, signal) {
  const header = Buffer.alloc(16), seen = { type: false, indexes: 0, fragments: 0, media: false };
  for (let count = 0; offset < size && count < 262_144; count++) {
    checkRecordingAbort(signal);
    if (size - offset < 8 || (await handle.read(header, 0, 8, offset)).bytesRead !== 8) return null;
    let bytes = header.readUInt32BE(0), headerBytes = 8;
    const name = header.toString('ascii', 4, 8);
    if (bytes === 1) {
      if (size - offset < 16 || (await handle.read(header, 8, 8, offset + 8)).bytesRead !== 8) return null;
      const extended = header.readBigUInt64BE(8);
      if (extended > BigInt(Number.MAX_SAFE_INTEGER)) return null;
      bytes = Number(extended); headerBytes = 16;
    } else if (bytes === 0) bytes = size - offset;
    if (bytes < headerBytes || bytes > size - offset) return null;
    if (name === 'ftyp') seen.type = true;
    else if (name === 'moov') seen.indexes++;
    else if (name === 'moof') seen.fragments++;
    else if (name === 'mdat' && bytes > headerBytes) seen.media = true;
    offset += bytes;
  }
  return offset === size ? seen : null;
}

/** Top-level MP4 layout and fragment count from a bounded header walk, even for a match-sized mdat payload.
 * 'fragmented': one init moov plus moof/mdat pairs (still recording, interrupted at a fragment boundary, or Overwolf output).
 * 'progressive': one complete moov and no top-level moof (a cleanly finalized hybrid or faststart file).
 * null: anything else, including truncation, a box past EOF, a missing or repeated moov, or no media.
 * Rejects only for open/read failures or abort. */
export async function mp4Summary(file, signal) {
  const handle = await open(file, 'r');
  try {
    const seen = await walkBoxes(handle, (await handle.stat()).size, 0, signal);
    if (!seen?.type || seen.indexes !== 1 || !seen.media) return null;
    return { layout: seen.fragments ? 'fragmented' : 'progressive', fragments: seen.fragments };
  } finally { await handle.close(); }
}
export async function mp4Layout(file, signal) { return (await mp4Summary(file, signal))?.layout ?? null; }

const boxHeader = (bytes, name) => { const value = Buffer.alloc(8); value.writeUInt32BE(bytes); value.write(name, 4, 'latin1'); return value; };
/** A clean hybrid stop appends the complete moov, then rewrites the header after ftyp into one
 * mdat (16 bytes above 4 GiB) that hides the init moov and every fragment. If that moov never
 * reached the disk (power lost before the cache was written, a full disk) nothing can open the
 * file. Only for exactly that signature, and only when the result walks cleanly, put back the
 * recording-time free boxes so the fragments play again. True only after a repair. */
export async function restoreFragmentedLayout(file, signal) {
  let patch;
  const handle = await open(file, 'r');
  try {
    const { size, nlink } = await handle.stat(), head = Buffer.alloc(560);
    const { bytesRead } = await handle.read(head, 0, head.length, 0);
    const name = at => head.toString('latin1', at + 4, at + 8), type = bytesRead >= 8 ? head.readUInt32BE(0) : 0;
    if (nlink !== 1 || name(0) !== 'ftyp' || type < 16 || type > 256 || bytesRead < 2 * type + 24) return false;
    if (head.readUInt32BE(type) === 8 && name(type) === 'free' && name(type + 8) === 'mdat')
      patch = [type + 8, boxHeader(type + 8, 'free')];
    else if (head.readUInt32BE(type) === 1 && name(type) === 'mdat')
      patch = [type, Buffer.concat([boxHeader(8, 'free'), boxHeader(type + 8, 'free')])];
    else return false;
    // Both forms keep the hidden ftyp copy at the same place, followed by the init moov.
    const hidden = type + 16;
    if (!head.subarray(hidden, hidden + type).equals(head.subarray(0, type)) || name(hidden + type) !== 'moov') return false;
    if (await mp4Layout(file, signal)) return false; // the index is there: a finished recording
    const seen = await walkBoxes(handle, size, hidden + type, signal);
    if (!seen || seen.indexes !== 1 || !seen.fragments || !seen.media) return false;
  } finally { await handle.close(); }
  checkRecordingAbort(signal);
  const writer = await open(file, 'r+');
  try { await writer.write(patch[1], 0, patch[1].length, patch[0]); await writer.sync(); } finally { await writer.close(); }
  return await mp4Layout(file, signal) === 'fragmented';
}

// A clean hybrid stop rewrites the header only after appending the index. Persist both
// before the file is linked so a power cut cannot keep the header without the index.
async function persistToDisk(file) {
  let handle;
  try { handle = await open(file, 'r+'); await handle.sync(); }
  catch { /* Best effort: another writer's share mode keeps the cache's own schedule. */ }
  finally { await handle?.close().catch(() => {}); }
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
    if (!metadata.isFile() || metadata.size < 24 || !Number.isSafeInteger(metadata.size)) throw recorderError('recording-media-invalid');
    await persistToDisk(file);
    let container = await mp4Layout(file, signal);
    // A clean stop whose index did not reach the disk still has every complete fragment.
    if (!container && await restoreFragmentedLayout(file, signal)) container = await mp4Layout(file, signal);
    if (!container) throw recorderError('recording-media-invalid');
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
    return { filePath: file, fileSize: metadata.size, durationSeconds: duration, container, startedAtVerified: result.startTimeEpoch !== undefined,
      ...(result.startTimeEpoch === undefined ? {} : { startedAt: new Date(result.startTimeEpoch).toISOString() }) };
  } catch (error) {
    checkRecordingAbort(signal);
    throw error instanceof RecorderAdapterError ? error : recorderError('recording-media-invalid');
  }
}
