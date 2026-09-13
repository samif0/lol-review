import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const finite = value => typeof value === 'number' && Number.isFinite(value);
const mean = values => values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
export const percentile = (values, p) => {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)];
};
const summary = values => {
  values = values.filter(finite);
  return { samples: values.length, mean: mean(values), p95: percentile(values, .95), max: values.length ? Math.max(...values) : null };
};
export function gpuSummary(rows) {
  const keys = new Set();
  const samples = rows.map(row => {
    if (row.unavailable.includes('gpuEngines') || row.gpuEngines.some(v => !finite(v.utilizationPercent))) return null;
    const owned = new Set(row.processes.filter(p => p.owned).map(p => p.pid));
    const engines = new Map();
    for (const value of row.gpuEngines) {
      const match = /^pid_(\d+)_(luid_0x[\da-f]+_0x[\da-f]+_phys_\d+)_eng_(\d+)_engtype_(.+)$/i.exec(value.instance);
      if (!match) continue;
      for (const scope of ['system', ...(owned.has(Number(match[1])) ? ['owned'] : [])]) {
        const key = `${scope}/${match[2]}/${match[4]}`;
        keys.add(key);
        const engineKey = `${key}/${match[3]}`;
        engines.set(engineKey, (engines.get(engineKey) ?? 0) + value.utilizationPercent);
      }
    }
    const peaks = new Map();
    for (const [key, value] of engines) {
      const group = key.slice(0, key.lastIndexOf('/'));
      peaks.set(group, Math.max(peaks.get(group) ?? 0, value));
    }
    return peaks;
  });
  return { definition: 'Sum processes on the same physical adapter/engine, then report the busiest engine within each engine type. Distinct engines are never summed. Percentages are sampled integer Windows counters.',
    groups: Object.fromEntries([...keys].sort().map(key => [key, summary(samples.map(s => s === null ? null : s.get(key) ?? 0))])) };
}
export function frameSummary(values) {
  const frames = values.filter(value => finite(value) && value > 0);
  const slowest = [...frames].sort((a, b) => b - a).slice(0, Math.ceil(frames.length * .01));
  return { frames: frames.length, meanFrameMs: mean(frames), p50FrameMs: percentile(frames, .50), p95FrameMs: percentile(frames, .95),
    p99FrameMs: percentile(frames, .99), p999FrameMs: percentile(frames, .999), averageFps: frames.length ? 1000 / mean(frames) : null,
    onePercentLowFps: slowest.length ? 1000 / mean(slowest) : null,
    onePercentLowDefinition: '1000 / arithmetic mean of the slowest ceil(N*0.01) frame intervals; not 1000/p99.',
    over16_67ms: frames.filter(v => v > 1000 / 60).length, over33_33ms: frames.filter(v => v > 1000 / 30).length,
    over50ms: frames.filter(v => v > 50).length };
}
// PresentMon emits numeric fields and quoted CSV strings; accept escaped double quotes.
export function parseCsv(text) {
  const rows = []; let row = [], value = '', quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') { if (quoted && text[i + 1] === '"') { value += '"'; i++; } else quoted = !quoted; }
    else if (c === ',' && !quoted) { row.push(value); value = ''; }
    else if (c === '\n' && !quoted) { row.push(value.replace(/\r$/, '')); if (row.some(Boolean)) rows.push(row); row = []; value = ''; }
    else value += c;
  }
  if (quoted) throw new Error('Unterminated CSV quote.');
  if (row.length || value) { row.push(value.replace(/\r$/, '')); rows.push(row); }
  const headers = rows.shift()?.map(h => h.replace(/^\uFEFF/, '')) ?? [];
  return rows.map(cells => Object.fromEntries(headers.map((h, i) => [h, cells[i] ?? ''])));
}
export function presentMonSummary(text, { application = 'League of Legends.exe', processId, swapChain, startSeconds = 0, endSeconds = Infinity } = {}) {
  const rows = parseCsv(text).filter(row => row.Application?.toLowerCase() === application.toLowerCase() &&
    (processId === undefined || Number(row.ProcessID) === processId) && (swapChain === undefined || row.SwapChainAddress === swapChain));
  const groups = new Map();
  for (const row of rows) {
    const key = `${row.ProcessID}/${row.SwapChainAddress}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(row);
  }
  if (groups.size !== 1) return { verified: false, reason: groups.size ? 'Multiple PID/swap-chain groups; select --process-id and --swap-chain.' : 'No matching game frames.', groups: [...groups].map(([key, rows]) => ({ key, rows: rows.length })) };
  const selected = [...groups.values()][0];
  if (!('MsBetweenPresents' in selected[0]) || !('TimeInSeconds' in selected[0])) throw new Error('Use PresentMon --v1_metrics for consistent present-to-present intervals and TimeInSeconds.');
  const trimmed = selected.filter(row => row.TimeInSeconds.trim() !== '' && Number.isFinite(Number(row.TimeInSeconds)) && Number(row.TimeInSeconds) >= startSeconds && Number(row.TimeInSeconds) < endSeconds);
  const frames = frameSummary(trimmed.map(row => Number(row.MsBetweenPresents)));
  return { verified: frames.frames > 0, metric: 'MsBetweenPresents (application present cadence; not display latency)',
    startSeconds, endSeconds: finite(endSeconds) ? endSeconds : null, ...frames,
    displayedFrameIntervals: frameSummary(trimmed.filter(row => row.Dropped !== '1' && row.Dropped !== 'true').map(row => Number(row.MsBetweenDisplayChange))),
    note: 'No trimming or outlier removal beyond the explicit time window. Present intervals retain dropped presents.' };
}
export function resourceSummary(rows) {
  const owned = rows.map(row => row.processes.filter(p => p.owned));
  return { samples: rows.length, acquisitionMs: summary(rows.map(r => r.acquisitionMs)),
    measuredSpanSeconds: rows.length > 1 ? (rows.at(-1).elapsedMs - rows[0].elapsedMs) / 1000 : null,
    systemCpuPercent: summary(rows.map(r => r.systemCpuPercent)), availableMemoryMB: summary(rows.map(r => r.availableMemoryMB)),
    ownedCpuPercentMachine: summary(owned.map(processes => processes.length && processes.every(p => finite(p.cpuPercentMachine)) ? processes.reduce((sum, p) => sum + p.cpuPercentMachine, 0) : null)),
    ownedWorkingSetBytes: summary(owned.map(processes => processes.length && processes.every(p => finite(p.workingSetBytes)) ? processes.reduce((sum, p) => sum + p.workingSetBytes, 0) : null)),
    ownedPrivateBytes: summary(owned.map(processes => processes.length && processes.every(p => finite(p.privateBytes)) ? processes.reduce((sum, p) => sum + p.privateBytes, 0) : null)),
    systemDiskWriteBytesPerSecond: summary(rows.map(r => r.physicalDisks.find(d => d.name === '_Total')?.writeBytesPerSecond)),
    systemDiskReadBytesPerSecond: summary(rows.map(r => r.physicalDisks.find(d => d.name === '_Total')?.readBytesPerSecond)),
    systemDiskQueueLength: summary(rows.map(r => r.physicalDisks.find(d => d.name === '_Total')?.queueLength)),
    gpu: gpuSummary(rows),
    gpuEngineNote: 'Raw resources.jsonl contains per-PID/per-adapter engine instances. Engine values must not be summed into GPU utilization; missing engine rows mean zero only if gpuEngines is available.',
    unavailable: [...new Set(rows.flatMap(r => r.unavailable))] };
}
export function journalSummary(rows) {
  const startIndex = rows.findIndex(r => r.kind === 'capture-state' && ['starting', 'recording'].includes(r.state));
  const session = rows.slice(Math.max(0, startIndex));
  const stopStatsIndex = session.findIndex(r => r.kind === 'recorder-stats' && r.phase === 'stop');
  const finalizingIndex = session.findIndex(r => r.kind === 'capture-state' && r.state === 'finalizing');
  const endIndex = stopStatsIndex >= 0 ? stopStatsIndex : finalizingIndex;
  const stats = (endIndex >= 0 ? session.slice(0, endIndex + 1) : session).filter(r => r.kind === 'recorder-stats');
  const delta = key => {
    const values = stats.map(r => r[key]);
    if (values.length < 2 || !values.every(finite)) return { value: null, reset: false, coverage: 'Insufficient counter samples.' };
    const reset = values.some((v, i) => i > 0 && v < values[i - 1]);
    return { value: reset ? null : values.at(-1) - values[0], reset, coverage: 'First-to-last sampled counter only; excludes unsampled startup/stop.' };
  };
  const stop = session.find(r => r.kind === 'capture-state' && r.state === 'stopping');
  const done = stop && session.find(r => r.kind === 'capture-state' && r.state === 'finalized' && r.monoMs >= stop.monoMs);
  const callback = session.find(r => r.kind === 'recorder-stop');
  const ratio = (skipped, total) => {
    const numerator = delta(skipped).value; const denominator = delta(total).value;
    return { skippedCounterDelta: numerator, totalCounterDelta: denominator,
      skippedPer100Total: finite(numerator) && finite(denominator) && denominator > 0 ? 100 * numerator / denominator : null };
  };
  return { samples: stats.length, activeFps: summary(stats.map(r => r.fps)), recorderCpuPercent: summary(stats.map(r => r.cpu)),
    recorderMemoryMB: summary(stats.map(r => r.memoryMB)), outputSkipped: delta('outputSkipped'), renderSkipped: delta('renderSkipped'),
    outputTotal: delta('outputTotal'), renderTotal: delta('renderTotal'),
    stageCounterRatios: { output: ratio('outputSkipped', 'outputTotal'), render: ratio('renderSkipped', 'renderTotal'),
      definition: '100 × skipped-counter delta / provider total-counter delta over identical sampled endpoints. Separate stages; not unique end-to-end dropped-frame percentage.' },
    stopSnapshotPresent: stopStatsIndex >= 0,
    stopRequestToCallbackMs: finite(callback?.requestToCallbackMs) ? callback.requestToCallbackMs : null,
    stopToValidatedMs: done && finite(stop.monoMs) && finite(done.monoMs) ? done.monoMs - stop.monoMs : null,
    avSyncVerified: false, note: 'First session only, bounded at terminal stats/finalizing. Counter deltas are separate render/output stages, not unique lost video frames. Validated means probe media checks, not packaged-player acceptance.' };
}
function jsonl(file) { return readFileSync(file, 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)); }
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = Object.fromEntries(Array.from({ length: (process.argv.length - 2) / 2 }, (_, i) => [process.argv[2 + i * 2], process.argv[3 + i * 2]]));
  if (!args['--run']) throw new Error('Usage: node analyze.mjs --run RUN_DIRECTORY [--presentmon CSV --journal JSONL --start-seconds 10 --end-seconds 130 --process-id PID --swap-chain ADDRESS]');
  const run = path.resolve(args['--run']); const result = { generatedAt: new Date().toISOString(), resources: null, gameplay: null, recorder: null };
  if (existsSync(path.join(run, 'resources.jsonl'))) result.resources = resourceSummary(jsonl(path.join(run, 'resources.jsonl')));
  if (args['--presentmon']) result.gameplay = presentMonSummary(readFileSync(args['--presentmon'], 'utf8'), {
    processId: args['--process-id'] ? Number(args['--process-id']) : undefined, swapChain: args['--swap-chain'],
    startSeconds: Number(args['--start-seconds'] ?? 0), endSeconds: Number(args['--end-seconds'] ?? Infinity) });
  if (args['--journal']) result.recorder = journalSummary(jsonl(args['--journal']));
  writeFileSync(path.join(run, 'summary.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 2));
}
