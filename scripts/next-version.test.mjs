import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { nextVersion } from './next-version.mjs';

const tags = ['v4.4.2', 'v4.10.0', 'v4.9.7', 'v3.16.0', 'v4.11.0-beta.1', 'v5', 'release-1', 'v4.10.0 '];

test('bumps the highest stable tag numerically, ignoring prereleases and other names', () => {
  assert.equal(nextVersion(tags, 'patch'), 'v4.10.1');
  assert.equal(nextVersion(tags, 'minor'), 'v4.11.0');
  assert.equal(nextVersion(tags, 'major'), 'v5.0.0');
});

test('refuses an unknown bump or a repository without stable tags', () => {
  assert.throws(() => nextVersion(tags, 'beta'), /patch, minor or major/);
  assert.throws(() => nextVersion(['v1.0.0-rc.1'], 'patch'), /no stable/);
});

test('the command line reads tags from stdin', () => {
  const script = fileURLToPath(new URL('./next-version.mjs', import.meta.url));
  const ok = spawnSync(process.execPath, [script, 'minor'], { input: 'v4.5.0\nv4.4.2\n', encoding: 'utf8' });
  assert.equal(ok.status, 0, ok.stderr);
  assert.equal(ok.stdout, 'v4.6.0\n');
  const bad = spawnSync(process.execPath, [script, 'nope'], { input: 'v4.5.0\n', encoding: 'utf8' });
  assert.equal(bad.status, 1);
});
