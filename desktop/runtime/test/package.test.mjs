import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import asar from '@electron/asar';
import { activatePackage, isAppFile, stageApp, verifyPackage } from '../../scripts/package.mjs';

async function scratch(t) {
  const base = path.resolve(os.tmpdir());
  const root = await mkdtemp(path.join(base, 'Revu.Package.Tests.'));
  t.after(async () => {
    assert.equal(path.dirname(root), base);
    assert.ok(path.basename(root).startsWith('Revu.Package.Tests.'));
    await rm(root, { recursive: true, force: true });
  });
  return root;
}
async function file(root, name, value = 'fixture') {
  const target = path.join(root, name);
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, value);
}
async function fixture(root) {
  for (const name of ['electron/main.mjs', 'electron/preload.cjs', 'electron/branding/revu.ico',
    'runtime/overwolf.mjs', 'ui/index.html', 'ui/app.js', 'ui/platform/index.mjs',
    'ui/fonts/Inter.ttf', 'ui/sample-dashboard.json']) await file(root, name);
  await file(root, 'package.json', JSON.stringify({ name: 'revu-desktop', version: '3.10.1',
    main: 'electron/main.mjs', type: 'module', overwolf: { packages: [] } }));
}

test('staging copies the canonical app and rejects tests, output, private files and dependency trees', async t => {
  const root = await scratch(t), source = path.join(root, 'source'), stage = path.join(root, 'app');
  await fixture(source);
  for (const name of ['electron/test/host.test.mjs', 'electron/host.test.mjs', 'electron/private.local.mjs',
    'electron/.local/credentials.json', 'electron/package.json', 'ui/dist/index.html', 'ui/.env',
    'ui/private.json', 'ui/node_modules/test.js', 'runtime/test/host.test.mjs',
    'overwolf-probes/src/main.ts', 'node_modules/dependency/index.js']) await file(source, name, 'must not ship');
  const copied = await stageApp(source, stage);
  assert.ok(copied.includes('ui/fonts/Inter.ttf'));
  assert.ok(copied.includes('ui/sample-dashboard.json'));
  assert.equal(copied.some(name => /test|private|credentials|node_modules|dist|overwolf-probes/.test(name)), false);
  assert.deepEqual((await readdir(path.join(stage, 'electron'))).sort(), ['branding', 'main.mjs', 'preload.cjs']);
  assert.equal(await readFile(path.join(stage, 'ui/app.js'), 'utf8'), 'fixture');
});

test('missing entry files and package activation fail staging', async t => {
  const root = await scratch(t), source = path.join(root, 'source');
  await fixture(source);
  await file(source, 'package.json', JSON.stringify({ main: 'electron/main.mjs', overwolf: { packages: ['gep'] } }));
  await assert.rejects(stageApp(source, path.join(root, 'bad-package')), /gaming package activation/);
  await rm(path.join(source, 'electron/main.mjs'));
  await assert.rejects(stageApp(source, path.join(root, 'missing-main')), /Missing required app file/);
});

test('a junction inside an otherwise allowed source directory cannot escape staging', async t => {
  const root = await scratch(t), source = path.join(root, 'source'), secret = path.join(root, 'private');
  await fixture(source); await file(secret, 'revu.ico', 'private');
  await rm(path.join(source, 'electron/branding'), { recursive: true });
  await symlink(secret, path.join(source, 'electron/branding'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(stageApp(source, path.join(root, 'app')), /unaliased regular file/);
});

test('package rotation retains prior output and never accepts an outside path', async t => {
  const root = await scratch(t), output = path.join(root, 'output'), built = path.join(output, '.build-first/output/win-unpacked');
  await file(built, 'generation', 'new');
  await file(output, 'win-unpacked/generation', 'old');
  const active = await activatePackage(output, built);
  assert.equal(await readFile(path.join(active, 'generation'), 'utf8'), 'new');
  assert.equal(await readFile(path.join(output, 'previous-.build-first/generation'), 'utf8'), 'old');
  const outside = path.join(root, 'outside/win-unpacked'); await mkdir(outside, { recursive: true });
  await assert.rejects(activatePackage(output, outside), /outside build paths/);
  assert.equal(await readFile(path.join(active, 'generation'), 'utf8'), 'new');
});

test('package rotation refuses an existing output junction without touching its target', async t => {
  const root = await scratch(t), output = path.join(root, 'output'), privateRoot = path.join(root, 'private');
  const built = path.join(output, '.build-first/output/win-unpacked');
  await file(built, 'generation', 'new'); await file(privateRoot, 'generation', 'private');
  await symlink(privateRoot, path.join(output, 'win-unpacked'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(activatePackage(output, built), /not a real directory/);
  assert.equal(await readFile(path.join(privateRoot, 'generation'), 'utf8'), 'private');
});

test('layout verification requires self-contained sidecar next to the executable and clean app files', async t => {
  const root = await scratch(t);
  const source = path.join(root, 'source'), packaged = path.join(root, 'package');
  await fixture(source);
  for (const name of ['revu-desktop.exe', 'Revu.Sidecar.exe', 'Revu.Sidecar.dll',
    'Revu.Sidecar.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'resources/branding/revu.ico']) await file(packaged, name);
  const archive = path.join(packaged, 'resources/app.asar');
  await asar.createPackage(source, archive);
  await verifyPackage(packaged);
  await file(source, 'ui/private.json');
  await asar.createPackage(source, archive);
  await assert.rejects(verifyPackage(packaged), /Unexpected packaged app file/);
  await rm(path.join(source, 'ui/private.json'));
  await asar.createPackage(source, archive);
  await rm(path.join(packaged, 'coreclr.dll'));
  await assert.rejects(verifyPackage(packaged), /ENOENT/);
});

test('file policy excludes traversal and alternate separators', () => {
  for (const name of ['../ui/index.html', 'ui/../package.json', 'ui\\index.html',
    'ui//index.html', 'ui/.local.js', 'electron/bridge.spec.mjs']) assert.equal(isAppFile(name), false, name);
});
