import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { byteRange, MediaRegistry } from '../media.mjs';
import { commandRequest, validateSender, snapshotMedia } from '../routing.mjs';
import { SseDecoder, validateHandshake } from '../sidecar.mjs';

test('single byte ranges, suffixes, bounded end, and large offsets', () => {
  assert.deepEqual(byteRange('bytes=7-99', 10), { start: 7, end: 9, partial: true });
  assert.deepEqual(byteRange('bytes=-3', 10), { start: 7, end: 9, partial: true });
  assert.deepEqual(byteRange('bytes=4294967296-', 5000000000), { start: 4294967296, end: 4999999999, partial: true });
  for (const range of ['bytes=10-', 'bytes=3-2', 'bytes=-0', 'bytes=0-1,3-4', 'bytes=-', 'cats=0-1']) assert.equal(byteRange(range, 10), null);
});
test('media authorizes records, serves ranges/HEAD, and revokes old grants', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'Revu.Media.'));
  const file = path.join(root, 'VOD ü space.mp4');
  const registry = new MediaRegistry();
  try {
    await writeFile(file, '0123456789');
    const [[, url]] = await registry.replace([file]);
    const response = await registry.respond(new Request(url, { headers: { Range: 'bytes=4-6' } }));
    assert.equal(response.status, 206); assert.equal(await response.text(), '456');
    assert.equal(response.headers.get('Content-Range'), 'bytes 4-6/10');
    const [[, refreshedUrl]] = await registry.replace([file]);
    assert.equal(refreshedUrl, url); // Bookmark refresh must not invalidate video.src.
    assert.equal((await registry.respond(new Request(url, { headers: { Range: 'bytes=8-' } }))).status, 206);
    const head = await registry.respond(new Request(url, { method: 'HEAD' }));
    assert.equal(head.headers.get('Content-Length'), '10'); assert.equal(await head.text(), '');
    assert.equal((await registry.respond(new Request(url, { headers: { Range: 'bytes=10-' } }))).status, 416);
    assert.equal((await registry.respond(new Request('revu-media://file/' + encodeURIComponent(file)))).status, 403);
    registry.clear(); assert.equal((await registry.respond(new Request(url))).status, 403);
    const [[, nextUrl]] = await registry.replace([file]);
    await rm(file); assert.equal((await registry.respond(new Request(nextUrl))).status, 404);
  } finally { registry.clear(); await rm(root, { recursive: true, force: true }); }
});
test('only trusted main-frame IPC, command allowlist, and query encoding', () => {
  const mainFrame = { url: 'revu-app://ui/index.html' }; const contents = { mainFrame };
  validateSender({ sender: contents, senderFrame: mainFrame }, contents);
  assert.throws(() => validateSender({ sender: contents, senderFrame: { url: mainFrame.url } }, contents));
  mainFrame.url = 'https://example.com'; assert.throws(() => validateSender({ sender: contents, senderFrame: mainFrame }, contents));
  const request = commandRequest('get_pregame', { enemy: 'A&B', myChampion: 'Ü', role: 'mid' });
  assert.match(request.route, /enemy=A%26B/);
  assert.throws(() => commandRequest('fetch', { url: 'http://localhost' }));
  assert.throws(() => commandRequest('get_vod', { gameId: '1' }));
  const scan = commandRequest('scan_vods');
  assert.equal(scan.route, '/api/settings/scan-vods');
  assert.equal(scan.method, 'POST');
  assert.deepEqual(scan.body, {});
  assert.throws(() => commandRequest('scan_vods', { folder: 'C:\\unconfigured' }));
  assert.throws(() => commandRequest('save_config', { payload: { notes: 'x'.repeat(65536) } }));
  assert.equal(snapshotMedia('get_config', { filePath: 'secret' }), null);
  assert.deepEqual(snapshotMedia('get_patterns', { groups: [{ moments: [{ vodPath: 'vod', clipPath: 'clip' }] }] }), ['vod', 'clip']);
});
test('stale and foreign handshakes never become an authenticated backend', () => {
  const identity = { launchId: 'test', processId: 12, dataDirectory: path.resolve('scratch') };
  const value = { ...identity, apiVersion: 1, port: 5000, token: 'A'.repeat(64) };
  validateHandshake(value, identity);
  for (const change of [{ launchId: 'foreign' }, { processId: 13 }, { apiVersion: 2 }, { dataDirectory: '/other' }, { token: 'not-a-token' }])
    assert.throws(() => validateHandshake({ ...value, ...change }, identity));
});
test('SSE handles split UTF-8, multiline records, comments, and bounded input', () => {
  const events = []; const decoder = new SseDecoder(event => events.push(event));
  const bytes = Buffer.from(': heartbeat\r\n\r\nevent: liveState\r\ndata: {"name":\r\ndata: "ü"}\r\n\r\n');
  for (const byte of bytes) decoder.push(Uint8Array.of(byte));
  assert.deepEqual(events, [{ type: 'liveState', payload: { name: 'ü' } }]);
  assert.throws(() => decoder.push(Buffer.alloc(262145, 32)), /limit/);
});
