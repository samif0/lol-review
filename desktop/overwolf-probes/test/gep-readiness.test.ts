import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { connectGep } from '../src/gep.ts';
import { FEATURES, LEAGUE_ID, ObservationFilter } from '../src/policy.ts';

// Payload contracts/examples reviewed 2026-09-12 against official Electron docs:
// https://dev.overwolf.com/ow-electron/live-game-data-gep/supported-games/league-of-legends/
// https://dev.overwolf.com/ow-electron/reference/Overwolf-electron-APIs/gep/interfaces/OverwolfGameEventPackage/
// The queueId table says game_info; its example is launcher 10902, not League
// 5426. That launcher example must never authorize this game's observations.
const info = (feature: string, category: string, key: string, value: unknown) => ({ gameId: LEAGUE_ID, feature, category, key, value });
const event = (feature: string, key: string, value: unknown) => ({ gameId: LEAGUE_ID, feature, key, value });
const queue = (value: unknown = '420') => info('matchState', 'game_info', 'queueId', value);
const clock = (value: unknown = 223) => event('counters', 'match_clock', value);
const damage = () => event('damage', 'physical_damage_taken', '8.452381');
const flush = () => new Promise<void>(resolve => setImmediate(resolve));
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function fixture() {
  const rows: Record<string, unknown>[] = [], statuses: string[] = [], calls: unknown[][] = [];
  let enabled = 0;
  const gep = Object.assign(new EventEmitter(), {
    getFeatures: async (id: number): Promise<unknown> => { calls.push(['features', id]); return [...FEATURES, 'chat', 'teams']; },
    setRequiredFeatures: async (id: number, features: string[]) => { calls.push(['subscribe', id, features]); },
    getInfo: async (id: number): Promise<unknown> => { calls.push(['info', id]); return null; }
  });
  const connection = connectGep(gep as never, { write: (row: Record<string, unknown>) => rows.push(row) } as never, value => statuses.push(value));
  const emitInfo = (data: unknown, id = LEAGUE_ID) => gep.emit('new-info-update', {}, id, data);
  const emitEvent = (data: unknown, id = LEAGUE_ID) => gep.emit('new-game-event', {}, id, data);
  return { gep, rows, statuses, calls, connection, emitInfo, emitEvent, enabled: () => enabled,
    detect: (id = LEAGUE_ID) => gep.emit('game-detected', { enable() { enabled++; } }, id, 'fixture'),
    observations: () => rows.filter(row => row.kind === 'observation') };
}

test('real Electron channel/category/game identity is required before queue authorization', () => {
  const f = new ObservationFilter();
  for (const packet of [
    { gameId: 10902, feature: 'lobby_info', category: 'lobby_info', key: 'queueId', value: '3140' },
    { ...queue(), category: 'lobby_info' }, { ...queue(), category: undefined },
    { ...queue(), gameId: 10902 }
  ]) assert.equal(f.accept(packet, 1, 'info'), undefined);
  assert.equal(f.accept(event('matchState', 'queueId', '420'), 2, 'event'), undefined);
  assert.equal(f.accept(damage(), 3, 'event'), undefined);
  assert.equal(f.accept(queue(), 4, 'info')?.value, 420);
  assert.equal(f.accept(info('damage', 'damage', 'total_damage_dealt', '76.242004'), 5, 'info')?.value, 76.242004);
  assert.equal(f.accept(damage(), 6, 'event')?.value, 8.452381);
});

test('numeric clock receipt age remains explicit across duplicate queue and clock updates', () => {
  const f = new ObservationFilter();
  f.accept(queue(), 10, 'info'); f.accept(clock(), 20, 'event');
  assert.equal(f.accept(queue(), 25, 'info')?.gameClock, 223);
  assert.equal(f.accept(clock(), 30, 'event')?.clockAgeMs, 10);
  const next = f.accept(damage(), 10_020, 'event');
  assert.equal(next?.gameClock, 223);
  assert.equal(next?.clockAgeMs, 10_000);
});

for (const [name, reset] of Object.entries({
  'unsupported queue': queue(2300), 'queue sentinel': queue(null),
  'match reset': info('matchState', 'game_info', 'matchStarted', false),
  'match sentinel': info('matchState', 'game_info', 'matchStarted', null)
})) test(`${name} revokes queue and clock instead of retaining old scope`, () => {
  const f = new ObservationFilter(); f.accept(queue(), 1, 'info'); f.accept(clock(), 2, 'event');
  f.accept(reset, 3, 'info');
  assert.equal(f.queue, undefined); assert.equal(f.clock, null);
  assert.equal(f.accept(damage(), 4, 'event'), undefined);
});

test('changed match identity, backward clock, null clock and reordered receipts cannot join matches', () => {
  const f = new ObservationFilter(); f.accept(queue(), 1, 'info');
  const match = (id: unknown) => info('matchState', 'game_info', 'matchId', id);
  assert.equal(f.accept(match('7632720723'), 2, 'info'), undefined);
  assert.ok(f.accept(damage(), 3, 'event')); // initial identity may arrive after queue
  f.accept(match('7632720724'), 4, 'info'); assert.equal(f.queue, undefined);
  f.accept(queue(), 5, 'info'); f.accept(clock(30), 6, 'event');
  f.accept(clock(29), 7, 'event'); assert.equal(f.queue, undefined); assert.equal(f.clock, null);
  f.accept(queue(), 8, 'info'); f.accept(clock(null), 9, 'event'); assert.equal(f.queue, undefined);
  f.accept(queue(), 10, 'info'); f.accept(damage(), 9, 'event'); assert.equal(f.queue, undefined);
});

test('only activated ability slots and documented null result events are sanitized', () => {
  const f = new ObservationFilter(); f.accept(queue(), 1, 'info');
  const accepted = f.accept(event('abilities', 'usedAbility', { type: '4', chat: 'forbidden', name: 'private' }), 2, 'event');
  assert.equal(accepted?.value, 4); assert.doesNotMatch(JSON.stringify(accepted), /forbidden|private/);
  assert.equal(f.accept(event('abilities', 'ability', { type: '4' }), 3, 'event'), undefined);
  assert.equal(f.accept(event('announcer', 'victory', { chat: 'forbidden' }), 4, 'event'), undefined);
  assert.equal(f.accept(event('announcer', 'victory', null), 5, 'event')?.value, true);
  assert.equal(f.accept(damage(), 6, 'event'), undefined); // ended match revoked
});

test('attachment cannot fabricate detection or subscribe a launcher; feature subscription stays allowlisted', async () => {
  const f = fixture(); await f.connection.refresh(); f.detect(10902); await flush();
  assert.deepEqual(f.calls, []); assert.equal(f.enabled(), 0);
  f.detect(); await flush();
  assert.equal(f.enabled(), 1);
  assert.deepEqual(f.calls, [['features', LEAGUE_ID], ['subscribe', LEAGUE_ID, [...FEATURES]], ['info', LEAGUE_ID]]);
  assert.match(f.statuses.at(-1)!, /blocked until a fresh/);
  f.emitInfo(queue(), 10902); f.emitEvent(event('matchState', 'queueId', 420)); f.emitEvent(damage());
  assert.equal(f.observations().length, 0);
  f.emitInfo(queue()); f.emitEvent(clock()); f.emitEvent(damage());
  assert.equal(f.observations().length, 3);
  assert.ok(f.observations().every(row => row.coverage === 'partial' && typeof row.generation === 'number'));
  assert.match(f.statuses.at(-1)!, /coverage partial/);
  f.connection.dispose();
});

test('unknown getInfo envelopes never unlock observations or dump payloads', async () => {
  const f = fixture();
  // This is deliberately a Native-shaped fixture, NOT a claimed Electron schema.
  f.gep.getInfo = async () => ({ success: true, res: { game_info: { queueId: '420' }, chat: 'forbidden', summoner_info: { name: 'private' } } });
  f.detect(); await flush(); f.emitEvent(damage());
  assert.equal(f.observations().length, 0);
  assert.ok(f.rows.some(row => row.kind === 'initial-state' && row.usedForQueue === false && row.schema === 'unverified'));
  assert.doesNotMatch(JSON.stringify(f.rows), /forbidden|private|summoner_info/);
  f.connection.dispose();
});

test('queue arriving inside feature subscription is retained but not published before success', async () => {
  const f = fixture(), pending = deferred<void>();
  f.gep.setRequiredFeatures = async () => { f.emitInfo(queue()); f.emitEvent(damage()); return pending.promise; };
  f.detect(); await flush(); assert.equal(f.observations().length, 0);
  pending.resolve(); await flush();
  assert.deepEqual(f.observations().map(row => row.key), ['queueId']);
  f.emitEvent(damage()); assert.equal(f.observations().length, 2);
  f.connection.dispose();
});

test('initial queue arriving synchronously inside enable is preserved through feature discovery', async () => {
  const f = fixture(), pending = deferred<unknown>();
  f.gep.getFeatures = () => pending.promise;
  f.gep.emit('game-detected', { enable() { f.emitInfo(queue()); f.emitEvent(damage()); } }, LEAGUE_ID);
  assert.equal(f.observations().length, 0);
  pending.resolve(FEATURES); await flush();
  assert.deepEqual(f.observations().map(row => row.key), ['queueId']);
  assert.match(f.statuses.at(-1)!, /coverage partial/);
  f.emitEvent(damage()); assert.equal(f.observations().length, 2);
  f.connection.dispose();
});

test('missing queue capability and malformed feature results remain blocked', async () => {
  for (const supported of [['damage', 'chat'], null, { features: FEATURES }, ['matchState', 42]]) {
    const f = fixture(); f.gep.getFeatures = async () => supported;
    f.detect(); await flush(); f.emitInfo(queue()); f.emitEvent(damage());
    assert.equal(f.observations().length, 0);
    assert.equal(f.calls.filter(call => call[0] === 'subscribe').length, 0);
    assert.match(f.statuses.at(-1)!, /Blocked|blocked/);
    f.connection.dispose();
  }
});

for (const failure of ['error', 'elevated-privileges-required']) test(`${failure} invalidates scope and explicit retry needs fresh queue identity`, async () => {
  const f = fixture(); f.detect(); await flush(); f.emitInfo(queue()); f.emitEvent(clock());
  const count = f.observations().length;
  f.gep.emit(failure, {}, LEAGUE_ID, 'must not log this private error');
  f.emitInfo(queue()); f.emitEvent(damage()); assert.equal(f.observations().length, count);
  await f.connection.refresh(); f.emitEvent(damage()); assert.equal(f.observations().length, count);
  f.emitInfo(queue()); f.emitEvent(damage()); assert.equal(f.observations().length, count + 2);
  assert.equal(f.observations().at(-1)?.gameClock, null);
  assert.doesNotMatch(JSON.stringify(f.rows), /private error/);
  f.connection.dispose();
});

test('exit while features are pending prevents later subscription and stale errors', async () => {
  for (const reject of [false, true]) {
    const f = fixture(), pending = deferred<unknown>(); f.gep.getFeatures = () => pending.promise;
    f.detect(); f.gep.emit('game-exit', {}, LEAGUE_ID); const count = f.rows.length;
    if (reject) pending.reject(new Error('private error')); else pending.resolve(FEATURES);
    await flush(); assert.equal(f.rows.length, count); assert.match(f.statuses.at(-1)!, /League exited/);
    assert.equal(f.calls.length, 0); f.connection.dispose();
  }
});

test('exit during setRequiredFeatures prevents getInfo and buffered queue publication', async () => {
  const f = fixture(), pending = deferred<void>();
  f.gep.setRequiredFeatures = async () => { f.emitInfo(queue()); return pending.promise; };
  f.detect(); await flush(); f.gep.emit('game-exit', {}, LEAGUE_ID); pending.resolve(); await flush();
  assert.equal(f.observations().length, 0); assert.equal(f.calls.filter(call => call[0] === 'info').length, 0);
  f.connection.dispose();
});

test('old getInfo completion cannot override retry, exit or disposal status', async () => {
  const f = fixture(), pending = deferred<unknown>(); let reads = 0;
  f.gep.getInfo = () => ++reads === 1 ? pending.promise : Promise.resolve(null);
  f.detect(); await flush(); await f.connection.refresh(); f.emitInfo(queue());
  const count = f.rows.length; pending.resolve({ arbitrary: 'forbidden' }); await flush();
  assert.equal(f.rows.length, count); assert.match(f.statuses.at(-1)!, /coverage partial/);
  f.connection.dispose(); f.connection.dispose();
  assert.deepEqual(f.gep.eventNames(), []);
  const statusCount = f.statuses.length; await f.connection.refresh(); f.detect(); f.emitInfo(queue());
  assert.equal(f.rows.length, count); assert.equal(f.statuses.length, statusCount);
});

test('disposal while awaiting a snapshot suppresses its rejection and removes only owned listeners', async () => {
  const f = fixture(), pending = deferred<unknown>(); f.gep.getInfo = () => pending.promise;
  const external = () => {}; f.gep.on('new-info-update', external);
  f.detect(); await flush(); f.connection.dispose();
  const count = f.rows.length; pending.reject(new Error('private error')); await flush();
  assert.equal(f.rows.length, count); assert.deepEqual(f.gep.listeners('new-info-update'), [external]);
});

test('synchronous enable failure cannot subscribe or accept game data', async () => {
  const f = fixture();
  f.gep.emit('game-detected', { enable() { throw new Error('private error'); } }, LEAGUE_ID);
  await f.connection.refresh(); f.emitInfo(queue()); f.emitEvent(damage());
  assert.equal(f.calls.length, 0); assert.equal(f.observations().length, 0);
  assert.doesNotMatch(JSON.stringify(f.rows), /private error/); f.connection.dispose();
});
