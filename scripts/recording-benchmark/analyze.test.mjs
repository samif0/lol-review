import test from 'node:test';
import assert from 'node:assert/strict';
import { frameSummary, presentMonSummary, journalSummary, resourceSummary, parseCsv, gpuSummary } from './analyze.mjs';
import { numericMetadata } from './media.mjs';

test('1% low uses slowest frames rather than reciprocal p99; retains stutters', () => {
  const result = frameSummary([...Array(198).fill(10), 100, 200]);
  assert.equal(result.onePercentLowFps, 1000 / 150);
  assert.equal(result.p99FrameMs, 10);
  assert.equal(result.over50ms, 2);
  assert.equal(frameSummary([]).onePercentLowFps, null);
});
test('PresentMon isolates exact gameplay process and refuses ambiguous swapchains', () => {
  const csv = 'Application,ProcessID,SwapChainAddress,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,Dropped\nLeagueClient.exe,1,0xa,1,200,200,0\n"League of Legends.exe",2,0xb,1,10,10,0\nLeague of Legends.exe,2,0xb,2,20,20,0\nLeague of Legends.exe,2,0xc,2,999,999,0\n';
  assert.equal(presentMonSummary(csv).verified, false);
  assert.equal(presentMonSummary(csv, { swapChain: '0xb', startSeconds: 2 }).meanFrameMs, 20);
  assert.equal(presentMonSummary(csv, { processId: 99 }).verified, false);
});
test('CSV accepts Windows BOM, escaped quotes and commas', () => {
  assert.deepEqual(parseCsv('\uFEFFA,B\r\n"a,b","quoted ""value"""\r\n'), [{ A: 'a,b', B: 'quoted "value"' }]);
});
test('missing/reset recorder counters stay unverified rather than becoming zero', () => {
  assert.equal(journalSummary([]).outputSkipped.value, null);
  const base = [{ kind: 'recorder-stats', outputSkipped: 10 }, { kind: 'recorder-stats', outputSkipped: 1 }];
  assert.deepEqual(journalSummary(base).outputSkipped.reset, true);
  assert.equal(journalSummary(base).outputSkipped.value, null);
  const stable = journalSummary([{ kind: 'recorder-stats', outputSkipped: 2 }, { kind: 'recorder-stats', outputSkipped: 7 }]);
  assert.equal(stable.outputSkipped.value, 5);
});
test('only owned processes contribute; an empty process set is unavailable', () => {
  const sample = { acquisitionMs: 12, elapsedMs: 0, processes: [{ owned: true, cpuPercentMachine: 2, privateBytes: 100, workingSetBytes: 200 }, { owned: false, cpuPercentMachine: 90, privateBytes: 5000, workingSetBytes: 6000 }], physicalDisks: [], gpuEngines: [], unavailable: ['physicalDisk'] };
  assert.equal(resourceSummary([sample]).ownedCpuPercentMachine.mean, 2);
  assert.equal(resourceSummary([{ ...sample, processes: [] }]).ownedCpuPercentMachine.mean, null);
  assert.equal(resourceSummary([sample]).systemDiskWriteBytesPerSecond.mean, null);
});
test('GPU totals add processes on one engine but do not add separate engines', () => {
  const row = { processes: [{ pid: 1, owned: true }], unavailable: [], gpuEngines: [
    { instance: 'pid_1_luid_0x00000000_0x00015B68_phys_0_eng_0_engtype_3D', utilizationPercent: 30 },
    { instance: 'pid_2_luid_0x00000000_0x00015B68_phys_0_eng_0_engtype_3D', utilizationPercent: 20 },
    { instance: 'pid_1_luid_0x00000000_0x00015B68_phys_0_eng_1_engtype_3D', utilizationPercent: 40 }
  ] };
  assert.equal(gpuSummary([row]).groups['system/luid_0x00000000_0x00015B68_phys_0/3D'].mean, 50);
  assert.equal(gpuSummary([row]).groups['owned/luid_0x00000000_0x00015B68_phys_0/3D'].mean, 40);
});
test('missing media metadata and invalid frame rows cannot fabricate measurements', () => {
  for (const value of [null, undefined, '', ' ', 'N/A', NaN]) assert.equal(numericMetadata(value), null);
  assert.equal(numericMetadata('0.125'), .125);
  assert.equal(numericMetadata(0), 0);
  const headers = 'Application,ProcessID,SwapChainAddress,TimeInSeconds,MsBetweenPresents\n';
  assert.equal(presentMonSummary(headers + 'League of Legends.exe,1,0xa,1,N/A\n').verified, false);
  assert.equal(presentMonSummary(headers + 'League of Legends.exe,1,0xa,,15\n').verified, false);
});
test('terminal stats bound the session before idle counter resets and keep callback timing separate', () => {
  const rows = [
    { kind: 'recorder-stats', outputSkipped: 99 },
    { kind: 'capture-state', state: 'starting', monoMs: 10 },
    { kind: 'recorder-stats', outputSkipped: 2, outputTotal: 100 },
    { kind: 'capture-state', state: 'stopping', monoMs: 100 },
    { kind: 'recorder-stats', phase: 'stop', outputSkipped: 7, outputTotal: 120 },
    { kind: 'recorder-stop', requestToCallbackMs: 50 },
    { kind: 'capture-state', state: 'finalized', monoMs: 180 },
    { kind: 'recorder-stats', outputSkipped: 0 }
  ];
  const result = journalSummary(rows);
  assert.equal(result.outputSkipped.value, 5);
  assert.equal(result.stopRequestToCallbackMs, 50);
  assert.equal(result.stopToValidatedMs, 80);
  assert.equal(result.stageCounterRatios.output.skippedPer100Total, 25);
  assert.equal(result.stageCounterRatios.render.skippedPer100Total, null);
});
