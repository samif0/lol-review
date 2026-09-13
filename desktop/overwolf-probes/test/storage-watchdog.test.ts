import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { inspectCaptureStorage, storageWatchdog, type CaptureStorage } from '../src/storage-watchdog.ts';

const healthy = { availableBytes: 10 * 1024 ** 3, captureBytes: 100 };
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (reason: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function fixture(inspect: () => Promise<CaptureStorage>) {
  const reasons: string[] = [];
  let active = true, elapsedMs = 1000, calls = 0;
  const watchdog = storageWatchdog({ root: 'fixture', active: () => active, elapsedMs: () => elapsedMs,
    onStop: reason => reasons.push(reason), inspect: () => { calls++; return inspect(); }, timeoutMs: 20 });
  return { watchdog, reasons, calls: () => calls, end: () => { active = false; }, exceedDuration: () => { elapsedMs = 7_200_001; } };
}

test('slow storage inspection never queues overlapping scans and permits another completed sample', async () => {
  const pending = deferred<CaptureStorage>();
  const f = fixture(() => pending.promise);
  const first = f.watchdog.check();
  await Promise.all([f.watchdog.check(), f.watchdog.check()]);
  assert.equal(f.calls(), 1);
  pending.resolve(healthy); await first;
  await f.watchdog.check(); assert.equal(f.calls(), 2); assert.deepEqual(f.reasons, []);
  f.watchdog.dispose();
});

test('unresponsive storage requests stop once and ignores its late result without accumulating I/O', async () => {
  const pending = deferred<CaptureStorage>();
  const f = fixture(() => pending.promise);
  const first = f.watchdog.check();
  await delay(40);
  await f.watchdog.check(); assert.equal(f.calls(), 1); assert.deepEqual(f.reasons, ['storage-unavailable']);
  pending.resolve({ ...healthy, availableBytes: 0 }); await first;
  assert.deepEqual(f.reasons, ['storage-unavailable']); f.watchdog.dispose();
});

test('elapsed capture limit remains enforced while filesystem work is still pending', async () => {
  const pending = deferred<CaptureStorage>(); const f = fixture(() => pending.promise);
  const first = f.watchdog.check(); f.exceedDuration(); await f.watchdog.check();
  assert.deepEqual(f.reasons, ['capture-limit-stop']); assert.equal(f.calls(), 1);
  f.end(); pending.resolve(healthy); await first; f.watchdog.dispose();
});

for (const action of ['end', 'dispose'] as const) {
  test(`${action} fences late filesystem errors and its timeout`, async () => {
    const pending = deferred<CaptureStorage>(); const f = fixture(() => pending.promise);
    const first = f.watchdog.check();
    if (action === 'end') f.end(); else f.watchdog.dispose();
    await delay(40); pending.reject(new Error('private-storage-path')); await first;
    await f.watchdog.check(); assert.deepEqual(f.reasons, []); assert.equal(f.calls(), 1); f.watchdog.dispose();
  });
}

for (const [name, inspect, reason] of [
  ['low space', async () => ({ ...healthy, availableBytes: 2 * 1024 ** 3 - 1 }), 'capture-limit-stop'],
  ['large recording', async () => ({ ...healthy, captureBytes: 20 * 1024 ** 3 + 1 }), 'capture-limit-stop'],
  ['failed scan', async () => { throw new Error('private-storage-path'); }, 'storage-unavailable'],
  ['invalid metadata', async () => ({ ...healthy, captureBytes: NaN }), 'storage-unavailable']
] as const) {
  test(`${name} requests a safe stop`, async () => {
    const f = fixture(inspect); await f.watchdog.check(); assert.deepEqual(f.reasons, [reason]); f.watchdog.dispose();
  });
}

test('filesystem inspection counts only capture files and rejects unexpected capture paths', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'revu-storage-watchdog-'));
  try {
    await writeFile(path.join(root, 'capture.mp4'), Buffer.alloc(24));
    await writeFile(path.join(root, 'capture-settings.json'), Buffer.alloc(100));
    await writeFile(path.join(root, 'observations.jsonl'), Buffer.alloc(100));
    const result = await inspectCaptureStorage(root);
    assert.equal(result.captureBytes, 24); assert.ok(Number.isFinite(result.availableBytes));
    await mkdir(path.join(root, 'capture-unexpected'));
    await assert.rejects(inspectCaptureStorage(root), /unexpected-capture-path/);
  } finally { await rm(root, { recursive: true }); }
});
