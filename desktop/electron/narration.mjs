import { randomUUID } from 'node:crypto';
import { mkdir, rename, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';

// Narration save channel (C7). The renderer hands over the raw voice take; main
// validates it, writes it atomically under <dataRoot>/Revu/Narration and asks the
// sidecar to render the narrated clip. The renderer never names a file path.
export const NARRATION_MIN_BYTES = 1024;
export const NARRATION_MAX_BYTES = 33554432;
export const NARRATION_MIN_DURATION_MS = 1000;
export const NARRATION_MAX_DURATION_MS = 625000;
export const NARRATION_IPC_TIMEOUT_MS = 280_000;
// The sidecar answered and refused the narration; its file will never be referenced.
export const DEFINITIVE_REJECTIONS = new Set([400, 404, 409, 422, 503]);

const META_KEYS = Object.freeze(['gameId', 'bookmarkId', 'mimeType', 'offsetMs', 'durationMs', 'gameVolume', 'narrationVolume', 'duck']);
const EBML_MAGIC = [0x1A, 0x45, 0xDF, 0xA3];

const integerIn = (value, min, max) => Number.isSafeInteger(value) && value >= min && value <= max;
const numberIn = (value, min, max) => typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max;

export function validateNarrationMeta(meta) {
  if (!meta || typeof meta !== 'object' || Array.isArray(meta)) throw new TypeError('Invalid narration details');
  const keys = Object.keys(meta);
  if (keys.length !== META_KEYS.length || !META_KEYS.every(key => Object.hasOwn(meta, key)))
    throw new TypeError('Invalid narration details');
  const { gameId, bookmarkId, mimeType, offsetMs, durationMs, gameVolume, narrationVolume, duck } = meta;
  if (!integerIn(gameId, 1, Number.MAX_SAFE_INTEGER)) throw new TypeError('Invalid narration game');
  if (!integerIn(bookmarkId, 1, Number.MAX_SAFE_INTEGER)) throw new TypeError('Invalid narration clip');
  if (typeof mimeType !== 'string' || !mimeType.startsWith('audio/webm') || mimeType.length > 64)
    throw new TypeError('Invalid narration format');
  if (!integerIn(offsetMs, -10000, 10000)) throw new TypeError('Invalid narration sync');
  if (!integerIn(durationMs, NARRATION_MIN_DURATION_MS, NARRATION_MAX_DURATION_MS)) throw new TypeError('Invalid narration length');
  if (!numberIn(gameVolume, 0, 1.5) || !numberIn(narrationVolume, 0, 2)) throw new TypeError('Invalid narration volume');
  if (typeof duck !== 'boolean') throw new TypeError('Invalid narration ducking');
  return Object.freeze({ gameId, bookmarkId, mimeType, offsetMs, durationMs, gameVolume, narrationVolume, duck });
}

// Accepts bytes from any realm (IPC structured clone, vm contexts): a one-byte
// typed view or an ArrayBuffer. Returns a Buffer over an owned copy.
export function toNarrationBuffer(bytes) {
  let buffer;
  if (ArrayBuffer.isView(bytes) && bytes.BYTES_PER_ELEMENT === 1) {
    buffer = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  } else if (bytes instanceof ArrayBuffer || Object.prototype.toString.call(bytes) === '[object ArrayBuffer]') {
    buffer = Buffer.from(bytes);
  } else throw new TypeError('Invalid narration audio');
  if (buffer.length < NARRATION_MIN_BYTES || buffer.length > NARRATION_MAX_BYTES) throw new RangeError('Invalid narration size');
  if (!EBML_MAGIC.every((byte, index) => buffer[index] === byte)) throw new TypeError('Invalid narration audio');
  return Buffer.from(buffer); // detach from the caller's memory before any await
}

export async function writeNarrationFile(root, buffer) {
  await mkdir(root, { recursive: true });
  const id = randomUUID();
  const file = path.join(root, `${id}.webm`);
  const part = `${file}.part`;
  try {
    await writeFile(part, buffer, { flag: 'wx' });
    await rename(part, file);
  } catch (error) {
    await unlink(part).catch(() => {});
    throw error;
  }
  return { id, file };
}

// A timeout or lost response keeps the file: the sidecar may still have accepted
// it, and its sweep removes unreferenced voice files after 24 hours.
export async function saveNarration({ root, backend, bytes, meta }) {
  const valid = validateNarrationMeta(meta);
  const buffer = toNarrationBuffer(bytes);
  const { id, file } = await writeNarrationFile(root, buffer);
  try {
    return await backend.request('/api/clip/narration/save', {
      method: 'POST', body: { ...valid, narrationId: id }, timeoutMs: NARRATION_IPC_TIMEOUT_MS,
    });
  } catch (e) {
    if (DEFINITIVE_REJECTIONS.has(e?.status)) await unlink(file).catch(() => {});
    throw e;
  }
}
