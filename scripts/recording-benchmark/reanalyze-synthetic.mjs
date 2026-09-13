import { readFileSync, writeFileSync, statSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { resourceSummary } from './analyze.mjs';
import { inspectMedia } from './media.mjs';

const root = path.resolve(process.argv[2] ?? '');
if (!process.argv[2]) throw new Error('Usage: node reanalyze-synthetic.mjs EXPERIMENT_DIRECTORY');
const original = JSON.parse(readFileSync(path.join(root, 'results.json'), 'utf8'));
const corrected = original.map(result => {
  const directory = path.join(root, result.name);
  const rows = readFileSync(path.join(directory, 'resources.jsonl'), 'utf8').trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
  // Older evidence lacked UTC launch/stop fields. File creation/final progress times
  // conservatively approximate the active interval; retain this weaker provenance.
  const logMetadata = statSync(path.join(directory, 'progress.txt'));
  const approximate = result.startedUtcMs === undefined;
  const start = result.startedUtcMs ?? logMetadata.birthtimeMs;
  const stop = result.requestedStopUtcMs ?? logMetadata.mtimeMs - (result.stopRequestToExitMs ?? 0) - 500;
  const active = rows.filter(row => Date.parse(row.utc) >= start + 2000 && Date.parse(row.utc) + row.acquisitionMs <= stop &&
    row.acquisitionMs <= 3000 && row.processes.some(p => p.owned) && row.processes.filter(p => p.owned).every(p => Number.isFinite(p.cpuPercentMachine)));
  const mediaFile = path.join(directory, 'generated.mp4'); let media = null;
  if (existsSync(mediaFile)) {
    try { media = inspectMedia(mediaFile); } catch (error) { media = { bytes: statSync(mediaFile).size, probeError: error.stderr?.toString().slice(0, 1000) ?? error.message, avSyncVerified: false }; }
    const decode = spawnSync('ffmpeg', ['-v', 'error', '-xerror', '-threads', '2', '-i', mediaFile, '-f', 'null', '-'], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
    media.strictDecode = { passed: decode.status === 0 && !decode.stderr.trim(), exitCode: decode.status, stderr: decode.stderr.slice(0, 2000), error: decode.error?.message ?? null };
  }
  const progress = readFileSync(path.join(directory, 'progress.txt'), 'utf8');
  const progressLast = key => [...progress.matchAll(new RegExp(`^${key}=(.+)$`, 'gm'))].at(-1)?.[1].trim() ?? null;
  return { name: result.name, preset: result.preset, exitCode: result.status.code, stopRequestToExitMs: result.stopRequestToExitMs,
    resourceCoverage: { rawSamples: rows.length, activeSamples: active.length, qualified: active.length >= 5, approximateUtcBoundaries: approximate,
      note: 'Post-exit and >3s acquisition rows excluded; common active-owner window. Historical boundaries approximated from progress-file creation/final write, with 2s startup and 500ms tail margins. Diagnostic only, not a game performance benchmark.' },
    resources: resourceSummary(active), media, ffmpegProgress: { frames: progressLast('frame'), duplicates: progressLast('dup_frames'), drops: progressLast('drop_frames'),
      note: 'FFmpeg pipeline progress counters on generated input, not Overwolf render/output skip counters.' } };
});
writeFileSync(path.join(root, 'results-reanalyzed.json'), JSON.stringify(corrected, null, 2));
console.log(JSON.stringify(corrected.map(r => ({ name: r.name, samples: r.resourceCoverage.activeSamples, cpu: r.resources.ownedCpuPercentMachine.mean,
  privateMB: r.resources.ownedPrivateBytes.mean === null ? null : r.resources.ownedPrivateBytes.mean / 1e6,
  workingSetMB: r.resources.ownedWorkingSetBytes.mean === null ? null : r.resources.ownedWorkingSetBytes.mean / 1e6, bytes: r.media?.bytes,
  duration: r.media?.probe?.format?.duration, avStartMs: r.media?.audioMinusVideoStartMs, avEndMs: r.media?.audioMinusVideoEndMs, decode: r.media?.strictDecode, stopMs: r.stopRequestToExitMs, progress: r.ffmpegProgress })), null, 2));
