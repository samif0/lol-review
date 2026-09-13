import { readFileSync, writeFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { preflight, probeRoot } from './preflight.mjs';
import { cleanRuntimeEnvironment } from './runtime.mjs';
import { prepareProbe } from './prepare.mjs';
import { acquireLaunchLock, recordChild, releaseLaunchLock, requestShutdown } from './ownership.mjs';

export async function launchProbe({ mode, enableLive = false, readinessSmoke = false, root = probeRoot } = {}) {
  if (!['readiness', 'gep', 'recorder'].includes(mode)) throw new Error('Select readiness, gep, or recorder.');
  const live = mode !== 'readiness';
  if (live && !enableLive) throw new Error('Manual --enable-live is required before gaming packages may activate.');
  if (live && readinessSmoke) throw new Error('Readiness smoke cannot activate gaming packages.');
  const checks = preflight({ root });
  if (checks.platform !== 'win32') throw new Error('Windows is required.');
  if (live && !checks.credentialsUsable) throw new Error('Approved credentials are absent or a partial OW_CLI pair shadows OW_DEV_KEY.');
  const prepared = prepareProbe({ mode, root, runtime: checks.runtime });
  const { lock, environment } = claimPreparedLaunch({ prepared, checks, mode, readinessSmoke });

  return await new Promise((resolve, reject) => {
    let child;
    let finished = false;
    let forced = false;
    let deadline;
    let forceTimer;
    const stop = () => {
      if (finished || forceTimer) return;
      requestShutdown(lock);
      forceTimer = setTimeout(() => { forced = true; child?.kill(); }, 20_000);
    };
    const cleanup = () => {
      clearTimeout(deadline); clearTimeout(forceTimer);
      process.off('SIGINT', stop); process.off('SIGTERM', stop);
    };
    try {
      child = spawn(prepared.executable, [prepared.appDirectory, ...(readinessSmoke ? ['--readiness-smoke'] : [])],
        { cwd: prepared.appDirectory, windowsHide: true, stdio: 'ignore', env: environment });
      child.on('error', () => {
        if (finished) return;
        finished = true; cleanup();
        // ChildProcess also emits errors for failed signals after a successful spawn.
        // Such an error cannot establish that the runtime (or native capture) exited.
        if (!child.pid && lock.childPid === null) releaseLaunchLock(lock, { spawnFailed: true });
        else requestShutdown(lock);
        reject(new Error('Runtime process failed; any ambiguous ownership was preserved.'));
      });
      child.on('exit', (code, signal) => {
        if (finished) return;
        finished = true; cleanup(); releaseLaunchLock(lock, { childExited: true });
        let result = null;
        if (readinessSmoke) {
          try { result = JSON.parse(readFileSync(path.join(prepared.runRoot, 'readiness-result.json'), 'utf8')); } catch { /* Failed bootstrap is reported as failure. */ }
        }
        const smokePassed = !readinessSmoke || (result?.passed === true && result?.appReady === true && result?.rendererLoaded === true
          && Array.isArray(result?.gamingPackagesRequested) && result.gamingPackagesRequested.length === 0);
        resolve({ runRoot: prepared.runRoot, exitCode: code === 0 && !signal && !forced && smokePassed ? 0 : 1,
          runtimePid: child.pid, lockReleased: !readFileExists(lock.lockFile), result });
      });
      if (child.pid) recordChild(lock, child.pid);
      process.on('SIGINT', stop); process.on('SIGTERM', stop);
      if (readinessSmoke) deadline = setTimeout(stop, 45_000);
    } catch {
      cleanup();
      if (child?.pid) child.kill();
      else releaseLaunchLock(lock, { spawnFailed: true });
      reject(new Error('Probe launcher failed; ambiguous ownership was preserved.'));
    }
  });
}

export function claimPreparedLaunch({ prepared, checks, mode, readinessSmoke = false }) {
  const live = mode !== 'readiness';
  // Finish fallible preparation before claiming process ownership. A disk error
  // here cannot strand a lock for a runtime that was never spawned.
  writeFileSync(path.join(prepared.runRoot, 'access-check.json'), JSON.stringify({ ...checks,
    operatorReviewedApproval: live, package: live ? mode : null, approvedAccessDemonstrated: false }, null, 2));
  const environment = cleanRuntimeEnvironment(process.env, { live });
  for (const name of Object.keys(environment)) if (name.startsWith('REVU_PROBE_')) delete environment[name];
  const lock = acquireLaunchLock({ lockFile: prepared.lockFile, mode, runRoot: prepared.runRoot });
  Object.assign(environment, { REVU_PROBE_MODE: mode, REVU_PROBE_ROOT: prepared.runRoot,
    REVU_PROBE_MANUAL_OPT_IN: live ? '1' : '0', REVU_PROBE_READINESS_SMOKE: readinessSmoke ? '1' : '0',
    REVU_PROBE_LAUNCH_TOKEN: lock.token, REVU_PROBE_PARENT_PID: String(process.pid),
    REVU_PROBE_LOCK_FILE: lock.lockFile });
  if (live) environment.REVU_PROBE_EXPECTED_APP_ID = prepared.expectedAppId;
  return { lock, environment };
}

function readFileExists(file) { try { readFileSync(file); return true; } catch (error) { return error.code !== 'ENOENT'; } }

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const outcome = await launchProbe({ mode: process.argv[2], enableLive: process.argv.includes('--enable-live'),
      readinessSmoke: process.argv.includes('--readiness-smoke') });
    console.log(JSON.stringify(outcome, null, 2));
    process.exitCode = outcome.exitCode;
  } catch (error) { console.error(`Probe not launched: ${error.message}`); process.exitCode = 1; }
}
