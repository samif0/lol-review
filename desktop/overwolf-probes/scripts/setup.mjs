import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { installPinnedRuntime, probeRoot } from './runtime.mjs';
import { prepareProbe } from './prepare.mjs';

export function ensureAccessTemplate(root = probeRoot) {
  mkdirSync(path.join(root, '.local'), { recursive: true });
  const accessPath = path.join(root, '.local', 'access.json');
  try {
    writeFileSync(accessPath, JSON.stringify({ status: 'pending', verifiedAt: '', productName: 'Revu', authorName: '',
      appId: '', evidenceReference: '', leaguePatch: '', leagueAccess: false, gepAccess: false, recorderAccess: false,
      capture: { sourceWidth: 1920, sourceHeight: 1080, preset: '720p30', allowSoftwareEncoder: false } }, null, 2), { flag: 'wx' });
    return { accessPath, created: true };
  } catch (error) { if (error.code === 'EEXIST') return { accessPath, created: false }; throw error; }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const runtime = installPinnedRuntime();
    if (process.argv.includes('--runtime-only')) console.log(JSON.stringify({ runtime }, null, 2));
    else {
      const access = ensureAccessTemplate();
      const prepared = prepareProbe({ runtime });
      console.log(JSON.stringify({ runtime, access, preparedApp: prepared.appDirectory,
        nextCommand: 'npm run readiness', approvedAccessDemonstrated: false, credentialsSaved: false }, null, 2));
    }
  } catch { console.error('Setup failed. Check pinned dependencies/build and rerun preflight; no credentials were saved.'); process.exitCode = 1; }
}
