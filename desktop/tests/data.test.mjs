import assert from 'node:assert/strict';
import test from 'node:test';
import { createSnapshotReader } from '../ui/data.mjs';
import { createPlatform } from '../ui/platform/index.mjs';

test('desktop snapshots preserve parameters and results without loading preview fixtures', async () => {
  const calls = [];
  const expected = { subject: { gameId: 42 }, nextUnreviewedGameId: 43 };
  const read = createSnapshotReader({
    resolveInvoke: async () => async (command, args) => { calls.push([command, args]); return expected; },
    fetchJson: () => assert.fail('desktop host requested a preview fixture'),
  });
  assert.equal(await read('get_review', 'sample-review.json', { gameId: 42 }), expected);
  assert.deepEqual(calls, [['get_review', { gameId: 42 }]]);
});

for (const stage of ['bridge initialization', 'backend request']) {
  test(`${stage} errors remain visible instead of silently showing sample data`, async () => {
    const error = new Error('Sidecar is unavailable');
    const read = createSnapshotReader({
      resolveInvoke: async () => {
        if (stage === 'bridge initialization') throw error;
        return async () => { throw error; };
      },
      fetchJson: () => assert.fail('failed desktop request fell back to preview data'),
    });
    await assert.rejects(read('get_games', 'sample-games.json'), value => value === error);
  });
}

test('preview reads the requested fixture on each refresh', async () => {
  const paths = [];
  const read = createSnapshotReader({
    resolveInvoke: async () => null,
    fetchJson: async path => { paths.push(path); return { ok: true, json: async () => ({ count: paths.length }) }; },
  });
  assert.deepEqual(await read('get_games', 'sample-games.json', { view: 'queue', page: 0 }), { count: 1 });
  assert.deepEqual(await read('get_games', 'sample-games.json', { view: 'all', page: 1 }), { count: 2 });
  assert.deepEqual(paths, ['./sample-games.json', './sample-games.json']);
});

test('missing preview fixture retains the page-specific HTTP error', async () => {
  const read = createSnapshotReader({ resolveInvoke: async () => null,
    fetchJson: async () => ({ ok: false, status: 404, json: () => assert.fail('failed HTTP response was parsed') }) });
  await assert.rejects(read('get_objective', 'sample-objective.json'), { message: 'sample-objective.json 404' });
});

test('malformed preview JSON is not treated as an empty successful snapshot', async () => {
  const error = new SyntaxError('Invalid fixture');
  const read = createSnapshotReader({ resolveInvoke: async () => null,
    fetchJson: async () => ({ ok: true, json: async () => { throw error; } }) });
  await assert.rejects(read('get_patterns', 'sample-patterns.json'), value => value === error);
});

test('a hosted page whose bridge is missing shows an error instead of fabricated sample data', async () => {
  const scope = new EventTarget();
  scope.location = { origin: 'revu-app://ui', protocol: 'revu-app:', search: '' };
  scope.top = scope;
  const client = createPlatform({ scope });
  const read = createSnapshotReader({ resolveInvoke: client.getInvoke,
    fetchJson: () => assert.fail('a hosted page loaded a preview fixture') });
  await assert.rejects(read('get_dashboard', 'sample-dashboard.json'), /could not connect to its desktop host/);
  client.dispose();
});

test('browser previews outside the app still read their fixtures', async () => {
  const scope = new EventTarget();
  scope.location = { origin: 'http://localhost:5173', protocol: 'http:', search: '' };
  scope.top = scope;
  const client = createPlatform({ scope });
  const read = createSnapshotReader({ resolveInvoke: client.getInvoke,
    fetchJson: async path => ({ ok: true, json: async () => ({ path }) }) });
  assert.deepEqual(await read('get_dashboard', 'sample-dashboard.json'), { path: './sample-dashboard.json' });
  client.dispose();
});
