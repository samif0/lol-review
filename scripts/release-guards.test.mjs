import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { repositoryRoot } from './guard-deploy.mjs';

const workflowPath = path.join(repositoryRoot, '.github', 'workflows', 'release.yml');
const cleanupStep = 'Remove a draft left by an earlier attempt';
const repo = 'owner/revu';
const tag = 'v3.15.0';

// The `run: |` block of a named step, dedented, exactly as the runner executes it.
function stepScript(workflow, name) {
  const lines = workflow.split(/\r?\n/);
  const start = lines.findIndex(line => line.trim() === `- name: ${name}`);
  assert.notEqual(start, -1, `step "${name}" not found`);
  const run = lines.findIndex((line, index) => index > start && /^\s*run: \|$/.test(line));
  assert.ok(run > start && !lines.slice(start + 1, run).some(line => /^\s*- name: /.test(line)), `step "${name}" has no run block`);
  const runIndent = lines[run].search(/\S/);
  const body = [];
  for (const line of lines.slice(run + 1)) {
    if (line.trim() && line.search(/\S/) <= runIndent) break;
    body.push(line);
  }
  const indent = Math.min(...body.filter(line => line.trim()).map(line => line.search(/\S/)));
  return body.map(line => line.slice(indent)).join('\n').trimEnd();
}

function findPowerShell() {
  for (const command of process.platform === 'win32' ? ['pwsh', 'powershell'] : ['pwsh']) {
    const probe = spawnSync(command, ['-NoProfile', '-NonInteractive', '-Command', 'exit 0'], { windowsHide: true });
    if (probe.status === 0) return command;
  }
  return null;
}
const powershell = findPowerShell();
const skip = powershell ? false : 'PowerShell is not installed';

// `gh` is replaced by a function (functions win over executables): the release
// listing returns the fixture lines, which stand in for the step's --jq output,
// and every call is logged. The wrapper mirrors the runner's pwsh template.
const fakeGh = `
function gh {
  Add-Content -LiteralPath $env:FAKE_GH_LOG -Value ($args -join ' ')
  if ($args[0] -eq 'api' -and $args -contains '--paginate') {
    $global:LASTEXITCODE = [int]$env:FAKE_GH_LIST_EXIT
    if ($global:LASTEXITCODE -eq 0) { Get-Content -LiteralPath $env:FAKE_GH_RELEASES }
    return
  }
  $global:LASTEXITCODE = 0
}
$ErrorActionPreference = 'Stop'
`;

async function runCleanup(t, releases, { listExit = 0 } = {}) {
  const base = path.resolve(os.tmpdir());
  const root = await mkdtemp(path.join(base, 'Revu.ReleaseGuards.Tests.'));
  t.after(async () => {
    assert.equal(path.dirname(root), base);
    await rm(root, { recursive: true, force: true });
  });
  const log = path.join(root, 'gh.log');
  const fixture = path.join(root, 'releases.jsonl');
  await writeFile(log, '');
  await writeFile(fixture, releases.map(release => JSON.stringify(release)).join('\n'));
  const step = stepScript(await readFile(workflowPath, 'utf8'), cleanupStep);
  const script = `${fakeGh}\n${step}\nif ((Test-Path -LiteralPath variable:\\LASTEXITCODE)) { exit $LASTEXITCODE }\n`;
  const result = spawnSync(powershell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], {
    encoding: 'utf8',
    windowsHide: true,
    env: { ...process.env, TAG: tag, VERSION: '3.15.0', GH_REPO: repo, FAKE_GH_LOG: log, FAKE_GH_RELEASES: fixture, FAKE_GH_LIST_EXIT: String(listExit) },
  });
  const calls = (await readFile(log, 'utf8')).split(/\r?\n/).filter(Boolean);
  return { status: result.status, output: `${result.stdout}\n${result.stderr}`.replace(/\s+/g, ' '), calls };
}

const listCall = `api --paginate repos/${repo}/releases?per_page=100 --jq .[] | {id, draft, tag_name, name} | @json`;
const deleteCall = id => `api --method DELETE repos/${repo}/releases/${id}`;
const published = { id: 11, draft: false, tag_name: 'v3.14.1', name: 'Revu v3.14.1' };

test('the publish job clears earlier drafts before it uploads, then verifies and publishes', async () => {
  const workflow = await readFile(workflowPath, 'utf8');
  const steps = [...workflow.matchAll(/^\s*- name: (.+)$/gm)].map(match => match[1].trim());
  const order = [cleanupStep, 'Upload Velopack release to GitHub as a draft', 'Verify the draft before it goes live', 'Publish the release']
    .map(name => steps.indexOf(name));
  assert.ok(order.every((index, position) => index !== -1 && (position === 0 || index > order[position - 1])), `unexpected step order ${order}`);
  assert.match(stepScript(workflow, 'Upload Velopack release to GitHub as a draft'), /if \(\$env:PRERELEASE -eq 'true'\) \{ \$arguments \+= '--pre' \}/);
});

test('a first attempt with no release for the tag deletes nothing', { skip }, async t => {
  const unrelated = [published, { id: 12, draft: true, tag_name: 'V3.15.0', name: 'Revu V3.15.0' }];
  const { status, calls } = await runCleanup(t, unrelated);
  assert.equal(status, 0);
  assert.deepEqual(calls, [listCall]);
});

test('a re-run deletes the drafts an earlier attempt left, matched by tag or by name', { skip }, async t => {
  const releases = [
    { id: 21, draft: true, tag_name: tag, name: 'Revu v3.15.0' },
    { id: 22, draft: true, tag_name: 'untagged-abc', name: 'Revu v3.15.0' },
    published,
  ];
  const { status, calls, output } = await runCleanup(t, releases);
  assert.equal(status, 0, output);
  assert.deepEqual(calls, [listCall, deleteCall(21), deleteCall(22)]);
});

test('a published release for the tag or name is never deleted and fails the job', { skip }, async t => {
  for (const live of [{ id: 31, draft: false, tag_name: tag, name: 'Revu v3.15.0' }, { id: 32, draft: false, tag_name: 'v3.15.0-old', name: 'Revu v3.15.0' }]) {
    const { status, calls, output } = await runCleanup(t, [{ id: 30, draft: true, tag_name: tag, name: 'Revu v3.15.0' }, live]);
    assert.notEqual(status, 0);
    assert.match(output, /already published; refusing to replace it/);
    assert.deepEqual(calls, [listCall]);
  }
});

test('a failed release listing stops the job before any delete', { skip }, async t => {
  const { status, calls } = await runCleanup(t, [{ id: 41, draft: true, tag_name: tag, name: 'Revu v3.15.0' }], { listExit: 1 });
  assert.equal(status, 1);
  assert.deepEqual(calls, [listCall]);
});

test('the proxy README documents only the guarded deploy', async () => {
  const readme = await readFile(path.join(repositoryRoot, 'proxy', 'README.md'), 'utf8');
  const commands = [...readme.matchAll(/```sh\r?\n([\s\S]*?)```/g)].flatMap(match => match[1].split(/\r?\n/));
  assert.deepEqual(commands.filter(line => /^\s*(npx\s+)?wrangler\s+deploy\b/.test(line)), []);
  assert.ok(commands.filter(line => /^npm run deploy\b/.test(line)).length >= 2);
});
