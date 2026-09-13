import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { prepareRecorderDevelopment } from '../../scripts/start-recorder.mjs';

async function scratch(t) {
  const base = path.resolve(os.tmpdir());
  const directory = await mkdtemp(path.join(base, 'Revu.RecorderDevelopment.Tests.'));
  t.after(async () => {
    assert.equal(path.dirname(directory), base);
    assert.ok(path.basename(directory).startsWith('Revu.RecorderDevelopment.Tests.'));
    await rm(directory, { recursive: true, force: true });
  });
  return directory;
}
async function file(root, name, value = 'fixture') {
  const target = path.join(root, name);
  await mkdir(path.dirname(target), { recursive: true });
  await writeFile(target, value);
}
async function fixture(root) {
  for (const name of ['electron/main.mjs', 'electron/preload.cjs', 'electron/recording-service.mjs',
    'electron/branding/revu.ico', 'runtime/overwolf.mjs', 'ui/index.html', 'ui/settings.html',
    'ui/recording-settings.mjs', 'ui/platform/index.mjs', 'ui/fonts/Inter.ttf']) await file(root, name);
  const manifest = JSON.stringify({ name: 'revu-desktop', productName: 'Revu', version: '3.10.1',
    author: '', main: 'electron/main.mjs', type: 'module', overwolf: { packages: [] } }, null, 2);
  await file(root, 'package.json', manifest);
  return manifest;
}

test('Recorder development stages the real host with approved identity and only Recorder, leaving its source manifest unchanged', async t => {
  const directory = await scratch(t), root = path.join(directory, 'source'), destination = path.join(directory, 'stage');
  const originalManifest = await fixture(root);
  const files = await prepareRecorderDevelopment({ root, destination,
    identity: { productName: 'Approved Revu Fixture', authorName: 'Approved Fixture Author' } });
  const staged = JSON.parse(await readFile(path.join(destination, 'package.json'), 'utf8'));
  assert.equal(staged.productName, 'Approved Revu Fixture');
  assert.deepEqual(staged.author, { name: 'Approved Fixture Author' });
  assert.deepEqual(staged.overwolf, { packages: ['recorder'] });
  assert.equal(staged.main, 'electron/main.mjs');
  assert.equal(staged.version, '3.10.1');
  assert.ok(files.includes('electron/recording-service.mjs'));
  assert.ok(files.includes('ui/recording-settings.mjs'));
  assert.equal(await readFile(path.join(root, 'package.json'), 'utf8'), originalManifest);
});

test('local approval metadata and fixture credentials do not enter staged files or the manifest', async t => {
  const directory = await scratch(t), root = path.join(directory, 'source'), destination = path.join(directory, 'stage');
  await fixture(root);
  const privateMarker = 'fixture-private-metadata-must-not-be-staged';
  for (const name of ['overwolf-probes/.local/access.json', 'electron/.local/key.json', 'electron/private.local.mjs',
    'runtime/.env', 'ui/.env', 'scripts/Start-RecorderDevelopment.ps1']) await file(root, name, privateMarker);
  const files = await prepareRecorderDevelopment({ root, destination, identity: {
    productName: 'Approved Revu Fixture', authorName: 'Approved Fixture Author',
    uid: privateMarker, reference: privateMarker, OW_DEV_KEY: privateMarker,
    leagueAccess: privateMarker, recorderAccess: privateMarker,
  } });
  for (const relative of files) {
    assert.equal((await readFile(path.join(destination, relative), 'utf8')).includes(privateMarker), false, relative);
  }
  assert.deepEqual((await readdir(destination)).sort(), ['electron', 'package.json', 'runtime', 'ui']);
  const manifest = JSON.parse(await readFile(path.join(destination, 'package.json'), 'utf8'));
  for (const field of ['uid', 'reference', 'OW_DEV_KEY', 'leagueAccess', 'recorderAccess']) assert.equal(Object.hasOwn(manifest, field), false);
});

test('invalid approval identity is rejected before the destination is created or source staging begins', async t => {
  const directory = await scratch(t), root = path.join(directory, 'source');
  const originalManifest = await fixture(root);
  const invalid = [undefined, null, {}, { productName: 'Revu' }, { productName: ' ', authorName: 'Author' },
    { productName: 'Revu', authorName: '' }, { productName: 42, authorName: 'Author' },
    { productName: 'Revu', authorName: 'x'.repeat(161) }, { productName: 'x'.repeat(161), authorName: 'Author' }];
  for (const [index, identity] of invalid.entries()) {
    const destination = path.join(directory, `invalid-${index}`);
    await assert.rejects(prepareRecorderDevelopment({ root, destination, identity }), /approved productName and authorName/);
    await assert.rejects(readdir(destination), { code: 'ENOENT' });
  }
  assert.equal(await readFile(path.join(root, 'package.json'), 'utf8'), originalManifest);
});
