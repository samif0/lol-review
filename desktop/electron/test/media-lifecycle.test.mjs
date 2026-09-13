import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, open, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { byteRange, MediaRegistry } from '../media.mjs';

async function fixture(run) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'Revu.Media.Lifecycle.'));
  const registry = new MediaRegistry();
  try { await run(directory, registry); }
  finally { registry.clear(); await rm(directory, { recursive: true, force: true }); }
}

test('same-file refresh preserves an active stream while removed files lose access', () => fixture(async (directory, registry) => {
  const first = path.join(directory, 'first ü.mp4');
  const second = path.join(directory, 'second.mp4');
  const bytes = Buffer.alloc(512 * 1024, 91);
  await writeFile(first, bytes);
  await writeFile(second, 'second');
  const original = new Map(await registry.replace([first, second]));
  const response = await registry.respond(new Request(original.get(first)));
  const updated = new Map(await registry.replace([first]));
  assert.equal(updated.get(first), original.get(first));
  assert.deepEqual(Buffer.from(await response.arrayBuffer()), bytes);
  assert.equal((await registry.respond(new Request(original.get(second)))).status, 403);
  const seek = await registry.respond(new Request(original.get(first), { headers: { Range: 'bytes=200000-200004' } }));
  assert.equal(seek.status, 206);
  assert.deepEqual(Buffer.from(await seek.arrayBuffer()), bytes.subarray(200000, 200005));
}));

test('clear during grant resolution rejects stale grants and allows the next page', () => fixture(async (directory, registry) => {
  const file = path.join(directory, 'synthetic.mp4');
  await writeFile(file, '0123456789');
  const stale = registry.replace([file]);
  registry.clear();
  await assert.rejects(stale, /Media review changed/);
  const [[, url]] = await registry.replace([file]);
  const response = await registry.respond(new Request(url));
  assert.equal(await response.text(), '0123456789');
}));

test('overlapping media snapshots accept the latest replacement only', () => fixture(async (directory, registry) => {
  const first = path.join(directory, 'first.mp4');
  const second = path.join(directory, 'second.mp4');
  await writeFile(first, 'first');
  await writeFile(second, 'second');
  const outcomes = await Promise.allSettled([registry.replace([first]), registry.replace([second])]);
  assert.equal(outcomes[0].status, 'rejected');
  assert.equal(outcomes[1].status, 'fulfilled');
  const [[, url]] = outcomes[1].value;
  assert.equal(await (await registry.respond(new Request(url))).text(), 'second');
}));

test('revocation cancels open streams and permits deleting the media file', () => fixture(async (directory, registry) => {
  const file = path.join(directory, 'large.mp4');
  await writeFile(file, Buffer.alloc(4 * 1024 * 1024, 19));
  const [[, url]] = await registry.replace([file]);
  const response = await registry.respond(new Request(url));
  const reader = response.body.getReader();
  const first = await reader.read();
  assert.equal(first.done, false);
  registry.clear();
  await assert.rejects(async () => { while (!(await reader.read()).done) {} });
  // The OS may deliver the stream close event just after its rejection.
  await new Promise((resolve) => setImmediate(resolve));
  await rm(file);
  assert.equal((await registry.respond(new Request(url))).status, 403);
}));

test('empty media, suffix ranges, invalid methods and URL decorations fail predictably', () => fixture(async (directory, registry) => {
  const empty = path.join(directory, 'empty.mp4');
  const file = path.join(directory, 'clip.mp4');
  await writeFile(empty, '');
  await writeFile(file, '0123456789');
  const grants = new Map(await registry.replace([empty, file]));
  const emptyResponse = await registry.respond(new Request(grants.get(empty)));
  assert.equal(emptyResponse.status, 200);
  assert.equal(emptyResponse.headers.get('Content-Length'), '0');
  assert.equal(await emptyResponse.text(), '');
  assert.equal((await registry.respond(new Request(grants.get(empty), { headers: { Range: 'bytes=0-' } }))).status, 416);
  const suffix = await registry.respond(new Request(grants.get(file), { headers: { Range: 'bytes=-50' } }));
  assert.equal(suffix.status, 206);
  assert.equal(await suffix.text(), '0123456789');
  assert.equal((await registry.respond(new Request(grants.get(file), { method: 'POST' }))).status, 405);
  for (const tail of ['?secret=1', '#fragment', '/extra'])
    assert.equal((await registry.respond(new Request(grants.get(file) + tail))).status, 403);
  assert.equal(byteRange('bytes=9007199254740992-', 100), null);
}));

test('large media serves an actual range above the 32-bit file offset boundary', (context) => fixture(async (directory, registry) => {
  const file = path.join(directory, 'sparse.mp4');
  const position = 2 ** 32 + 7;
  const handle = await open(file, 'w');
  try {
    if (process.platform === 'win32') {
      try { await promisify(execFile)('fsutil', ['sparse', 'setflag', file], { windowsHide: true }); }
      catch { context.skip('Filesystem does not permit sparse fixtures'); return; }
    }
    // Sparse extent: validates offsets without creating gigabytes of test bytes.
    await handle.write(Buffer.from('seek'), 0, 4, position);
  } finally { await handle.close(); }
  const [[, url]] = await registry.replace([file]);
  const response = await registry.respond(new Request(url, { headers: { Range: `bytes=${position}-${position + 3}` } }));
  assert.equal(response.status, 206);
  assert.equal(response.headers.get('Content-Range'), `bytes ${position}-${position + 3}/${position + 4}`);
  assert.equal(await response.text(), 'seek');
}));
