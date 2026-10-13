import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import test from 'node:test';
import { createCommandGate, createQuitHandler, requireCleanBackendExit, shutdownOwnedApp } from '../shutdown.mjs';

const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

test('terminal admission fences competing update/replacement and new writes while draining accepted work', async () => {
  for (const firstAction of ['update', 'replacement']) {
    const gate = createCommandGate();
    const saving = deferred();
    const terminal = deferred();
    const calls = [];
    const pendingCommands = new Set();
    const accepted = gate({ terminal: false, pendingCommands }, async () => {
      await saving.promise;
      calls.push('saved');
      return { value: { ok: true } };
    });
    pendingCommands.add(accepted);
    const first = gate({ terminal: true, pendingCommands }, async () => {
      calls.push(firstAction);
      await terminal.promise;
      return { value: firstAction === 'update' ? null : { ok: true } };
    });
    pendingCommands.add(first);
    await assert.rejects(gate({ terminal: true, pendingCommands }, () => assert.fail('second terminal ran')), /already in progress/);
    await assert.rejects(gate({ terminal: false, pendingCommands }, () => assert.fail('new write ran')), /already in progress/);
    assert.deepEqual(calls, []);
    saving.resolve();
    await accepted;
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(calls, ['saved', firstAction]);
    terminal.resolve();
    await first;
    await assert.rejects(gate({ terminal: true, pendingCommands }, () => assert.fail('terminal fence released before exit')), /already in progress/);
  }
});

test('failed terminal commands release admission for correction and retry', async () => {
  const gate = createCommandGate();
  const pendingCommands = new Set();
  await assert.rejects(gate({ terminal: true, pendingCommands }, async () => { throw new Error('updater missing'); }), /updater missing/);
  const failed = await gate({ terminal: true, pendingCommands }, async () => ({ value: { ok: false } }));
  assert.equal(failed.value.ok, false);
  assert.equal(await gate({ terminal: false, pendingCommands }, async () => 'read available'), 'read available');
  assert.deepEqual(await gate({ terminal: true, pendingCommands }, async () => ({ value: { ok: true } })), { value: { ok: true } });
});

test('accepted writes finish before backend shutdown and updater or relaunch actions', async () => {
  const write = deferred();
  const backend = deferred();
  const calls = [];
  const finished = shutdownOwnedApp({ pendingCommands: new Set([write.promise]),
    abortEvents: () => calls.push('events stopped'), releaseMedia: () => calls.push('media released'),
    stopBackend: async () => { calls.push('backend stopping'); await backend.promise; calls.push('backend stopped'); },
    afterShutdown: () => calls.push('updater launched'),
  }).then(() => calls.push('relaunch allowed'));
  await Promise.resolve();
  assert.deepEqual(calls, ['events stopped']);
  write.resolve();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls, ['events stopped', 'media released', 'backend stopping']);
  backend.resolve();
  await finished;
  assert.deepEqual(calls, ['events stopped', 'media released', 'backend stopping', 'backend stopped',
    'updater launched', 'relaunch allowed']);
});

test('quit waits for ordinary writes but stops waiting on long jobs at the bound and names them', async () => {
  const write = deferred();
  const longJob = new Promise(() => {}); // a 290 s backfill that outlives the bound
  const pendingCommands = new Set([write.promise, longJob]);
  write.promise.then(() => pendingCommands.delete(write.promise));
  const calls = [];
  const finished = shutdownOwnedApp({ pendingCommands, drainTimeoutMs: 50,
    onAbandoned: remaining => calls.push(['abandoned', remaining]),
    abortEvents() {}, releaseMedia: () => calls.push(['media released']),
    stopBackend: async () => calls.push(['backend stopping']),
  });
  setTimeout(() => write.resolve(), 10);
  await finished;
  assert.deepEqual(calls, [['abandoned', [longJob]], ['media released'], ['backend stopping']]);

  const drained = [];
  await shutdownOwnedApp({ pendingCommands: new Set([Promise.resolve()]), drainTimeoutMs: 50,
    onAbandoned: () => drained.push('abandoned'), abortEvents() {}, releaseMedia() {}, stopBackend: async () => {} });
  assert.deepEqual(drained, []);
});

test('failed backend shutdown prevents both an updater and relaunch', async () => {
  const terminalActions = [];
  await assert.rejects(shutdownOwnedApp({ pendingCommands: [], abortEvents() {}, releaseMedia() {},
    stopBackend: async () => { throw new Error('unfinished save'); },
    afterShutdown: () => terminalActions.push('updater'),
  }).then(() => terminalActions.push('relaunch')), /unfinished save/);
  assert.deepEqual(terminalActions, []);
});

test('a second quit request cannot bypass a pending save drain or run shutdown twice', async () => {
  const saving = deferred();
  let began = 0, shutdowns = 0, exited = 0, prevented = 0;
  const quit = createQuitHandler({ begin: () => began++,
    shutdown: async () => { shutdowns++; await saving.promise; },
    failed: () => assert.fail('unexpected shutdown failure'), exit: () => exited++,
  });
  const first = quit({ preventDefault: () => prevented++ });
  const second = quit({ preventDefault: () => prevented++ });
  assert.equal(first, second);
  await Promise.resolve();
  assert.equal(prevented, 2);
  assert.equal(began, 1);
  assert.equal(shutdowns, 1);
  assert.equal(exited, 0);
  saving.resolve();
  await first;
  assert.equal(exited, 1);
});

test('an already-exited failing child cannot pass shutdown as successful', async () => {
  const child = spawn(process.execPath, ['-e', 'process.exit(7)'], { windowsHide: true, stdio: 'ignore' });
  await once(child, 'exit');
  assert.equal(child.exitCode, 7);
  assert.throws(() => requireCleanBackendExit(child, true), /did not finish cleanly/);
  assert.doesNotThrow(() => requireCleanBackendExit(child, false)); // Startup reports its own launch failure.
  assert.doesNotThrow(() => requireCleanBackendExit({ exitCode: 0, signalCode: null }, true));
  assert.throws(() => requireCleanBackendExit({ exitCode: null, signalCode: 'SIGTERM' }, true), /did not finish cleanly/);
  assert.throws(() => requireCleanBackendExit({ exitCode: 0, signalCode: null }, true, true), /did not finish cleanly/);
  // Only an exit the host forced is marked, so quit can tell it from a backend reporting a failed drain.
  assert.throws(() => requireCleanBackendExit({ exitCode: null, signalCode: 'SIGTERM' }, true, true), error => error.forced === true);
  assert.throws(() => requireCleanBackendExit(child, true), error => error.forced === false);
});
