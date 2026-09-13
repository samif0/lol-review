import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { isMaintenanceLaunch, launchOptions, prepareConfiguration } from '../configuration.mjs';
import { externalUrl, updaterPath, nativeCommands } from '../native.mjs';
import { appResponse } from '../protocols.mjs';

test('normal launch retains the data root and installed layout; isolated smoke cannot target LocalAppData', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.HostConfig.'));
  const localAppData = path.join(root, 'LocalAppData');
  const desktopRoot = path.join(root, 'resources/app.asar');
  const executablePath = path.join(root, 'current/revu-desktop.exe');
  const base = { desktopRoot, localAppData, packaged: true, executablePath, environment: {} };
  const normal = await prepareConfiguration({ ...base, options: launchOptions([]) });
  assert.equal(normal.dataRoot, localAppData);
  assert.equal(normal.executable, path.join(root, 'current/Revu.Sidecar.exe'));
  assert.equal(normal.uiDirectory, path.join(desktopRoot, 'ui'));
  assert.equal(normal.iconPath, path.join(root, 'current/resources/branding/revu.ico'));
  assert.equal(normal.windowsAppId, 'gg.revu.desktop');
  assert.throws(() => prepareConfiguration({ ...base, options: launchOptions(['--isolated']) }), /require --data-root/);
  assert.throws(() => launchOptions(['--smoke']), /requires --isolated/);
  assert.throws(() => launchOptions(['--isolated', '--data-root', '--smoke']), /absolute path/);
  assert.throws(() => launchOptions(['--sidecar', 'relative.exe']), /absolute path/);
  assert.throws(() => prepareConfiguration({ ...base, options: launchOptions(['--isolated', '--data-root', localAppData]) }), /outside LocalAppData/);
  const isolated = await prepareConfiguration({ ...base, options: launchOptions(['--isolated', '--smoke', '--data-root', path.join(root, 'scratch')]) });
  assert.equal(isolated.isolated, true);
  assert.notEqual(isolated.profile, normal.profile);
  assert.notEqual(isolated.windowsAppId, normal.windowsAppId);
});

test('Velopack maintenance hooks skip startup, first-run opens normally, updater is install-local only', () => {
  for (const event of ['install', 'updated', 'obsolete', 'uninstall']) assert.equal(isMaintenanceLaunch([`--veloapp-${event}`]), true);
  assert.equal(isMaintenanceLaunch(['--veloapp-firstrun']), false);
  assert.equal(isMaintenanceLaunch(['--veloapp-install-fake']), false);
  const executable = path.resolve('fixture/current/revu-desktop.exe');
  assert.equal(updaterPath(executable, true), path.resolve('fixture/Update.exe'));
  assert.throws(() => updaterPath(executable, false), /installed app/);
  assert.throws(() => updaterPath(path.resolve('fixture/win-unpacked/revu-desktop.exe'), true), /installed app/);
});

test('external links cannot launch local executables, custom protocols or embedded credentials', () => {
  assert.equal(externalUrl('https://revu.gg/docs?a=b'), 'https://revu.gg/docs?a=b');
  for (const url of ['file:///C:/Windows/system32/cmd.exe', 'javascript:alert(1)', 'ms-settings:',
    'https://user:password@example.com', '\\\\server\\app.exe', null, 'x'.repeat(8193)]) assert.throws(() => externalUrl(url));
});

test('native commands retain export cancellation and version behavior without invoking an updater in diagnostics', async () => {
  const native = nativeCommands({ isolated: true, app: { getVersion: () => '3.10.1' },
    dialog: { showSaveDialog: async () => ({ canceled: true }), showOpenDialog: async () => ({ canceled: true }) } });
  assert.equal(await native('app_version'), '3.10.1');
  assert.equal(await native('pick_folder'), null);
  assert.deepEqual(await native('save_export_file', { fileName: 'review.md', markdown: 'draft' }), { ok: true, saved: false });
  await assert.rejects(native('apply_update'), /isolated mode/);
});

test('UI protocol serves approved assets with CSP and rejects arbitrary files and malformed paths', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.Protocol.'));
  const ui = path.join(root, 'ui');
  await mkdir(ui);
  await writeFile(path.join(ui, 'index.html'), '<h1>Revu</h1>');
  await writeFile(path.join(ui, 'secret.txt'), 'private');
  const response = await appResponse(new Request('revu-app://ui/index.html'), ui);
  assert.equal(response.status, 200);
  assert.equal(await response.text(), '<h1>Revu</h1>');
  assert.match(response.headers.get('content-security-policy'), /object-src 'none'/);
  assert.equal((await appResponse(new Request('revu-app://ui/index.html', { method: 'HEAD' }), ui)).headers.get('content-type'), 'text/html');
  for (const url of ['revu-app://ui/secret.txt', 'revu-app://ui/%2e%2e%5csecret.txt', 'revu-app://evil/index.html'])
    assert.equal((await appResponse(new Request(url), ui)).status, 403);
  assert.equal((await appResponse(new Request('revu-app://ui/%ZZ'), ui)).status, 404);
});
