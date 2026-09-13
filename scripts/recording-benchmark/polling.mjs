import { execFileSync } from 'node:child_process';
import { writeFileSync, existsSync } from 'node:fs';
import { performance } from 'node:perf_hooks';
import { percentile } from './analyze.mjs';

const output = process.argv[2];
if (!output || existsSync(output)) throw new Error('Supply a new JSON output path.');
const shell = process.env.SystemRoot + '\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';
const script = "$ErrorActionPreference='Stop'; @((Get-CimInstance Win32_Process -Filter \"Name = 'League of Legends.exe'\" -Property Name,ProcessId) | Where-Object { $_.Name -ieq 'League of Legends.exe' } | ForEach-Object { [int]$_.ProcessId }) | ConvertTo-Json -Compress";
const shellMs = [];
for (let i = 0; i < 5; i++) {
  const start = performance.now();
  execFileSync(shell, ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], timeout: 10000 });
  shellMs.push(performance.now() - start);
}
const nativeMs = [];
for (let batch = 0; batch < 5; batch++) {
  const start = performance.now();
  for (let i = 0; i < 10000; i++) process.kill(process.pid, 0);
  nativeMs.push((performance.now() - start) / 10000);
}
const result = { generatedAt: new Date().toISOString(), node: process.version,
  powershellDiscoveryWallMs: shellMs, powershellMedianMs: percentile(shellMs, .5),
  nativeKnownPidPerCallMs: nativeMs, nativeMedianMs: percentile(nativeMs, .5),
  caveat: 'Non-game microbenchmark; spawn+CIM discovery and signal-0 known-PID liveness do different work. A living PID does not validate executable identity and PID reuse remains possible. Wall latency is not CPU time or gameplay frame impact. Background applications remained active.' };
writeFileSync(output, JSON.stringify(result, null, 2)); console.log(JSON.stringify(result, null, 2));
