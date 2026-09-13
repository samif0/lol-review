import { spawn } from 'node:child_process';
import { access, mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { stageApp } from './package.mjs';
import { cleanRuntimeEnvironment, credentialStatus, desktopRoot, inspectRuntime } from '../runtime/overwolf.mjs';

/** Stages the real Revu host, with only Recorder enabled, for local Dev Mode.
 * Credentials remain in the process environment and never enter staged files.
 * This does not grant access or produce a signed distributable application. */
export async function prepareRecorderDevelopment({ root = desktopRoot, identity, destination }) {
  const text = value => typeof value === 'string' && value.trim().length > 0 && value.length <= 160;
  if (!text(identity?.productName) || !text(identity?.authorName))
    throw new Error('Set your approved productName and authorName in overwolf-probes/.local/access.json (metadata only).');
  const files = await stageApp(root, destination);
  const file = path.join(destination, 'package.json');
  const manifest = JSON.parse(await readFile(file, 'utf8'));
  manifest.productName = identity.productName;
  manifest.author = { name: identity.authorName };
  manifest.overwolf = { packages: ['recorder'] };
  await writeFile(file, JSON.stringify(manifest, null, 2));
  return files;
}

export async function launchRecorderDevelopment() {
  if (!credentialStatus().usable) throw new Error('Valid Overwolf developer credentials are required. Use scripts/Start-RecorderDevelopment.ps1 for a masked key prompt.');
  const runtime = inspectRuntime();
  if (!runtime.installed) throw new Error('Run npm run runtime:install first.');
  const executable = path.resolve(desktopRoot, '../artifacts/desktop-build/bin/Revu.Sidecar/release_win-x64/Revu.Sidecar.exe');
  await access(executable);
  let identity;
  try { identity = JSON.parse(await readFile(path.join(desktopRoot, 'overwolf-probes/.local/access.json'), 'utf8')); }
  catch { throw new Error('Create the local application metadata with the Overwolf probe setup first.'); }
  const output = path.resolve(desktopRoot, '../artifacts/recorder-development');
  await mkdir(output, { recursive: true });
  const destination = await mkdtemp(path.join(output, 'app-'));
  await prepareRecorderDevelopment({ identity, destination });
  // Use the normal Revu profile/data root and single-instance lock. Close an
  // existing normal Revu instance before selecting this development build.
  const child = spawn(runtime.executable, [destination, '--sidecar', executable], {
    cwd: destination, windowsHide: false, stdio: 'inherit', env: cleanRuntimeEnvironment(process.env, { live: true }),
  });
  return new Promise((resolve, reject) => {
    child.once('error', () => reject(new Error('Unable to launch the Recorder development build.')));
    child.once('exit', code => resolve(code ?? 1));
  });
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  launchRecorderDevelopment().then(code => { process.exitCode = code; })
    .catch(error => { console.error(error.message); process.exitCode = 1; });
}
