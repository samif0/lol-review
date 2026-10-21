import { randomUUID } from 'node:crypto';
import { lstat, readFile, rename, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';

const presets = new Set(['720p30', '1080p30', '1080p60']);
const faultCode = value => typeof value === 'string' && /^[a-z0-9-]{1,64}$/.test(value);
export const RECORDING_DEFAULTS = Object.freeze({ enabled: false, preset: '720p30' });

export function recordingSettings(value) {
  if (!value || Object.keys(value).some(key => !['enabled', 'preset'].includes(key))
      || typeof value.enabled !== 'boolean' || !presets.has(value.preset)) throw new Error('Invalid recording settings');
  return { enabled: value.enabled, preset: value.preset };
}

export async function atomicJson(file, value) {
  const temporary = `${file}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporary, JSON.stringify(value, null, 2), { flag: 'wx', flush: true });
    await rename(temporary, file);
  } catch (error) { await unlink(temporary).catch(() => {}); throw error; }
}
export async function bounded(operation, milliseconds, code = 'recording-timeout') {
  let timer;
  try { return await Promise.race([operation, new Promise((_, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error(code), { code })), milliseconds);
  })]); } finally { clearTimeout(timer); }
}

/** Why the host turned recording off after a native fault. It lives beside the
 * settings file, so older versions still read their settings unchanged. */
export const recordingFault = (code, at) => ({ schema: 1, code: faultCode(code) ? code : 'recorder-unavailable', at });
export async function readRecordingFault(profile) {
  try {
    const file = path.join(profile, 'recording-fault.json');
    if ((await lstat(file)).size > 4096) return null;
    const fault = JSON.parse(await readFile(file, 'utf8'));
    return fault?.schema === 1 && faultCode(fault.code) ? fault : null;
  } catch { return null; } // Informational: a missing or unreadable reason shows the plain off state.
}
/** Best effort: the reason is informational and never blocks the settings write. */
export async function writeRecordingFault(profile, fault) {
  const file = path.join(profile, 'recording-fault.json');
  await (fault ? atomicJson(file, fault) : unlink(file)).catch(() => {});
}
