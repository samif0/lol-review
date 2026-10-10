import assert from 'node:assert/strict';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import { DEFINITIVE_REJECTIONS, saveNarration, toNarrationBuffer, validateNarrationMeta, writeNarrationFile } from '../narration.mjs';

const meta = Object.freeze({ gameId: 12, bookmarkId: 34, mimeType: 'audio/webm;codecs=opus', offsetMs: -120,
  durationMs: 4500, gameVolume: 0.8, narrationVolume: 1, duck: true });
function webm(size = 2048) {
  const bytes = new Uint8Array(size);
  bytes.set([0x1A, 0x45, 0xDF, 0xA3]);
  bytes[size - 1] = 7;
  return bytes;
}

test('narration details must be exactly the documented fields within their ranges', () => {
  const valid = validateNarrationMeta({ ...meta });
  assert.deepEqual({ ...valid }, meta);
  assert.ok(Object.isFrozen(valid));
  assert.deepEqual({ ...validateNarrationMeta({ ...meta, mimeType: 'audio/webm', offsetMs: 10000, durationMs: 1000,
    gameVolume: 0, narrationVolume: 2, duck: false }) }, { ...meta, mimeType: 'audio/webm', offsetMs: 10000,
    durationMs: 1000, gameVolume: 0, narrationVolume: 2, duck: false });
  assert.equal(validateNarrationMeta({ ...meta, offsetMs: -10000, durationMs: 625000, gameVolume: 1.5 }).durationMs, 625000);
  const { duck, ...missing } = meta;
  for (const value of [null, undefined, [], 'meta', missing, { ...meta, extra: 1 }, { ...meta, narrationId: 'x' },
    { ...meta, gameId: 0 }, { ...meta, gameId: 1.5 }, { ...meta, gameId: '12' }, { ...meta, bookmarkId: -1 },
    { ...meta, bookmarkId: Number.MAX_SAFE_INTEGER + 1 },
    { ...meta, mimeType: 'audio/ogg' }, { ...meta, mimeType: 'video/webm' }, { ...meta, mimeType: 'audio/webm' + 'x'.repeat(60) },
    { ...meta, mimeType: 1 }, { ...meta, offsetMs: 10001 }, { ...meta, offsetMs: -10001 }, { ...meta, offsetMs: 1.5 },
    { ...meta, durationMs: 999 }, { ...meta, durationMs: 625001 }, { ...meta, durationMs: 1500.5 },
    { ...meta, gameVolume: 1.51 }, { ...meta, gameVolume: -0.1 }, { ...meta, gameVolume: NaN }, { ...meta, gameVolume: '1' },
    { ...meta, narrationVolume: 2.01 }, { ...meta, narrationVolume: Infinity }, { ...meta, duck: 1 }, { ...meta, duck: 'true' }])
    assert.throws(() => validateNarrationMeta(value), TypeError, JSON.stringify(value));
});

test('narration audio must be a WebM byte view or buffer of 1 KiB to 32 MiB from any realm', () => {
  const bytes = webm();
  const buffer = toNarrationBuffer(bytes);
  assert.ok(Buffer.isBuffer(buffer));
  assert.equal(buffer.length, 2048);
  bytes[5] = 99; // the returned buffer owns its bytes
  assert.equal(buffer[5], 0);
  assert.equal(toNarrationBuffer(webm(1024).buffer).length, 1024);
  assert.equal(toNarrationBuffer(webm(33554432)).length, 33554432);
  const foreign = vm.runInNewContext('const b = new Uint8Array(1500); b.set([0x1A, 0x45, 0xDF, 0xA3]); b');
  assert.equal(foreign instanceof Uint8Array, false);
  assert.equal(toNarrationBuffer(foreign).length, 1500);
  assert.equal(toNarrationBuffer(vm.runInNewContext('const b = new Uint8Array(1500); b.set([0x1A, 0x45, 0xDF, 0xA3]); b.buffer')).length, 1500);
  const offset = new Uint8Array(4096);
  offset.set([0x1A, 0x45, 0xDF, 0xA3], 100);
  assert.equal(toNarrationBuffer(offset.subarray(100, 2100)).length, 2000);
  assert.throws(() => toNarrationBuffer(webm(1023)), RangeError);
  assert.throws(() => toNarrationBuffer(webm(33554433)), RangeError);
  const wrongMagic = webm(); wrongMagic[3] = 0;
  assert.throws(() => toNarrationBuffer(wrongMagic), /audio/);
  for (const value of [new Uint16Array(2048), new Float32Array(2048), [0x1A, 0x45, 0xDF, 0xA3], 'webm', null, undefined, {}])
    assert.throws(() => toNarrationBuffer(value), TypeError);
});

test('voice files are written atomically with a fresh id and no leftover part file', async () => {
  const root = path.join(await mkdtemp(path.join(os.tmpdir(), 'Revu.Narration.')), 'Revu', 'Narration');
  try {
    const first = await writeNarrationFile(root, Buffer.from('first'));
    const second = await writeNarrationFile(root, Buffer.from('second'));
    assert.match(first.id, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    assert.notEqual(first.id, second.id);
    assert.equal(first.file, path.join(root, `${first.id}.webm`));
    assert.equal(await readFile(first.file, 'utf8'), 'first');
    assert.deepEqual((await readdir(root)).sort(), [`${first.id}.webm`, `${second.id}.webm`].sort());
  } finally { await rm(path.dirname(path.dirname(root)), { recursive: true, force: true }); }
});

async function withRoot(run) {
  const base = await mkdtemp(path.join(os.tmpdir(), 'Revu.NarrationSave.'));
  try { return await run(path.join(base, 'Narration')); } finally { await rm(base, { recursive: true, force: true }); }
}

test('save posts exactly the details plus the generated id with the narration deadline', async () => withRoot(async root => {
  const calls = [];
  const response = { ok: true, narration: { bookmarkId: 34 }, shareCleared: false };
  const backend = { async request(route, options) { calls.push([route, options]); return response; } };
  assert.equal(await saveNarration({ root, backend, bytes: webm(), meta: { ...meta } }), response);
  const [[route, options]] = calls;
  const [file] = await readdir(root);
  assert.equal(route, '/api/clip/narration/save');
  assert.equal(options.method, 'POST');
  assert.equal(options.timeoutMs, 280000);
  assert.deepEqual(options.body, { ...meta, narrationId: file.replace(/\.webm$/, '') });
  assert.deepEqual(Object.keys(options.body).sort(), [...Object.keys(meta), 'narrationId'].sort());
  assert.equal((await readFile(path.join(root, file))).length, 2048);
}));

test('a definitive sidecar refusal deletes the voice file; a lost response keeps it', async () => withRoot(async root => {
  assert.deepEqual([...DEFINITIVE_REJECTIONS].sort(), [400, 404, 409, 422, 503]);
  const refusal = Object.assign(new Error('Revu could not render the narrated clip. Your recording was not saved.'), { status: 422 });
  await assert.rejects(saveNarration({ root, backend: { request: async () => { throw refusal; } }, bytes: webm(), meta }),
    error => error === refusal);
  assert.deepEqual(await readdir(root), []);
  const timeout = new DOMException('The operation was aborted due to timeout', 'TimeoutError');
  await assert.rejects(saveNarration({ root, backend: { request: async () => { throw timeout; } }, bytes: webm(), meta }),
    error => error === timeout);
  assert.equal((await readdir(root)).length, 1);
  const serverError = Object.assign(new Error('Backend HTTP 500'), { status: 500 });
  await assert.rejects(saveNarration({ root, backend: { request: async () => { throw serverError; } }, bytes: webm(), meta }));
  assert.equal((await readdir(root)).length, 2);
}));

test('invalid details or audio are refused before anything is written or sent', async () => withRoot(async root => {
  let requests = 0;
  const backend = { request: async () => { requests++; return { ok: true }; } };
  await assert.rejects(saveNarration({ root, backend, bytes: webm(), meta: { ...meta, extra: true } }), TypeError);
  await assert.rejects(saveNarration({ root, backend, bytes: webm(100), meta }), RangeError);
  await assert.rejects(saveNarration({ root, backend, bytes: new Uint8Array(2048), meta }), TypeError);
  assert.equal(requests, 0);
  await assert.rejects(readdir(root), { code: 'ENOENT' });
}));
