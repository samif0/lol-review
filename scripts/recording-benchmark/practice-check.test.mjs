import test from 'node:test';
import assert from 'node:assert/strict';
import { folderTime, matchGames, perfStats, recordingSummary } from './practice-check.mjs';

const log = `001709.155| ALWAYS|  FLOW| Switching Game State
001711.484| ALWAYS| =================== Perf Stats (all in ms) =========================
001711.484| ALWAYS| Average Frame Time:    4.61
001711.484| ALWAYS| Average FPS:           217.03 fps
001711.484| ALWAYS| Max Frame Time:        97.18
001711.484| ALWAYS| Ave GPU Frame Time:    0.70
001711.484| ALWAYS| Frame Time Histogram:  [0,0,0,330193,11473,21670,4669,71,6,3,2,0,0,0]
`;

test('League perf stats separate slow frames from hitches using its own histogram buckets', () => {
  const stats = perfStats(log);
  assert.equal(stats.averageFps, 217.03); assert.equal(stats.maxFrameMs, 97.18); assert.equal(stats.gpuFrameMs, 0.7);
  assert.equal(stats.frames, 368087);
  assert.equal(stats.slowFrames, 21670 + 4669 + 71 + 6 + 3 + 2);
  assert.equal(stats.hitches, 82); assert.equal(stats.bigHitches, 11);
  assert.equal(stats.seconds, 1711.484);
  assert.equal(perfStats('game crashed before its stats were written'), null);
});

test('log folders are local start times and recordings attach to the game they started in', () => {
  assert.equal(folderTime('2026-10-14T11-24-56'), new Date('2026-10-14T11:24:56').getTime());
  assert.ok(Number.isNaN(folderTime('notes')));
  const start = folderTime('2026-10-14T11-24-56');
  const recording = recordingSummary({ startedAt: new Date(start + 6000).toISOString(), priority: 'below-normal', preset: '1080p60',
    resolvedSettings: { fps: 60, gpuPriority: 'below-normal', captureMinIntervalHns: 83333 },
    finalStats: { laggedFrames: 1, perMinute: { frames: [3600, 1800], repeated: [6, 3], lagged: [1, 0] } } });
  assert.equal(recording.repeatedPerMinute, 6); assert.equal(recording.repeatedPercent, 100 * 9 / 5400);
  assert.equal(recording.lagged, 1); assert.equal(recording.capturePacing, '8.33 ms');
  assert.equal(recordingSummary({ startedAt: new Date(start).toISOString(),
    resolvedSettings: { fps: 60, captureMinIntervalHns: null } }).capturePacing, 'default');
  const [matched, unrecorded] = matchGames([{ startedAt: start, perf: { seconds: 300 } },
    { startedAt: start + 900_000, perf: { seconds: 300 } }], [recording]);
  assert.equal(matched.recording, recording); assert.equal(unrecorded.recording, null);
  const before = recordingSummary({ startedAt: new Date(start).toISOString(), finalStats: { laggedFrames: 410 } });
  assert.equal(before.priority, 'before fix'); assert.equal(before.repeatedPerMinute, null); assert.equal(before.lagged, 410);
  assert.equal(before.capturePacing, 'unknown');
});
