import { readFileSync, realpathSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { probeRoot } from './runtime.mjs';
import { releaseLaunchLock } from './ownership.mjs';

function confirmedDead(pid) {
  if (!Number.isInteger(pid) || pid <= 0 || pid > 2147483647) return false;
  try { process.kill(pid, 0); return false; }
  catch (error) { return error.code === 'ESRCH'; } // EPERM and all unknown results preserve ownership.
}

export function recoverProbeLock({ mode, root = probeRoot }) {
  if (!['readiness', 'gep', 'recorder'].includes(mode)) throw new Error('Select readiness, gep, or recorder.');
  const lockFile = path.join(root, '.local', `${mode}.lock`);
  let record;
  try { record = JSON.parse(readFileSync(lockFile, 'utf8')); }
  catch { throw new Error('No verifiable lock record exists; do not guess its owner.'); }
  const runsDirectory = path.resolve(root, '.local', 'runs');
  if (record.formatVersion !== 1 || record.mode !== mode || !/^[a-f0-9-]{36}$/i.test(record.token ?? '')
      || typeof record.runRoot !== 'string' || !path.isAbsolute(record.runRoot)
      || !path.resolve(record.runRoot).startsWith(runsDirectory + path.sep))
    throw new Error('The lock record is malformed or names an unexpected output directory.');
  if (realpathSync(record.runRoot).toLowerCase() !== path.resolve(record.runRoot).toLowerCase())
    throw new Error('Linked output directories require manual inspection.');
  if (!confirmedDead(record.launcherPid) || !confirmedDead(record.childPid))
    throw new Error('Both recorded owners must be confirmed dead; live, inaccessible, or missing PIDs stay locked.');
  if (!releaseLaunchLock({ ...record, lockFile }, { childExited: true }))
    throw new Error('Capture activity, storage health, or ownership remains uncertain; recovery was refused.');
  return { recovered: true, mode, runRoot: record.runRoot, reason: 'both-recorded-owners-exited' };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(JSON.stringify(recoverProbeLock({ mode: process.argv[2] }), null, 2)); }
  catch (error) { console.error(`Lock preserved: ${error.message}`); process.exitCode = 1; }
}
