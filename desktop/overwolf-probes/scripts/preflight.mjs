import { existsSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { credentialStatus, inspectRuntime, cleanRuntimeEnvironment, desktopRoot, probeRoot } from './runtime.mjs';

export { probeRoot } from './runtime.mjs';
export function preflight({ root = probeRoot, runtimeRoot = desktopRoot } = {}) {
  const credentials = credentialStatus();
  let leagueGameplayRunning = null;
  if (process.platform === 'win32') {
    try {
      leagueGameplayRunning = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        "[bool](Get-Process -Name 'League of Legends' -ErrorAction SilentlyContinue)"],
      { encoding: 'utf8', windowsHide: true, timeout: 10_000, maxBuffer: 4096,
        env: cleanRuntimeEnvironment(), stdio: ['ignore', 'pipe', 'ignore'] }).trim() === 'True';
    } catch { /* Unknown is not proof of a running game. */ }
  }
  const pkg = JSON.parse(readFileSync(path.join(runtimeRoot, 'package.json'), 'utf8'));
  const runtime = inspectRuntime({ root: runtimeRoot });
  return { checkedAt: new Date().toISOString(), platform: process.platform, node: process.version,
    runtimePackage: pkg.devDependencies?.['@overwolf/ow-electron'] ?? pkg.dependencies?.['@overwolf/ow-electron'],
    typesPackage: pkg.devDependencies?.['@overwolf/ow-electron-packages-types'] ?? pkg.dependencies?.['@overwolf/ow-electron-packages-types'],
    runtimeBinaryInstalled: runtime.installed, runtime,
    credentialPresence: credentials.presence, credentialMethod: credentials.method,
    developerKeyShadowed: credentials.developerKeyShadowed, credentialsUsable: credentials.usable, leagueGameplayRunning,
    compiledEntryPresent: existsSync(path.join(root, 'dist/main.js')), rendererPresent: existsSync(path.join(root, 'probe.html')),
    localAccessEvidencePresent: existsSync(path.join(root, '.local/access.json')),
    approvedAccessDemonstrated: false, liveCaptureRun: false, productionDatabaseAccessed: false };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) console.log(JSON.stringify(preflight(), null, 2));
