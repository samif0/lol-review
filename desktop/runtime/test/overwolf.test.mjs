import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { desktopRoot, inspectRuntime, cleanRuntimeEnvironment } from '../overwolf.mjs';
import * as probeRuntime from '../../overwolf-probes/scripts/runtime.mjs';

function scratch(t) {
  const base = path.resolve(os.tmpdir());
  const root = mkdtempSync(path.join(base, 'Revu.SharedRuntime.Tests.'));
  t.after(() => {
    assert.equal(path.dirname(root), base);
    assert.ok(path.basename(root).startsWith('Revu.SharedRuntime.Tests.'));
    rmSync(root, { recursive: true, force: true });
  });
  return root;
}
function fixture(root, { pin = '42.7.1', version = '42.7.1', dependencySection = 'devDependencies' } = {}) {
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({ [dependencySection]: { '@overwolf/ow-electron': pin } }));
  const module = path.join(root, 'node_modules/@overwolf/ow-electron');
  mkdirSync(path.join(module, 'dist'), { recursive: true });
  writeFileSync(path.join(module, 'package.json'), JSON.stringify({ name: '@overwolf/ow-electron', version, owElectronVersion: version, main: 'index.js' }));
  writeFileSync(path.join(module, 'path.txt'), 'electron.exe');
  writeFileSync(path.join(module, 'dist/version'), version);
  writeFileSync(path.join(module, 'dist/electron.exe'), 'synthetic binary, never execute');
  const marker = path.join(root, 'installer-was-loaded');
  writeFileSync(path.join(module, 'index.js'), `require('node:fs').writeFileSync(${JSON.stringify(marker)}, 'bad'); throw new Error('implicit installation');`);
  return { module, marker };
}

test('host and probe resolve one shared install/environment helper', () => {
  assert.equal(desktopRoot, path.resolve(fileURLToPath(new URL('../..', import.meta.url))));
  assert.equal(probeRuntime.desktopRoot, desktopRoot);
  assert.equal(probeRuntime.inspectRuntime, inspectRuntime);
  assert.equal(probeRuntime.cleanRuntimeEnvironment, cleanRuntimeEnvironment);
  assert.equal(probeRuntime.probeRoot, path.join(desktopRoot, 'overwolf-probes'));
});
test('read-only inspection resolves package metadata without loading the installer entry', t => {
  const root = scratch(t); const { module, marker } = fixture(root);
  const inspected = inspectRuntime({ root, verifyBinary: false });
  assert.equal(inspected.pinnedVersion, '42.7.1');
  assert.equal(inspected.executable, path.join(module, 'dist/electron.exe'));
  assert.equal(inspected.failure, 'binary-not-executed');
  assert.equal(inspected.installed, false); assert.equal(existsSync(marker), false);
});
test('root pin is required and mismatched package cannot be silently used', t => {
  const root = scratch(t); fixture(root, { pin: '^42.7.1' });
  assert.equal(inspectRuntime({ root, verifyBinary: false }).failure, 'runtime-pin-required');
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({ dependencies: { '@overwolf/ow-electron': '42.7.0' } }));
  assert.equal(inspectRuntime({ root, verifyBinary: false }).failure, 'package-version-mismatch');
});
test('production dependency metadata works and nested probe package pins are irrelevant', t => {
  const root = scratch(t); const { module } = fixture(root, { dependencySection: 'dependencies' });
  const probe = path.join(root, 'overwolf-probes'); mkdirSync(probe);
  writeFileSync(path.join(probe, 'package.json'), JSON.stringify({ devDependencies: { '@overwolf/ow-electron': '0.0.0' } }));
  const result = inspectRuntime({ root, verifyBinary: false });
  assert.equal(result.packageVersion, '42.7.1'); assert.equal(result.executable, path.join(module, 'dist/electron.exe'));
});
test('runtime environment sanitization preserves ordinary app settings and strips case variants', () => {
  const environment = { PATH: 'fixture', REVU_DATA_ROOT: 'fixture-root', node_options: '--require fixture',
    npm_config_electron_mirror: 'fixture', electron_override_dist_path: 'fixture', ow_dev_key: 'fixture-secret' };
  assert.deepEqual(cleanRuntimeEnvironment(environment), { PATH: 'fixture', REVU_DATA_ROOT: 'fixture-root' });
  assert.equal(environment.ow_dev_key, 'fixture-secret');
});
