import assert from 'node:assert/strict';
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createRollingLog, logDirectory, redact } from '../logging.mjs';

async function scratch(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.HostLog.'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return root;
}

test('logs live under the data root next to the sidecar log', () => {
  assert.equal(logDirectory(path.resolve('scratch')), path.join(path.resolve('scratch'), 'Revu', 'logs'));
});

test('lines carry time, level, message and error stack; early lines wait for open', async t => {
  const root = await scratch(t);
  const log = createRollingLog({ name: 'host', now: () => new Date('2026-10-12T10:00:00.000Z') });
  log.info('Revu 3.14.1 starting (packaged)');
  assert.equal(log.file, null);
  log.open(root);
  log.error('Revu could not finish closing', new Error('Backend shutdown did not finish cleanly'));
  const text = await readFile(path.join(root, 'host.log'), 'utf8');
  assert.match(text, /^2026-10-12T10:00:00\.000Z INFO  Revu 3\.14\.1 starting \(packaged\)\n/);
  assert.match(text, /ERROR Revu could not finish closing\n  Error: Backend shutdown did not finish cleanly\n {6}at /);
});

test('emails, credentials and tokens never reach the file', async t => {
  const root = await scratch(t);
  const log = createRollingLog({ name: 'host' });
  log.open(root);
  log.warn(`Sign in for player.one@example.com failed: Bearer ${'A'.repeat(64)} ?token=secret123&code=424242 RGAPI-0000-key`);
  log.append(Buffer.from('Unhandled exception. user@example.org\n'));
  const text = await readFile(path.join(root, 'host.log'), 'utf8');
  for (const secret of ['player.one', 'A'.repeat(64), 'secret123', '424242', 'RGAPI-0000', 'user@example.org'])
    assert.doesNotMatch(text, new RegExp(secret.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  assert.equal(redact('mail a.b+c@d.co.uk now'), 'mail [email] now');
  assert.equal(redact('Authorization: Bearer abc.def'), 'Authorization: Bearer [redacted]');
});

test('the log rolls over at its size cap and keeps a bounded number of generations', async t => {
  const root = await scratch(t);
  const log = createRollingLog({ name: 'sidecar-stderr', maxBytes: 2048, maxFiles: 2 });
  log.open(root);
  for (let index = 0; index < 200; index++) log.append(`line ${index} ${'x'.repeat(40)}\n`);
  assert.deepEqual((await readdir(root)).sort(), ['sidecar-stderr.1.log', 'sidecar-stderr.log']);
  const current = await readFile(path.join(root, 'sidecar-stderr.log'), 'utf8');
  const previous = await readFile(path.join(root, 'sidecar-stderr.1.log'), 'utf8');
  assert.ok(Buffer.byteLength(current) <= 2048 && Buffer.byteLength(previous) <= 2048);
  assert.match(current, /line 199 /);
  assert.doesNotMatch(current + previous, /line 0 /);
});

test('an unwritable log location never throws into the host', async t => {
  const root = await scratch(t);
  const blocked = path.join(root, 'blocked');
  await writeFile(blocked, 'a file where the log folder should be');
  const log = createRollingLog({ name: 'host' });
  log.open(blocked);
  assert.doesNotThrow(() => { log.error('still running', new Error('disk')); log.append('more'); });
});
