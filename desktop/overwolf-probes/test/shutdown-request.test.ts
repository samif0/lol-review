import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { shutdownRequestPoller } from '../src/shutdown-request.ts';

test('only an exact owned request authorizes shutdown; failed reads remain retryable', async () => {
  let result = 'not-json', shuts = 0, reject = false;
  const poller = shutdownRequestPoller('fixture', 'owned', () => { shuts++; }, async () => {
    if (reject) throw new Error('private-file');
    return result;
  });
  await poller.check(); result = '{"token":"someone-else"}'; await poller.check();
  result = '{"token":"owned"}'; reject = true; await poller.check(); assert.equal(shuts, 0);
  reject = false; await poller.check(); assert.equal(shuts, 1); poller.dispose();
});

for (const interrupt of ['timeout', 'dispose'] as const) {
  test(`${interrupt} aborts the read, ignores a late owned request, and never queues overlapping reads`, async () => {
    let finish!: (value: string) => void, signal: AbortSignal | undefined;
    let reads = 0, shuts = 0;
    const poller = shutdownRequestPoller('fixture', 'owned', () => { shuts++; }, async (_file, s) => {
      reads++; signal = s;
      return new Promise<string>(resolve => { finish = resolve; });
    }, 20);
    const checking = poller.check(); await poller.check(); assert.equal(reads, 1);
    if (interrupt === 'timeout') await delay(40); else poller.dispose();
    assert.equal(signal?.aborted, true); await poller.check(); assert.equal(reads, 1);
    finish('{"token":"owned"}'); await checking; assert.equal(shuts, 0); poller.dispose();
    await poller.check(); assert.equal(reads, 1);
  });
}
