import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import asar from '@electron/asar';
import builderConfig from '../../electron-builder.config.mjs';
import { activatePackage, bundledFonts, isAppFile, readFuseWire, requiredFuses, stageApp, verifyFuses, verifyPackage } from '../../scripts/package.mjs';
import { encoderBinaryPins, encoderDependencyProvenance, isRecorderFile, obsPin, recorderFiles, recorderManifest,
  recorderModules, recorderSourceLock, requiredRecorderFiles, verifyEncoderBinaryPins, verifyEncoderSourceProvenance,
  verifyRecorderRuntime } from '../../recorder/runtime/libobs.mjs';
import { writeSourceZip } from '../../recorder/scripts/source-zip.mjs';
import { execFileSync } from 'node:child_process';

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
// A stand-in executable carrying an Electron v1 fuse wire (states are ASCII '0'/'1').
function fusedExecutable(states = '00001100') {
  return Buffer.concat([Buffer.from('MZ fixture '), Buffer.from('dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX', 'latin1'),
    Buffer.from([1, states.length]), Buffer.from(states, 'latin1'), Buffer.from(' trailer')]);
}
const FONT_FILES = [...bundledFonts, 'ui/fonts/OFL-SourceSerif4.txt', 'ui/fonts/OFL-Geist.txt'];
async function fixture(root) {
  for (const name of ['electron/main.mjs', 'electron/preload.cjs', 'electron/libobs-recorder.mjs', 'electron/branding/revu.ico',
    'runtime/overwolf.mjs', 'ui/index.html', 'ui/app.js', 'ui/appearance.css', 'ui/platform/index.mjs',
    'ui/sample-dashboard.json', ...FONT_FILES]) await file(root, name);
  await file(root, 'package.json', JSON.stringify({ name: 'revu-desktop', version: '3.10.1',
    main: 'electron/main.mjs', type: 'module', overwolf: { packages: [] } }));
}

test('staging copies the canonical app and rejects tests, output, private files and dependency trees', async t => {
  const root = await scratch(t), source = path.join(root, 'source'), stage = path.join(root, 'app');
  await fixture(source);
  for (const name of ['electron/test/host.test.mjs', 'electron/host.test.mjs', 'electron/private.local.mjs',
    'electron/.local/credentials.json', 'electron/package.json', 'ui/dist/index.html', 'ui/.env',
    'ui/private.json', 'ui/node_modules/test.js', 'ui/fonts/Inter.ttf', 'ui/fonts/notes.md', 'ui/fonts/Inter.woff',
    'ui/fonts/extra/x.woff2', 'runtime/test/host.test.mjs',
    'overwolf-probes/src/main.ts', 'node_modules/dependency/index.js']) await file(source, name, 'must not ship');
  const copied = await stageApp(source, stage);
  // The font folder ships exactly the bundled woff2 faces and their license texts.
  assert.deepEqual(copied.filter(name => name.startsWith('ui/fonts/')).sort(), [...FONT_FILES].sort());
  assert.deepEqual((await readdir(path.join(stage, 'ui/fonts'))).sort(), FONT_FILES.map(name => name.slice(9)).sort());
  assert.ok(copied.includes('ui/appearance.css'));
  assert.ok(copied.includes('ui/sample-dashboard.json'));
  assert.equal(copied.some(name => /test|private|credentials|node_modules|dist|overwolf-probes/.test(name)), false);
  assert.deepEqual((await readdir(path.join(stage, 'electron'))).sort(), ['branding', 'libobs-recorder.mjs', 'main.mjs', 'preload.cjs']);
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

test('a build without a bundled face or the font folder fails staging', async t => {
  const root = await scratch(t), source = path.join(root, 'source');
  await fixture(source);
  await rm(path.join(source, 'ui/fonts/geist.woff2'));
  await assert.rejects(stageApp(source, path.join(root, 'missing-face')), /Missing required app file: ui\/fonts\/geist\.woff2/);
  await rm(path.join(source, 'ui/fonts'), { recursive: true });
  await assert.rejects(stageApp(source, path.join(root, 'missing-folder')), /ENOENT/);
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
  for (const name of ['Revu.Sidecar.exe', 'Revu.Sidecar.dll',
    'Revu.Sidecar.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'resources/branding/revu.ico']) await file(packaged, name);
  await file(packaged, 'revu-desktop.exe', fusedExecutable());
  const archive = path.join(packaged, 'resources/app.asar');
  await asar.createPackage(source, archive);
  await assert.rejects(verifyPackage(packaged), /ENOENT/); // An app without its recorder cannot ship.
  let recorderVerified;
  await verifyPackage(packaged, { verifyRecorder: async directory => { recorderVerified = directory; } });
  assert.equal(recorderVerified, path.join(packaged, 'recorder'));
  await file(source, 'ui/private.json');
  await asar.createPackage(source, archive);
  await assert.rejects(verifyPackage(packaged), /Unexpected packaged app file/);
  await rm(path.join(source, 'ui/private.json'));
  await asar.createPackage(source, archive);
  await rm(path.join(packaged, 'coreclr.dll'));
  await assert.rejects(verifyPackage(packaged), /ENOENT/);
});

test('fuse verification requires RunAsNode, NODE_OPTIONS and inspect off and the ASAR fuses on', async t => {
  const root = await scratch(t), executable = path.join(root, 'revu-desktop.exe');
  assert.deepEqual(readFuseWire(fusedExecutable('01')), [0x30, 0x31]);
  await writeFile(executable, fusedExecutable());
  await verifyFuses(executable);
  // Electron's shipped defaults: RunAsNode, NODE_OPTIONS and inspect enabled, ASAR checks off.
  await writeFile(executable, fusedExecutable('10110001'));
  await assert.rejects(verifyFuses(executable), /RunAsNode must be disabled/);
  for (const [states, name] of [['00101100', 'EnableNodeOptionsEnvironmentVariable'], ['00011100', 'EnableNodeCliInspectArguments'],
    ['00000100', 'EnableEmbeddedAsarIntegrityValidation'], ['00001000', 'OnlyLoadAppFromAsar'], ['0000', 'EnableEmbeddedAsarIntegrityValidation']]) {
    await writeFile(executable, fusedExecutable(states));
    await assert.rejects(verifyFuses(executable), new RegExp(name), states);
  }
  await writeFile(executable, Buffer.from('MZ no fuse wire'));
  await assert.rejects(verifyFuses(executable), /no single Electron fuse wire/);
  await writeFile(executable, Buffer.concat([fusedExecutable(), fusedExecutable()]));
  await assert.rejects(verifyFuses(executable), /no single Electron fuse wire/);
  const future = fusedExecutable(); future[future.indexOf('dL7p') + 32] = 2;
  await writeFile(executable, future);
  await assert.rejects(verifyFuses(executable), /Unsupported Electron fuse wire version/);
});

test('the builder config requests every fuse state the package check enforces', () => {
  for (const [name, , enabled] of requiredFuses)
    assert.equal(builderConfig.electronFuses[name[0].toLowerCase() + name.slice(1)], enabled, name);
});

test('file policy excludes traversal and alternate separators', () => {
  for (const name of ['../ui/index.html', 'ui/../package.json', 'ui\\index.html',
    'ui//index.html', 'ui/.local.js', 'electron/bridge.spec.mjs', 'ui/fonts/x.ttf', 'ui/fonts/a/b.woff2',
    'ui/fonts/x.md', 'ui/fonts/Geist.woff2', 'ui/fonts/OFL.txt', 'ui/fonts/OFL-a.b.txt', 'ui/fonts/.x.woff2',
    'ui/platform/OFL-Geist.txt']) assert.equal(isAppFile(name), false, name);
  for (const name of ['ui/fonts/geist-mono.woff2', 'ui/fonts/OFL-GeistMono.txt', 'ui/appearance.css'])
    assert.equal(isAppFile(name), true, name);
});

async function recorderFixture(root) {
  for (const name of requiredRecorderFiles) await file(root, name, name.endsWith('.exe') ? 'MZ fixture' : 'fixture');
  const manifest = await recorderManifest(root, { sources: recorderSourceLock });
  await file(root, 'runtime-manifest.json', JSON.stringify(manifest));
  return manifest;
}

test('recorder policy allows only bundled modules and rejects injection, UI and third-party plugin assets', () => {
  assert.deepEqual(recorderModules, ['win-wasapi', 'obs-ffmpeg', 'obs-nvenc', 'obs-qsv11']);
  for (const name of ['data/obs-plugins/win-capture/graphics-hook64.dll',
    'data/obs-plugins/win-capture/inject-helper64.exe', 'data/obs-plugins/win-capture/get-graphics-offsets64.exe',
    'data/obs-plugins/win-capture/obs-vulkan64.json', 'obs64.exe', 'Qt6Core.dll',
    'obs-plugins/64bit/win-capture.dll', 'data/obs-plugins/win-capture/compatibility.json',
    'data/obs-plugins/win-capture/locale/en-US.ini', 'libcurl.dll', 'vpl.dll', 'libmfxhw64.dll', 'amfrt64.dll',
    'obs-plugins/64bit/obs-browser.dll', 'obs-plugins/64bit/obs-websocket.dll', 'obs-plugins/64bit/private.dll',
    'data/obs-plugins/win-capture/update.exe', '../obs.dll', 'obs-plugins\\64bit\\win-capture.dll',
    'obs-plugins//64bit/win-capture.dll', 'data/libobs/.private.effect']) assert.equal(isRecorderFile(name), false, name);
  for (const name of ['revu-recorder.exe', 'obs.dll', 'revu-media-ffprobe.exe', 'data/libobs/default.effect',
    'obs-plugins/64bit/win-wasapi.dll', 'data/obs-plugins/win-wasapi/locale/en-US.ini',
    'obs-plugins/64bit/obs-qsv11.dll', 'obs-qsv-test.exe', 'obs-amf-test.exe', 'data/obs-plugins/obs-qsv11/locale/en-US.ini'])
    assert.equal(isRecorderFile(name), true, name);
});

test('recorder verification requires the helper, matching source archives and an exact file inventory', async t => {
  const root = await scratch(t), runtime = path.join(root, 'recorder');
  await recorderFixture(runtime);
  assert.ok((await recorderFiles(runtime)).includes('revu-recorder.exe'));
  // A manifest cannot manufacture trust for a fake upstream source archive.
  await assert.rejects(verifyRecorderRuntime(runtime), /Recorder source checksum mismatch/);
  await rm(path.join(runtime, 'revu-recorder.exe'));
  await assert.rejects(verifyRecorderRuntime(runtime), /Missing recorder runtime file: revu-recorder.exe/);
  await file(runtime, 'revu-recorder.exe', 'MZ fixture');
  await rm(path.join(runtime, `sources/${recorderSourceLock[0].archive}`));
  await assert.rejects(verifyRecorderRuntime(runtime), /Missing recorder runtime file: sources/);
});

test('recorder manifests reject edited binaries, stale OBS versions and forbidden hooks', async t => {
  const root = await scratch(t), runtime = path.join(root, 'recorder');
  const manifest = await recorderFixture(runtime);
  assert.equal(manifest.obsVersion, obsPin.version);
  await file(runtime, 'obs.dll', 'altered');
  await assert.rejects(verifyRecorderRuntime(runtime), /integrity mismatch: obs.dll/);
  await file(runtime, 'obs.dll', 'fixture');
  manifest.obsVersion = '31.0.0';
  await file(runtime, 'runtime-manifest.json', JSON.stringify(manifest));
  await assert.rejects(verifyRecorderRuntime(runtime), /does not match the pinned OBS/);
  await file(runtime, 'data/obs-plugins/win-capture/graphics-hook64.dll');
  await assert.rejects(verifyRecorderRuntime(runtime), /Unexpected recorder runtime file/);
});

test('recorder runtime junctions cannot escape the package', async t => {
  const root = await scratch(t), runtime = path.join(root, 'recorder'), external = path.join(root, 'external');
  await recorderFixture(runtime); await file(external, 'private.dll', 'private');
  await symlink(external, path.join(runtime, 'private'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(recorderFiles(runtime), /must use real directories|rejects aliases/);
});

test('Intel and AMD packaging requires their adapter helpers, QSV plugin and matching oneVPL source/license', async t => {
  const root = await scratch(t), runtime = path.join(root, 'recorder');
  await recorderFixture(runtime);
  const source = recorderSourceLock.find(item => item.name === 'vpl');
  assert.equal(source.version, encoderDependencyProvenance.oneVpl.version);
  assert.equal(source.revision, encoderDependencyProvenance.oneVpl.revision);
  assert.equal(source.sha256, encoderDependencyProvenance.oneVpl.sourceSha256);
  assert.equal(source.recipe, encoderDependencyProvenance.oneVpl.recipe);
  assert.equal(source.archive, encoderDependencyProvenance.oneVpl.sourceArchive);
  for (const relative of ['obs-plugins/64bit/obs-qsv11.dll', 'obs-qsv-test.exe', 'obs-amf-test.exe',
    'data/obs-plugins/obs-qsv11/locale/en-US.ini', 'licenses/dependencies/vpl/LICENSE.txt', `sources/${source.archive}`]) {
    const original = await readFile(path.join(runtime, relative));
    await rm(path.join(runtime, relative));
    await assert.rejects(verifyRecorderRuntime(runtime), error => error.message === `Missing recorder runtime file: ${relative}`);
    await file(runtime, relative, original);
  }
});

test('encoder binary pins reject a rewritten helper or QSV plugin even when a manifest claims its new hash', () => {
  const files = Object.fromEntries(Object.entries(encoderBinaryPins).map(([relative, sha256]) => [relative, { sha256 }]));
  verifyEncoderBinaryPins(files);
  for (const relative of Object.keys(encoderBinaryPins)) {
    const altered = structuredClone(files);
    altered[relative].sha256 = '0'.repeat(64);
    assert.throws(() => verifyEncoderBinaryPins(altered), error => error.message === `Pinned encoder binary checksum mismatch: ${relative}`);
    delete altered[relative];
    assert.throws(() => verifyEncoderBinaryPins(altered), /Pinned encoder binary checksum mismatch/);
  }
});

test('oneVPL source provenance cannot drift independently from the pinned QSV binary dependency', () => {
  verifyEncoderSourceProvenance();
  assert.throws(() => verifyEncoderSourceProvenance(recorderSourceLock.filter(item => item.name !== 'vpl')), /does not match the pinned encoder binaries/);
  for (const field of ['version', 'revision', 'archive', 'sha256', 'recipe']) {
    const changed = structuredClone(recorderSourceLock);
    changed.find(item => item.name === 'vpl')[field] = 'incorrect';
    assert.throws(() => verifyEncoderSourceProvenance(changed), /does not match the pinned encoder binaries/);
  }
});

test('source subsets preserve exact bytes and produce reproducible ZIPs readable by an independent decoder',
  { skip: process.platform !== 'win32' }, async t => {
    const root = await scratch(t), first = path.join(root, 'first'), second = path.join(root, 'second');
    await file(first, 'include/example.h', '/* header */\n'); await file(first, 'LICENSE.txt', 'Original license\n');
    await file(second, 'LICENSE.txt', 'Original license\n'); await file(second, 'include/example.h', '/* header */\n');
    const archive = path.join(root, 'first.zip'), repeated = path.join(root, 'second.zip');
    await writeSourceZip(first, archive); await writeSourceZip(second, repeated);
    assert.deepEqual(await readFile(archive), await readFile(repeated));
    assert.equal(execFileSync('tar.exe', ['-xOf', archive, 'include/example.h'], { encoding: 'utf8', windowsHide: true }), '/* header */\n');
    assert.equal(execFileSync('tar.exe', ['-xOf', archive, 'LICENSE.txt'], { encoding: 'utf8', windowsHide: true }), 'Original license\n');
  });
