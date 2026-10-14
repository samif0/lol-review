import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';
import { COMMANDS, validateCommand } from '../ui/platform/commands.mjs';
import { createPlatform, hostError, trustedTop, resolveAssetUrl } from '../ui/platform/index.mjs';

function page(origin = 'https://revu.test', { protocol, search = '' } = {}) {
  const scope = new EventTarget();
  scope.location = { origin, protocol: protocol ?? (URL.canParse(origin) ? new URL(origin).protocol : ''), search };
  scope.top = scope;
  return scope;
}

function host(overrides = {}) {
  return { version: 1, kind: 'electron',
    capabilities: { commands: true, events: true, media: true, windows: true },
    invoke: async () => ({ ok: true }), listen: async () => () => {},
    resolveMedia: (path) => `media:${encodeURIComponent(path)}`,
    ...overrides,
  };
}

test('browser previews have no desktop capabilities and cannot invoke native commands', async () => {
  const client = createPlatform({ scope: page() });
  assert.equal(await client.getInvoke(), null);
  assert.equal(await client.getListen(), null);
  assert.equal(await client.getMedia(), null);
  assert.equal(await client.getWindow(), null);
  await assert.rejects(client.invoke('get_config'), /capability unavailable/);
  client.dispose();
});

test('media URLs come only from the Electron bridge and expire when the page is disposed', async () => {
  const scope = page();
  scope.revuDesktop = host();
  const client = createPlatform({ scope });
  const media = await client.getMedia();
  const path = 'C:\\Vidéos\\한글 game #1.mp4';
  assert.equal(resolveAssetUrl(media, path), 'media:' + encodeURIComponent(path));
  assert.equal(resolveAssetUrl(media, null), null);
  assert.equal(resolveAssetUrl(media, 'invalid\0file'), null);
  client.dispose();
  assert.equal(resolveAssetUrl(media, path), null);
});

test('trusted iframe deliberately shares the top bridge and ignores child lookalikes', async () => {
  const top = page();
  const calls = [];
  top.revuDesktop = host({ invoke: async (command, args) => { calls.push([command, args]); return { saved: true }; } });
  const child = page();
  child.top = top;
  child.revuDesktop = host({ invoke() { throw new Error('child bridge must not be used'); } });
  const client = createPlatform({ scope: child });
  assert.deepEqual(await client.invoke('save_review', { payload: { gameId: 9, note: 'Keep this note' } }), { saved: true });
  assert.deepEqual(calls, [['save_review', { payload: { gameId: 9, note: 'Keep this note' } }]]);
  client.dispose();
});

test('cross-origin and opaque-origin iframes cannot inherit the desktop bridge', async () => {
  const top = page();
  top.revuDesktop = host();
  for (const origin of ['https://untrusted.example', 'null']) {
    const child = page(origin);
    child.top = top;
    assert.equal(trustedTop(child), null);
    const client = createPlatform({ scope: child });
    assert.equal(await client.getInvoke(), null);
    client.dispose();
  }
  assert.equal(trustedTop({ get top() { throw new Error('cross-origin access'); } }), null);
});

test('a hosted page without its bridge is a visible host failure, never a browser preview', async () => {
  const shell = page('revu-app://ui');
  const child = page('revu-app://ui');
  child.top = shell;
  for (const scope of [shell, child]) {
    const client = createPlatform({ scope });
    await assert.rejects(client.getInvoke(), /could not connect to its desktop host/);
    await assert.rejects(client.invoke('get_config'), /could not connect to its desktop host/);
    client.dispose();
  }
  // The UI smoke deliberately opts the shell into preview fixtures.
  const preview = createPlatform({ scope: page('revu-app://ui', { search: '?preview=samples' }) });
  assert.equal(await preview.getInvoke(), null);
  preview.dispose();
});

test('Electron IPC wrappers are stripped once so pages show the host sentence', async () => {
  const wrapped = new Error("Error invoking remote method 'revu:command': Error: Revu couldn't open its database: the file revu.db is marked read-only.");
  const client = createPlatform({ scope: page(), adapterFactory: () => host({
    capabilities: { commands: true, narration: true },
    invoke: async () => { throw wrapped; },
    saveNarration: async () => { throw new Error("Error invoking remote method 'revu:narration-save': TypeError: Invalid narration sync"); },
  }) });
  await assert.rejects(client.invoke('get_vod', { gameId: 9 }), error =>
    error.message === "Revu couldn't open its database: the file revu.db is marked read-only." && error.cause === wrapped);
  await assert.rejects(client.saveNarration(new Uint8Array(4), {}), { message: 'Invalid narration sync' });
  client.dispose();
  const plain = new Error('Error: kept as is');
  assert.equal(hostError(plain), plain);
  assert.equal(hostError('text failure'), 'text failure');
  assert.equal(hostError(new Error("Error invoking remote method 'revu:command': Error: Error: inner")).message, 'Error: inner');
});

test('invalid preload version is an explicit host failure, never sample-data fallback', async () => {
  const scope = page();
  scope.revuDesktop = host({ version: 2 });
  const client = createPlatform({ scope });
  await assert.rejects(client.getInvoke(), /Unsupported desktop bridge/);
  client.dispose();
});

test('commands preserve read/write envelopes and backend failures across review flows', async () => {
  const cases = [
    ['get_objective', { id: 3 }], ['get_vod', { gameId: 9 }],
    ['create_matchup_from_game', { payload: { gameId: 8 } }],
    ['get_pregame', { myChampion: 'Kai\'Sa', participantMap: '{"3":"Ally"}' }],
    ['save_event_correction', { payload: { gameId: 9, correctionId: 2, gameTimeSeconds: 123.5 } }],
    ['add_bookmark', { payload: { gameId: 9, timeSeconds: 100, note: 'spacing' } }],
    ['save_config', { payload: { clipsFolder: 'D:\\Clips', minimizeDuringGame: false } }],
    ['auth_login', { payload: { email: 'test@example.invalid', password: 'fixture-only' } }],
    ['save_export_file', { fileName: 'review.md', markdown: '# Review\n' }],
    ['copy_text_to_clipboard', { text: '# Review\nKeep the wave near turret.' }],
  ];
  const calls = [];
  const client = createPlatform({ scope: page(), adapterFactory: () => host({
    invoke: async (command, args) => { calls.push([command, args]); return { ok: true, command }; },
  }) });
  for (const [command, args] of cases) assert.deepEqual(await client.invoke(command, args), { ok: true, command });
  assert.deepEqual(calls, cases);
  client.dispose();
  const failure = 'sidecar HTTP 409: correction conflict';
  const failed = createPlatform({ scope: page(), adapterFactory: () => host({ invoke: async () => { throw failure; } }) });
  await assert.rejects(failed.invoke('get_vod', { gameId: 9 }), (error) => error === failure);
  failed.dispose();
});

test('allowlist rejects unknown commands, extra keys and invalid casing/types before IPC', async () => {
  let calls = 0;
  const client = createPlatform({ scope: page(), adapterFactory: () => host({ invoke() { calls++; } }) });
  for (const [command, args] of [
    ['toString', {}], ['read_file', { path: 'C:\\secret' }], ['scan_vods', { path: 'C:\\secret' }],
    ['get_vod', { game_id: 2 }], ['get_vod', { gameId: '2' }],
    ['get_vod', { gameId: NaN }], ['get_vod', { gameId: 1, route: '/arbitrary' }],
    ['save_review', { payload: [] }], ['get_config', null], ['save_review', {}],
    ['copy_text_to_clipboard', {}], ['copy_text_to_clipboard', { text: 42 }],
    ['copy_text_to_clipboard', { text: 'Review', format: 'html' }],
  ]) await assert.rejects(client.invoke(command, args));
  assert.equal(calls, 0);
  assert.equal(validateCommand('get_review', { gameId: null }), COMMANDS.get_review);
  assert.equal(validateCommand('get_review'), COMMANDS.get_review);
  client.dispose();
});

test('listener disposal stops delivery, unsubscribes once and does not stop the host SSE stream', async () => {
  const scope = page();
  let handler;
  let unsubscribed = 0;
  const events = [];
  const client = createPlatform({ scope, adapterFactory: () => host({ listen: async (name, callback) => {
    assert.equal(name, 'lcu-event'); handler = callback; return () => { unsubscribed++; };
  } }) });
  const unsubscribe = await client.listen('lcu-event', (event) => events.push(event));
  const event = { payload: { type: 'gameEnded', payload: { gameId: 9 } } };
  handler(event);
  scope.dispatchEvent(new Event('pagehide'));
  handler(event);
  unsubscribe();
  client.dispose();
  assert.deepEqual(events, [event]);
  assert.equal(unsubscribed, 1);
  await assert.rejects(client.invoke('get_config'), /disposed/);
});

test('navigation during native registration releases the late listener and drops late events', async () => {
  let complete;
  let handler;
  let started;
  const registered = new Promise((resolve) => { started = resolve; });
  let unsubscribed = 0;
  const scope = page();
  const client = createPlatform({ scope, adapterFactory: () => host({ listen: (name, callback) => {
    handler = callback; started(); return new Promise((resolve) => { complete = resolve; });
  } }) });
  const listening = client.listen('lcu-event', () => assert.fail('disposed page received event'));
  await registered;
  scope.dispatchEvent(new Event('pagehide'));
  handler({ payload: { type: 'gameEnded' } });
  complete(() => { unsubscribed++; });
  const unsubscribe = await listening;
  unsubscribe();
  assert.equal(unsubscribed, 1);
});

test('one failed disposer does not prevent cleanup of other page listeners', async () => {
  let disposed = 0;
  const client = createPlatform({ scope: page(), adapterFactory: () => host({ listen: async () => () => {
    disposed++;
    throw new Error('native listener already gone');
  } }) });
  await client.listen('lcu-event', () => {});
  await client.listen('lcu-event', () => {});
  client.dispose();
  assert.equal(disposed, 2);
});

test('pagehide releases events without suppressing existing review/matchup draft flushes', async () => {
  const scope = page();
  const calls = [];
  const client = createPlatform({ scope, adapterFactory: () => host({ invoke: async (command, args) => calls.push([command, args]) }) });
  let draft;
  scope.addEventListener('pagehide', () => {
    draft = client.getInvoke().then((invoke) => invoke('save_review_draft', { payload: { gameId: 42, note: 'final edit' } }));
  });
  scope.dispatchEvent(new Event('pagehide'));
  await draft;
  assert.deepEqual(calls, [['save_review_draft', { payload: { gameId: 42, note: 'final edit' } }]]);
  client.dispose();
});

test('back/forward page restoration resumes subscriptions and rejects old deliveries', async () => {
  const scope = page();
  const handlers = [];
  const events = [];
  let released = 0;
  const client = createPlatform({ scope, adapterFactory: () => host({ listen: async (_event, callback) => {
    handlers.push(callback);
    return () => { released++; };
  } }) });
  await client.listen('lcu-event', (event) => events.push(event));
  handlers[0]('before');
  scope.dispatchEvent(new Event('pagehide'));
  scope.dispatchEvent(new Event('pageshow'));
  await Promise.resolve();
  handlers[0]('stale');
  handlers[1]('restored');
  assert.deepEqual(events, ['before', 'restored']);
  assert.equal(released, 1);
  client.dispose();
  assert.equal(released, 2);
});

test('events, native window methods, links and recorder operations remain narrow', async () => {
  const calls = [];
  const client = createPlatform({ scope: page(), adapterFactory: () => host({ window: {
    minimize: async () => calls.push('minimize'), arbitraryMethod: () => assert.fail(),
  } }) });
  const win = await client.getWindow();
  await win.minimize();
  assert.deepEqual(calls, ['minimize']);
  assert.equal(win.arbitraryMethod, undefined);
  await assert.rejects(win.close(), /Window action unavailable/);
  await assert.rejects(client.listen('arbitrary-ipc', () => {}), /Unsupported desktop event/);
  assert.equal(client.recorderStatus, undefined); // Recording status is the get_recording_status command.
  assert.equal(win.startDragging, undefined); // app-region CSS owns frameless dragging.
  await assert.rejects(client.openExternal('file:///C:/private'), /Unsupported external URL/);
  await assert.rejects(client.openExternal('https://revu.example'), /capability unavailable/);
  client.dispose();
});

test('command declarations keep immutable envelopes, required arguments and exceptional deadlines', () => {
  assert.equal(Object.keys(COMMANDS).length, 115);
  for (const name of ['review_vod', 'open_review', 'take_next_step']) assert.equal(COMMANDS[name], undefined, name);
  assert.ok(Object.isFrozen(COMMANDS));
  for (const [name, definition] of Object.entries(COMMANDS)) {
    assert.ok(Object.isFrozen(definition), name);
    assert.ok(Object.isFrozen(definition.args), name);
    const args = {};
    for (const [key, type] of Object.entries(definition.args)) {
      if (!type.endsWith('?')) args[key] = type === 'integer' ? 7 : type === 'object' ? {} : 'fixture';
    }
    assert.equal(validateCommand(name, args), definition);
    for (const key of Object.keys(args)) {
      const missing = { ...args }; delete missing[key];
      assert.throws(() => validateCommand(name, missing), /Invalid argument/, name + '.' + key);
    }
    if (definition.query) {
      assert.ok(Object.isFrozen(definition.query));
      assert.deepEqual(Object.keys(definition.query), Object.keys(definition.args));
    }
    if (definition.positiveQuery) {
      assert.ok(Object.isFrozen(definition.positiveQuery));
      for (const key of definition.positiveQuery) assert.equal(definition.args[key], 'integer?');
    }
  }
  assert.deepEqual(COMMANDS.auth_logout.args, {});
  assert.deepEqual(COMMANDS.run_backfill.args, {});
  assert.deepEqual(COMMANDS.scan_vods.args, {});
  assert.equal(COMMANDS.scan_vods.route, '/api/settings/scan-vods');
  assert.equal(COMMANDS.scan_vods.method, 'POST');
  assert.equal(COMMANDS.scan_vods.timeoutMs, 120_000);
  assert.deepEqual(COMMANDS.recover_missing_games.args, {});
  assert.equal(COMMANDS.recover_missing_games.route, '/api/settings/recover-games');
  assert.equal(COMMANDS.recover_missing_games.method, 'POST');
  assert.equal(COMMANDS.recover_missing_games.timeoutMs, 120_000);
  assert.equal(COMMANDS.share_clip.timeoutMs, 60_000);
  assert.equal(COMMANDS.extract_clip.timeoutMs, 280_000);
  assert.equal(COMMANDS.mix_clip_narration.timeoutMs, 280_000);
  assert.equal(COMMANDS.run_backfill.timeoutMs, 290_000);
  // Accepted sidecar work: long enough for real batches, and a timeout never stops the work.
  assert.equal(COMMANDS.auto_clip_objectives.timeoutMs, 280_000);
  assert.equal(COMMANDS.download_update.timeoutMs, 280_000);
  assert.deepEqual(COMMANDS.download_update.args, {});
  // The sidecar saves the full export itself; the renderer only learns where it went.
  assert.equal(COMMANDS.export_review_data.method, 'POST');
  assert.equal(COMMANDS.export_review_data.route, '/api/settings/export');
  assert.deepEqual(COMMANDS.export_review_data.args, {});
  assert.equal(COMMANDS.export_review_data.timeoutMs, 120_000);
  assert.ok(!Object.hasOwn(COMMANDS, 'get_export_markdown'));
  assert.equal(COMMANDS.open_export_folder.method, 'NATIVE');
  assert.deepEqual(COMMANDS.open_export_folder.args, {});
  // The theme mirror is host-only: no sidecar route, one object payload the host validates.
  assert.equal(COMMANDS.set_window_appearance.method, 'NATIVE');
  assert.equal(COMMANDS.set_window_appearance.route, null);
  assert.deepEqual(COMMANDS.set_window_appearance.args, { payload: 'object' });
  assert.throws(() => validateCommand('set_window_appearance', { payload: '#21201e' }), /Invalid argument: payload/);
  assert.throws(() => validateCommand('set_window_appearance', { payload: {}, theme: 'warm' }), /Unexpected argument: theme/);
  // C0.7: Node fetch gives up waiting for response headers after 300 s.
  for (const [name, definition] of Object.entries(COMMANDS)) {
    if (definition.timeoutMs !== undefined) assert.ok(definition.timeoutMs <= 290_000, name);
  }
  assert.equal(COMMANDS.get_clip_share_status.route, '/api/clip/share-status');
  assert.deepEqual(COMMANDS.get_clip_share_status.args, { bookmarkId: 'integer' });
  assert.equal(COMMANDS.get_clip_share_status.method, 'GET');
  for (const name of ['mix_clip_narration', 'delete_clip_narration', 'transcribe_clip_narration']) {
    assert.equal(COMMANDS[name].method, 'POST', name);
    assert.deepEqual(COMMANDS[name].args, { payload: 'object' }, name);
  }
  assert.equal(COMMANDS.delete_clip_narration.route, '/api/clip/narration/delete');
  assert.equal(COMMANDS.transcribe_clip_narration.route, '/api/clip/narration/transcribe');
  for (const name of ['get_microphone_access', 'open_microphone_settings']) {
    assert.equal(COMMANDS[name].method, 'NATIVE', name);
    assert.deepEqual(COMMANDS[name].args, {}, name);
  }
  // The voice upload is host-only: it never becomes a renderer command.
  assert.ok(!Object.values(COMMANDS).some(definition => definition.route === '/api/clip/narration/save'));
  // Speed up names a game only: the host resolves the recording and alone calls /api/recording/*.
  for (const name of ['get_recording_optimization', 'optimize_recording']) {
    assert.equal(COMMANDS[name].method, 'NATIVE', name);
    assert.deepEqual(COMMANDS[name].args, { gameId: 'integer' }, name);
    assert.throws(() => validateCommand(name, { gameId: 4, filePath: 'C:\\capture.mp4' }), /Unexpected argument: filePath/);
    assert.throws(() => validateCommand(name, { gameId: '4' }), /Invalid argument: gameId/);
  }
  // Settings lists slow recordings with no arguments at all: the answer names games, never a path.
  assert.equal(COMMANDS.list_recording_optimizations.method, 'NATIVE');
  assert.equal(COMMANDS.list_recording_optimizations.route, null);
  assert.deepEqual(COMMANDS.list_recording_optimizations.args, {});
  assert.equal(validateCommand('list_recording_optimizations', {}), COMMANDS.list_recording_optimizations);
  for (const extra of [{ gameId: 4 }, { filePath: 'C:\\capture.mp4' }, { root: 'C:\\Revu' }])
    assert.throws(() => validateCommand('list_recording_optimizations', extra), /Unexpected argument/);
  assert.ok(!Object.values(COMMANDS).some(definition => definition.route?.startsWith('/api/recording')));
  assert.equal(COMMANDS.reset_all_data.sideEffect, 'restart-after-write');
  assert.equal(COMMANDS.restore_backup.sideEffect, 'restart-after-write');
  assert.equal(COMMANDS.apply_update.sideEffect, 'apply-update-stop-sidecar-exit');
});

test('narration saves require the host capability and accept only bounded byte views', async () => {
  const saved = [];
  const meta = { gameId: 4, bookmarkId: 9, mimeType: 'audio/webm', offsetMs: 0, durationMs: 1500,
    gameVolume: 0.8, narrationVolume: 1, duck: true };
  const without = createPlatform({ scope: page(), adapterFactory: () => host() });
  await assert.rejects(without.saveNarration(new Uint8Array(4), meta), /capability unavailable: narration/);
  assert.equal(await without.narrationAvailable(), false);
  without.dispose();
  const client = createPlatform({ scope: page(), adapterFactory: () => host({
    capabilities: { commands: true, narration: true },
    saveNarration: async (bytes, value) => { saved.push([bytes, value]); return { ok: true }; },
  }) });
  assert.equal(await client.narrationAvailable(), true);
  for (const bad of [new Float32Array(4), new Uint16Array(4), [1, 2, 3], 'bytes', null, undefined, {},
    { byteLength: 4 }, new SharedArrayBuffer(4), ArrayBuffer, new Uint8Array(33554433),
    new ArrayBuffer(33554433), vm.runInNewContext('new ArrayBuffer(33554433)')]) {
    await assert.rejects(client.saveNarration(bad, meta), /at most 32 MiB/);
  }
  assert.equal(saved.length, 0);
  // A page iframe hands over its own realm's Uint8Array.
  const foreign = vm.runInNewContext('new Uint8Array([26, 69, 223, 163])');
  assert.deepEqual(await client.saveNarration(foreign, meta), { ok: true });
  const view = new Uint8Array(33554432);
  assert.deepEqual(await client.saveNarration(view, meta), { ok: true });
  // The context-bridge fallback passes the backing ArrayBuffer, from either realm.
  const buffer = new ArrayBuffer(4);
  assert.deepEqual(await client.saveNarration(buffer, meta), { ok: true });
  const foreignBuffer = vm.runInNewContext('new Uint8Array([26, 69, 223, 163]).buffer');
  assert.deepEqual(await client.saveNarration(foreignBuffer, meta), { ok: true });
  assert.deepEqual(await client.saveNarration(new ArrayBuffer(33554432), meta), { ok: true });
  assert.equal(saved[0][0], foreign); assert.equal(saved[1][0], view); assert.equal(saved[0][1], meta);
  assert.equal(saved[2][0], buffer); assert.equal(saved[3][0], foreignBuffer); assert.equal(saved.length, 5);
  client.dispose();
  await assert.rejects(client.saveNarration(foreign, meta), /disposed/);
});

test('UI modules stay behind the Electron boundary and contain no removed host dependency', async () => {
  const ui = new URL('../ui/', import.meta.url);
  const roots = [ui, new URL('platform/', ui)];
  for (const root of roots) {
    const files = (await readdir(root)).filter(path => /\.(js|mjs)$/.test(path));
    for (const path of files) {
      const source = await readFile(new URL(path, root), 'utf8');
      assert.doesNotMatch(source, /@tauri-apps\/|__TAURI(?:__|_INTERNALS__)/, path);
      assert.doesNotMatch(source, /(?:from|import\()\s*['"](?:electron|node:)/, path);
    }
  }
});
