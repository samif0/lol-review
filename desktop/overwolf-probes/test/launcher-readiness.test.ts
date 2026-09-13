import { test } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { cleanRuntimeEnvironment, credentialStatus, inspectRuntime, probeRoot } from '../scripts/runtime.mjs';
import { prepareProbe, validateLiveAccess } from '../scripts/prepare.mjs';
import { ensureAccessTemplate } from '../scripts/setup.mjs';
import { acquireLaunchLock, readLock, recordChild, releaseLaunchLock } from '../scripts/ownership.mjs';
import { recoverProbeLock } from '../scripts/recover-lock.mjs';
import { claimPreparedLaunch } from '../scripts/launch.mjs';
import { preflight } from '../scripts/preflight.mjs';

function scratch(t: { after: (callback: () => void) => void }) {
  const base = path.resolve(os.tmpdir());
  const root = mkdtempSync(path.join(base, 'Revu.Probe.Readiness.Tests.'));
  t.after(() => {
    assert.equal(path.dirname(root), base);
    assert.ok(path.basename(root).startsWith('Revu.Probe.Readiness.Tests.'));
    rmSync(root, { recursive: true, force: true });
  });
  return root;
}

test('developer-key precedence is diagnosed without returning credential values', () => {
  const secret = 'synthetic-key-never-a-real-credential';
  assert.deepEqual(credentialStatus({}), { presence: { OW_CLI_EMAIL: false, OW_CLI_API_KEY: false, OW_DEV_KEY: false },
    method: 'absent', usable: false, developerKeyShadowed: false });
  assert.equal(credentialStatus({ OW_DEV_KEY: secret }).method, 'developer-key');
  const incomplete = credentialStatus({ OW_DEV_KEY: secret, OW_CLI_EMAIL: 'synthetic@example.invalid' });
  assert.equal(incomplete.usable, false);
  assert.equal(incomplete.developerKeyShadowed, true);
  const pair = credentialStatus({ OW_DEV_KEY: secret, OW_CLI_EMAIL: 'synthetic@example.invalid', OW_CLI_API_KEY: secret });
  assert.equal(pair.method, 'console-pair');
  assert.equal(pair.usable, true);
  assert.equal(JSON.stringify(pair).includes(secret), false);
});

test('no-key helpers strip developer keys, runtime overrides, and injected node options', () => {
  const source = { PATH: 'synthetic-path', OW_DEV_KEY: 'synthetic-key', OW_CLI_EMAIL: 'synthetic-email',
    OW_CLI_API_KEY: 'synthetic-console', OVERWOLF_APP_UID: 'old-uid', ELECTRON_OVERRIDE_DIST_PATH: 'wrong-runtime',
    ELECTRON_RUN_AS_NODE: '1', NODE_OPTIONS: '--require injected.js', NODE_PATH: 'injected-modules' };
  assert.deepEqual(cleanRuntimeEnvironment(source), { PATH: 'synthetic-path' });
  const live = cleanRuntimeEnvironment(source, { live: true });
  assert.equal(live.OW_DEV_KEY, source.OW_DEV_KEY);
  assert.equal(live.ELECTRON_RUN_AS_NODE, undefined);
  assert.equal(source.NODE_OPTIONS, '--require injected.js');
});

test('path.txt alone and an invalid binary version cannot claim a runtime is installed', t => {
  const root = scratch(t);
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({ devDependencies: { '@overwolf/ow-electron': '42.7.1' } }));
  const runtime = path.join(root, 'node_modules/@overwolf/ow-electron');
  mkdirSync(path.join(runtime, 'dist'), { recursive: true });
  writeFileSync(path.join(runtime, 'package.json'), JSON.stringify({ version: '42.7.1', owElectronVersion: '42.7.1' }));
  writeFileSync(path.join(runtime, 'path.txt'), 'electron.exe');
  assert.equal(inspectRuntime({ root }).installed, false);
  writeFileSync(path.join(runtime, 'dist/electron.exe'), 'synthetic non-executable');
  writeFileSync(path.join(runtime, 'dist/version'), '42.7.0');
  assert.equal(inspectRuntime({ root }).failure, 'version-file-mismatch');
  writeFileSync(path.join(runtime, 'dist/version'), '42.7.1');
  assert.equal(inspectRuntime({ root, verifyBinary: false }).installed, false);
});

test('probe preflight reads the desktop install manifest without a nested probe package', t => {
  const runtimeRoot = scratch(t);
  const root = path.join(runtimeRoot, 'overwolf-probes'); mkdirSync(root);
  writeFileSync(path.join(runtimeRoot, 'package.json'), JSON.stringify({ devDependencies: {
    '@overwolf/ow-electron': '42.7.1', '@overwolf/ow-electron-packages-types': '1.1.11' } }));
  const checked = preflight({ root, runtimeRoot });
  assert.equal(checked.runtimePackage, '42.7.1'); assert.equal(checked.typesPackage, '1.1.11');
  assert.equal(checked.runtimeBinaryInstalled, false); assert.equal(checked.compiledEntryPresent, false);
  assert.equal(existsSync(path.join(root, 'package.json')), false);
});

test('setup writes only pending access metadata and never replaces reviewed local settings', t => {
  const root = scratch(t);
  const first = ensureAccessTemplate(root);
  assert.equal(first.created, true);
  const template = JSON.parse(readFileSync(first.accessPath, 'utf8'));
  assert.equal(template.status, 'pending');
  assert.equal(template.appId, '');
  assert.equal(template.recorderAccess, false);
  assert.throws(() => validateLiveAccess(template, 'recorder'));
  writeFileSync(first.accessPath, '{"local":"existing-value"}');
  assert.equal(ensureAccessTemplate(root).created, false);
  assert.equal(readFileSync(first.accessPath, 'utf8'), '{"local":"existing-value"}');
});

test('prepared no-key app contains relative entry, compiled modules and renderer resources', t => {
  const root = scratch(t);
  mkdirSync(path.join(root, 'dist'));
  mkdirSync(path.join(root, 'scripts'));
  writeFileSync(path.join(root, 'dist/main.js'), "import './helper.js';\n");
  writeFileSync(path.join(root, 'dist/helper.js'), 'export const fixture = true;\n');
  writeFileSync(path.join(root, 'probe.html'), '<!doctype html><title>Synthetic readiness</title>');
  cpSync(path.join(probeRoot, 'scripts/ownership.mjs'), path.join(root, 'scripts/ownership.mjs'));
  const prepared = prepareProbe({ root, mode: 'readiness', access: { status: 'pending', unknownSecret: 'must-not-copy' },
    runtime: { installed: true, executable: 'synthetic-runtime-not-launched', binaryVersion: '42.7.1', binarySha256: 'fixture' } });
  const app = JSON.parse(readFileSync(path.join(prepared.appDirectory, 'package.json'), 'utf8'));
  assert.equal(app.main, 'dist/main.js');
  assert.equal(path.isAbsolute(app.main), false);
  assert.deepEqual(app.overwolf.packages, []);
  for (const resource of ['dist/main.js', 'dist/helper.js', 'probe.html', 'scripts/ownership.mjs'])
    assert.equal(existsSync(path.join(prepared.appDirectory, resource)), true);
  assert.equal(JSON.stringify(prepared).includes('must-not-copy'), false);
  assert.equal(readFileSync(path.join(prepared.runRoot, 'capture-settings.json'), 'utf8'), '{}');
  assert.equal(prepared.approvedAccessDemonstrated, false);
});

test('launch lock survives launcher-only cleanup while an actual runtime child is alive', async t => {
  const root = scratch(t);
  const lockFile = path.join(root, 'readiness.lock');
  const lock = acquireLaunchLock({ lockFile, mode: 'readiness', runRoot: root });
  const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 150)'], {
    windowsHide: true, stdio: 'ignore', env: cleanRuntimeEnvironment(),
  });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  recordChild(lock, child.pid);
  assert.equal(readLock(lockFile).childPid, child.pid);
  assert.equal(releaseLaunchLock(lock), false);
  assert.equal(releaseLaunchLock(lock, { spawnFailed: true }), false);
  assert.throws(() => acquireLaunchLock({ lockFile, mode: 'readiness', runRoot: root }));
  await once(child, 'exit');
  assert.equal(releaseLaunchLock(lock, { childExited: true }), true);
  assert.equal(existsSync(lockFile), false);
});

test('a diagnostics write failure before spawn does not acquire a mode lock', t => {
  const root = scratch(t);
  const lockFile = path.join(root, 'recorder.lock');
  mkdirSync(path.join(root, 'access-check.json'));
  assert.throws(() => claimPreparedLaunch({ prepared: { runRoot: root, lockFile }, checks: {}, mode: 'recorder' }));
  assert.equal(existsSync(lockFile), false);
});

test('a confirmed spawn failure releases a Recorder lock without a native session summary', async t => {
  const root = scratch(t);
  const lock = acquireLaunchLock({ lockFile: path.join(root, 'recorder.lock'), mode: 'recorder', runRoot: root });
  const child = spawn(path.join(root, 'absent-runtime.exe'), [], { windowsHide: true, stdio: 'ignore' });
  await once(child, 'error');
  assert.equal(child.pid, undefined);
  assert.equal(lock.childPid, null);
  assert.equal(releaseLaunchLock(lock, { spawnFailed: true }), true);
  assert.equal(existsSync(lock.lockFile), false);
});

test('changed ownership and active recording evidence keep locks after a child exits', t => {
  const root = scratch(t);
  const lock = acquireLaunchLock({ lockFile: path.join(root, 'recorder.lock'), mode: 'recorder', runRoot: root });
  writeFileSync(path.join(root, 'session.json'), '{"state":"recording"}');
  assert.equal(releaseLaunchLock(lock, { childExited: true }), false);
  writeFileSync(path.join(root, 'session.json'), '{"state":"finalized"}');
  const changed = { ...readLock(lock.lockFile), token: 'different-owner' };
  writeFileSync(lock.lockFile, JSON.stringify(changed));
  assert.equal(releaseLaunchLock(lock, { childExited: true }), false);
  assert.equal(readLock(lock.lockFile).token, 'different-owner');
});

test('partial Recorder state needs a verifiable healthy summary before lock release', t => {
  const root = scratch(t);
  const lock = acquireLaunchLock({ lockFile: path.join(root, 'recorder.lock'), mode: 'recorder', runRoot: root });
  writeFileSync(path.join(root, 'session.json'), '{"state":"partial"}');
  assert.equal(releaseLaunchLock(lock, { childExited: true }), false);
  const healthy = { mode: 'recorder', captureMayBeActive: false, persistenceHealthy: true, storageHealthy: true };
  for (const summary of [{ ...healthy, captureMayBeActive: true }, { ...healthy, persistenceHealthy: false },
    { ...healthy, storageHealthy: false }]) {
    writeFileSync(path.join(root, 'run-result.json'), JSON.stringify(summary));
    assert.equal(releaseLaunchLock(lock, { childExited: true }), false);
  }
  writeFileSync(path.join(root, 'run-result.json'), JSON.stringify(healthy));
  assert.equal(releaseLaunchLock(lock, { childExited: true }), true);
});

test('recovery refuses live/unknown owners and clears only two recorded exited processes', async t => {
  const root = scratch(t);
  const runRoot = path.join(root, '.local/runs/synthetic-run');
  mkdirSync(runRoot, { recursive: true });
  const lock = acquireLaunchLock({ lockFile: path.join(root, '.local/readiness.lock'), mode: 'readiness', runRoot });
  assert.throws(() => recoverProbeLock({ mode: 'readiness', root }));
  const exitedPid = async () => {
    const child = spawn(process.execPath, ['-e', ''], { windowsHide: true, stdio: 'ignore', env: cleanRuntimeEnvironment() });
    const pid = child.pid;
    await once(child, 'exit');
    return pid;
  };
  const launcherPid = await exitedPid();
  writeFileSync(lock.lockFile, JSON.stringify({ ...readLock(lock.lockFile), launcherPid }));
  assert.throws(() => recoverProbeLock({ mode: 'readiness', root })); // No recorded child is ambiguous.
  const childPid = await exitedPid();
  writeFileSync(lock.lockFile, JSON.stringify({ ...readLock(lock.lockFile), childPid }));
  assert.equal(recoverProbeLock({ mode: 'readiness', root }).recovered, true);
  assert.equal(existsSync(lock.lockFile), false);
});
