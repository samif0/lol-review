import { closeSync, existsSync, mkdirSync, openSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

export function readLock(lockFile) {
  try { return JSON.parse(readFileSync(lockFile, 'utf8')); } catch { return null; }
}

export function acquireLaunchLock({ lockFile, mode, runRoot, launcherPid = process.pid }) {
  mkdirSync(path.dirname(lockFile), { recursive: true });
  const record = { formatVersion: 1, token: randomUUID(), mode, runRoot, launcherPid,
    childPid: null, createdAt: new Date().toISOString() };
  let fd;
  try { fd = openSync(lockFile, 'wx'); }
  catch { throw new Error('This mode is already owned or needs explicit recovery; its lock was preserved.'); }
  try { writeFileSync(fd, JSON.stringify(record)); }
  finally { closeSync(fd); }
  return { lockFile, ...record };
}

export function recordChild(lock, childPid) {
  if (!Number.isInteger(childPid) || childPid < 1) throw new Error('Runtime child PID is invalid.');
  const previous = readLock(lock.lockFile);
  if (previous?.token !== lock.token) throw new Error('Launch ownership changed; the existing lock was preserved.');
  const temporary = `${lock.lockFile}.${lock.token}.tmp`;
  try {
    writeFileSync(temporary, JSON.stringify({ ...previous, childPid }), { flag: 'wx' });
    renameSync(temporary, lock.lockFile);
    lock.childPid = childPid;
  } finally { if (existsSync(temporary)) unlinkSync(temporary); }
}

export function releaseLaunchLock(lock, { childExited = false, spawnFailed = false } = {}) {
  // A launcher exit is not a child exit. Retain ambiguous/orphan ownership.
  if (!childExited && !spawnFailed) return false;
  const record = readLock(lock.lockFile);
  if (record?.token !== lock.token || record.launcherPid !== lock.launcherPid) return false;
  if (record.childPid !== lock.childPid) return false;
  if (spawnFailed && record.childPid !== null) return false;
  if (lock.mode === 'recorder' && !spawnFailed) {
    // A partial/failed file does not prove native recording stopped. Only the
    // child's final summary can establish healthy persistence and no active capture.
    try {
      const summary = JSON.parse(readFileSync(path.join(lock.runRoot, 'run-result.json'), 'utf8'));
      if (summary.mode !== 'recorder' || summary.captureMayBeActive !== false
          || summary.persistenceHealthy !== true || summary.storageHealthy !== true) return false;
    } catch { return false; }
    try {
      const session = JSON.parse(readFileSync(path.join(lock.runRoot, 'session.json'), 'utf8'));
      if (['starting', 'recording', 'stopping'].includes(session.state)) return false;
    } catch (error) { if (error.code !== 'ENOENT') return false; }
  }
  unlinkSync(lock.lockFile);
  return true;
}

export function requestShutdown(lock) {
  if (readLock(lock.lockFile)?.token !== lock.token) return false;
  try { writeFileSync(path.join(lock.runRoot, 'shutdown-request.json'), JSON.stringify({ token: lock.token })); return true; }
  catch { return false; }
}
