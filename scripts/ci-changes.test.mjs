import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { classify } from './ci-changes.mjs';

const both = { dotnet: true, desktop: true };
const neither = { dotnet: false, desktop: false };

test('site, proxy, Linux-tested scripts and root docs need no Windows job', () => {
  assert.deepEqual(classify(['site/index.html', 'proxy/src/clips.ts', 'scripts/deploy-site.mjs', 'README.md', '.github/workflows/release.yml']), neither);
});

test('desktop changes build the desktop only, except the sidecar contract file', () => {
  assert.deepEqual(classify(['desktop/ui/settings.js', 'desktop/tests/visual-system.test.mjs']), { dotnet: false, desktop: true });
  assert.deepEqual(classify(['desktop/ui/platform/commands.mjs']), both);
});

test('C# sources run the .NET tests and the desktop build that packages the sidecar', () => {
  assert.deepEqual(classify(['src/Revu.Core/Models/GameEvent.cs']), both);
});

test('packaging inputs outside desktop/ build the desktop', () => {
  for (const file of ['scripts/Get-PinnedFfmpeg.ps1', 'scripts/Test-PackagedHost.ps1', 'scripts/Test-ElectronHost.ps1', 'licenses/ffmpeg.txt', 'LICENSE']) {
    assert.deepEqual(classify([file]), { dotnet: false, desktop: true }, file);
  }
});

test('CI itself, shared build settings, unknown paths and an empty diff run everything', () => {
  for (const files of [['.github/workflows/ci.yml'], ['scripts/ci-changes.mjs'], ['global.json'], ['Directory.Build.props'], ['Revu.sln'], ['some/new/folder/file.txt'], [], ['', '  ']]) {
    assert.deepEqual(classify(files), both, JSON.stringify(files));
  }
});

test('Windows separators and surrounding whitespace are normalized', () => {
  assert.deepEqual(classify(['site\\index.html\r', ' proxy/README.md ']), neither);
});

test('the command line prints GITHUB_OUTPUT lines', () => {
  const script = fileURLToPath(new URL('./ci-changes.mjs', import.meta.url));
  const result = spawnSync(process.execPath, [script], { input: 'desktop/ui/app.js\nsite/index.html\n', encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, 'dotnet=false\ndesktop=true\n');
});
