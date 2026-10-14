// Compare League's own frame-time stats per game with Revu's per-minute recording stats, so a
// Practice Tool A/B (recording off, recording on) reads as one table.
// Usage: node scripts/recording-benchmark/practice-check.mjs [--hours 12] [--logs <League GameLogs>] [--recordings <dir> ...]
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// League's histogram buckets (ms): [0,1,2,5,7.5,10,18,35,50,75,100,500,1000,inf]; count i covers edge i-1 to edge i.
const SLOW_FROM = 5, HITCH_FROM = 7, BIG_HITCH_FROM = 8;

export function perfStats(text) {
  const value = label => {
    const match = new RegExp(`${label}:\\s*([\\d.]+)`).exec(text);
    return match ? Number(match[1]) : null;
  };
  const histogram = /Frame Time Histogram:\s*\[([\d,\s]+)\]/.exec(text)?.[1].split(',').map(Number);
  const averageFps = value('Average FPS');
  if (averageFps === null || !histogram || histogram.length !== 14) return null;
  const from = index => histogram.slice(index).reduce((sum, count) => sum + count, 0);
  const frames = from(0);
  return { averageFps, maxFrameMs: value('Max Frame Time'), gpuFrameMs: value('Ave GPU Frame Time'), frames,
    slowFrames: from(SLOW_FROM), hitches: from(HITCH_FROM), bigHitches: from(BIG_HITCH_FROM),
    seconds: Number(/^(\d+\.\d+)\|.*Average FPS/m.exec(text)?.[1] ?? NaN) };
}

// League names each game's log folder with its local start time, e.g. 2026-10-14T11-24-56.
export function folderTime(name) {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2})-(\d{2})-(\d{2})$/.exec(name);
  return match ? new Date(`${match[1]}T${match[2]}:${match[3]}:${match[4]}`).getTime() : NaN;
}

export function recordingSummary(session) {
  const stats = session.finalStats ?? {}, minutes = stats.perMinute?.frames?.length ?? 0;
  const sum = key => stats.perMinute?.[key]?.reduce((total, count) => total + count, 0) ?? null;
  const frames = sum('frames');
  return { startedAt: Date.parse(session.startedAt),
    priority: session.resolvedSettings?.priority ?? session.priority ?? 'before fix',
    gpuPriority: session.resolvedSettings?.gpuPriority ?? 'high (before fix)', preset: session.preset,
    repeatedPerMinute: minutes ? sum('repeated') / (frames / ((session.resolvedSettings?.fps ?? 60) * 60)) : null,
    repeatedPercent: frames ? 100 * sum('repeated') / frames : null, lagged: sum('lagged') ?? stats.laggedFrames ?? null };
}

export function matchGames(games, recordings) {
  return games.map(game => ({ ...game, recording: recordings.find(item =>
    item.startedAt >= game.startedAt - 60_000 && item.startedAt <= game.startedAt + (game.perf.seconds || 7200) * 1000) ?? null }));
}

function sessions(roots) {
  const found = [];
  for (const root of roots) {
    if (!existsSync(root)) continue;
    for (const entry of readdirSync(root, { withFileTypes: true })) {
      const file = path.join(root, entry.name, 'session.json');
      if (entry.isDirectory() && existsSync(file)) {
        try { found.push(recordingSummary(JSON.parse(readFileSync(file, 'utf8')))); } catch { /* Not a recording session. */ }
      }
    }
  }
  return found;
}

function main(argv) {
  const option = (name, fallback) => { const at = argv.indexOf(name); return at >= 0 ? argv[at + 1] : fallback; };
  const hours = Number(option('--hours', '12'));
  const logs = option('--logs', 'C:/Riot Games/League of Legends/Logs/GameLogs');
  const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
  const tests = path.join(repository, 'artifacts/recording-tests');
  const roots = argv.flatMap((arg, at) => arg === '--recordings' ? [argv[at + 1]] : []);
  if (!roots.length) {
    if (existsSync(tests)) for (const run of readdirSync(tests)) roots.push(path.join(tests, run, 'Revu/Recordings'));
    if (process.env.LOCALAPPDATA) roots.push(path.join(process.env.LOCALAPPDATA, 'Revu/Recordings'));
  }
  const since = Date.now() - hours * 3600_000;
  const games = readdirSync(logs).map(name => {
    const log = readdirSync(path.join(logs, name)).find(file => file.endsWith('_r3dlog.txt'));
    const perf = log ? perfStats(readFileSync(path.join(logs, name, log), 'utf8')) : null;
    return { name, startedAt: folderTime(name), perf };
  }).filter(game => game.perf && game.startedAt >= since).sort((a, b) => a.startedAt - b.startedAt);
  if (!games.length) { console.log(`No finished League games with performance stats in the last ${hours} hours.`); return; }
  const rows = matchGames(games, sessions(roots)).map(({ name, perf, recording }) => ({
    game: name.replace('T', ' '), minutes: (perf.seconds / 60).toFixed(1),
    recording: recording ? `${recording.priority} (GPU ${recording.gpuPriority})` : 'off',
    'avg FPS': perf.averageFps.toFixed(1), 'frames >7.5 ms': `${(100 * perf.slowFrames / perf.frames).toFixed(2)}%`,
    'hitches >18 ms': perf.hitches, 'hitches >35 ms': perf.bigHitches, 'max frame ms': perf.maxFrameMs,
    'GPU ms/frame': perf.gpuFrameMs,
    'repeated/min': recording?.repeatedPerMinute == null ? '' : recording.repeatedPerMinute.toFixed(1),
    'repeated %': recording?.repeatedPercent == null ? '' : recording.repeatedPercent.toFixed(2),
    'lagged': recording?.lagged ?? '' }));
  console.table(rows);
  console.log('Game smoothness comes from League\'s own log. Compare recording-on runs against "off" runs of the same routine:');
  console.log('avg FPS and the hitch counts should match within normal run-to-run variation. Repeated/min is how often the');
  console.log('recording itself froze for a frame (about 60/min before this fix on 1080p60).');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
