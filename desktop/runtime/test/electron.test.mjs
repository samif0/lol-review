import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { desktopRoot, inspectRuntime, cleanRuntimeEnvironment } from '../electron.mjs';

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
function fixture(root, { pin = '42.7.1', version = '42.7.1', name = 'electron', dependencySection = 'devDependencies' } = {}) {
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({ [dependencySection]: { electron: pin } }));
  const module = path.join(root, 'node_modules/electron');
  mkdirSync(path.join(module, 'dist'), { recursive: true });
  writeFileSync(path.join(module, 'package.json'), JSON.stringify({ name, version, main: 'index.js' }));
  writeFileSync(path.join(module, 'path.txt'), 'electron.exe');
  writeFileSync(path.join(module, 'dist/version'), version);
  writeFileSync(path.join(module, 'dist/electron.exe'), 'synthetic binary, never execute');
  const marker = path.join(root, 'installer-was-loaded');
  writeFileSync(path.join(module, 'index.js'), `require('node:fs').writeFileSync(${JSON.stringify(marker)}, 'bad'); throw new Error('implicit installation');`);
  return { module, marker };
}

test('the desktop root is the folder that holds package.json', () => {
  assert.equal(desktopRoot, path.resolve(fileURLToPath(new URL('../..', import.meta.url))));
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
  writeFileSync(path.join(root, 'package.json'), JSON.stringify({ dependencies: { electron: '42.7.0' } }));
  assert.equal(inspectRuntime({ root, verifyBinary: false }).failure, 'package-version-mismatch');
});
test('a package that is not the stock runtime cannot stand in for it', t => {
  const root = scratch(t); fixture(root, { name: 'electron-fork' });
  assert.equal(inspectRuntime({ root, verifyBinary: false }).failure, 'package-version-mismatch');
});
test('the desktop manifest pins the stock runtime and builder exactly and names no other runtime', () => {
  const manifest = JSON.parse(readFileSync(path.join(desktopRoot, 'package.json'), 'utf8'));
  const all = { ...manifest.dependencies, ...manifest.devDependencies };
  for (const name of ['electron', 'electron-builder']) assert.match(all[name] ?? '', /^\d+\.\d+\.\d+$/, name);
  assert.deepEqual(Object.keys(all).filter(name => name !== 'electron' && name !== 'electron-builder' && /electron$|electron-builder$/.test(name)), []);
  assert.deepEqual(Object.keys(all).filter(name => name.startsWith('@') && !name.startsWith('@electron/')), []);
});
test('production dependency metadata works and nested package pins are irrelevant', t => {
  const root = scratch(t); const { module } = fixture(root, { dependencySection: 'dependencies' });
  const probe = path.join(root, 'tools'); mkdirSync(probe);
  writeFileSync(path.join(probe, 'package.json'), JSON.stringify({ devDependencies: { electron: '0.0.0' } }));
  const result = inspectRuntime({ root, verifyBinary: false });
  assert.equal(result.packageVersion, '42.7.1'); assert.equal(result.executable, path.join(module, 'dist/electron.exe'));
});
test('runtime environment sanitization preserves ordinary app settings and strips case variants', () => {
  const environment = { PATH: 'fixture', REVU_DATA_ROOT: 'fixture-root', node_options: '--require fixture',
    npm_config_electron_mirror: 'fixture', electron_override_dist_path: 'fixture', Electron_Run_As_Node: '1' };
  assert.deepEqual(cleanRuntimeEnvironment(environment), { PATH: 'fixture', REVU_DATA_ROOT: 'fixture-root' });
  assert.equal(environment.node_options, '--require fixture', 'the environment passed in is not modified');
});
