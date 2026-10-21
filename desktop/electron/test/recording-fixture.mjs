import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { RecordingService } from '../recording-service.mjs';

export async function settle(service) {
  for (let i = 0; i < 10; i++) { const serial = service.serial; await serial; if (serial === service.serial) break; }
}
export async function fixture(t, options = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'Revu.Recording.'));
  let time = Date.parse('2026-09-12T12:00:00Z');
  const context = { gameId: 501, isGameInProgress: true, gameTimeSeconds: 0.5, observedAt: new Date(time).toISOString(), lastEndedGameId: null,
    lastEndedGameTimeSeconds: 60.5, lastEndedObservedAt: new Date(time).toISOString(), lastEndedConfirmedAt: new Date(time).toISOString() };
  const registrations = [], calls = [], prunes = [];
  let callback, result;
  const driver = {
    async initialize() {}, async dispose() {},
    async start({ outputDirectory, onStopped }) {
      calls.push('start'); callback = onStopped;
      await writeFile(path.join(outputDirectory, 'capture.mp4'), 'fixture');
      result = { hasError: false, filePath: path.join(outputDirectory, 'capture.mp4'), duration: 60, startTimeEpoch: time };
      return { encoder: 'fake' };
    },
    async stop() { calls.push('stop'); callback?.(result); },
    async validate(value) { return { filePath: value.filePath, fileSize: 7, durationSeconds: value.duration,
      startedAt: new Date(value.startTimeEpoch).toISOString(), startedAtVerified: true }; },
  };
  const backend = { async request(route, request) {
    if (route === '/api/recording/context') return { ...context };
    if (route === '/api/recording/prune') { prunes.push(request); return { ok: true, deleted: 0 }; }
    assert.equal(route, '/api/recording/register'); registrations.push(request.body);
    return { ok: true, status: request.body.complete ? 'linked' : 'retained-partial' };
  } };
  const service = new RecordingService({ root: path.join(directory, 'Recordings'), profile: path.join(directory, 'Profile'), backend,
    now: () => time, space: async () => 10 * 1024 ** 3, deadlines: { start: 100, stop: 100, callback: 100, validate: 100 }, ...options });
  await service.initialize();
  t.after(async () => { await service.shutdown(); await rm(directory, { recursive: true, force: true }); });
  return { service, driver, context, registrations, calls, prunes, directory, advance: ms => { time += ms; }, callback: () => callback(result) };
}
export async function start(f) {
  await f.service.saveSettings({ enabled: true, preset: '720p30' });
  await f.service.attachDriver(f.driver);
  f.service.onGame({ pid: 100, running: true }); await settle(f.service);
}
