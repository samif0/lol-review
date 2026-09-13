import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomUUID, createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inspectRuntime, probeRoot } from './runtime.mjs';

export function readLocalAccess(root = probeRoot) {
  const file = path.join(root, '.local/access.json');
  if (!existsSync(file)) return null;
  try { return JSON.parse(readFileSync(file, 'utf8')); }
  catch { throw new Error('Local access metadata is invalid JSON.'); }
}

export function validateLiveAccess(access, mode, now = Date.now()) {
  const text = value => typeof value === 'string' && value.trim().length > 0 && value.length <= 160;
  const verifiedAt = Date.parse(access?.verifiedAt);
  if (!['gep', 'recorder'].includes(mode) || access?.status !== 'approved' || access.leagueAccess !== true
      || access[`${mode}Access`] !== true || !Number.isFinite(verifiedAt) || verifiedAt > now
      || now - verifiedAt > 7 * 24 * 60 * 60 * 1000
      || ![access.productName, access.authorName, access.appId, access.evidenceReference, access.leaguePatch].every(text))
    throw new Error('Current reviewed approval, application identity, and selected package/League evidence are required.');
  if (mode === 'recorder') {
    const capture = access.capture;
    if (!capture || ![capture.sourceWidth, capture.sourceHeight].every(n => Number.isInteger(n) && n >= 64 && n <= 7680)
        || !['720p30', '1080p30', '1080p60'].includes(capture.preset) || typeof capture.allowSoftwareEncoder !== 'boolean'
        || (capture.encoder !== undefined && !['obs_nvenc_h264_tex', 'h264_texture_amf', 'obs_qsv11_v2', 'obs_x264'].includes(capture.encoder))
        || (capture.encoder === 'obs_x264' && !capture.allowSoftwareEncoder))
      throw new Error('Confirm gameplay resolution, preset, and encoder policy in local capture metadata.');
  }
}

export function prepareProbe({ mode = 'readiness', root = probeRoot, access = readLocalAccess(root), runtime = inspectRuntime() } = {}) {
  if (!['readiness', 'gep', 'recorder'].includes(mode)) throw new Error('Select readiness, gep, or recorder.');
  if (!runtime.installed) throw new Error('Install and verify the pinned runtime with npm run runtime:install.');
  if (mode !== 'readiness') validateLiveAccess(access, mode);
  if (!existsSync(path.join(root, 'dist/main.js')) || !existsSync(path.join(root, 'probe.html')))
    throw new Error('Build the probe before preparing its application.');

  const runRoot = path.join(root, '.local', 'runs', `${mode}-${randomUUID()}`);
  const appDirectory = path.join(runRoot, 'app');
  mkdirSync(path.join(appDirectory, 'dist'), { recursive: true });
  mkdirSync(path.join(appDirectory, 'scripts'));
  mkdirSync(path.join(runRoot, 'user-data'));
  const files = [];
  function copy(relative) {
    const destination = path.join(appDirectory, relative);
    copyFileSync(path.join(root, relative), destination);
    const bytes = readFileSync(destination);
    files.push({ path: relative.replaceAll('\\', '/'), bytes: bytes.length,
      sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  for (const entry of readdirSync(path.join(root, 'dist'), { withFileTypes: true })) {
    if (entry.isFile() && entry.name.endsWith('.js')) copy(path.join('dist', entry.name));
  }
  copy('probe.html');
  copy('scripts/ownership.mjs');
  const named = [access?.productName, access?.authorName].every(value => typeof value === 'string' && value.trim() && value.length <= 160);
  const requestedPackages = mode === 'readiness' ? [] : [mode];
  const manifest = { name: 'revu-development-probe', version: '0.0.0', private: true,
    productName: named ? access.productName : 'Revu Readiness Diagnostic',
    author: { name: named ? access.authorName : 'Local development diagnostic' },
    type: 'module', main: 'dist/main.js', overwolf: { packages: requestedPackages } };
  writeFileSync(path.join(appDirectory, 'package.json'), JSON.stringify(manifest, null, 2));
  const capture = mode === 'recorder' ? {
    sourceWidth: access.capture.sourceWidth, sourceHeight: access.capture.sourceHeight,
    preset: access.capture.preset, allowSoftwareEncoder: access.capture.allowSoftwareEncoder,
    ...(access.capture.encoder === undefined ? {} : { encoder: access.capture.encoder }),
  } : {};
  writeFileSync(path.join(runRoot, 'capture-settings.json'), JSON.stringify(capture, null, 2));
  const prepared = { formatVersion: 1, mode, runRoot, appDirectory, executable: runtime.executable,
    runtimeVersion: runtime.binaryVersion, runtimeSha256: runtime.binarySha256,
    requestedPackages, main: 'dist/main.js', files, approvedAccessDemonstrated: false,
    credentialsSaved: false, diagnosticIdentity: !named, expectedAppId: mode === 'readiness' ? null : access.appId };
  writeFileSync(path.join(runRoot, 'prepared-app.json'), JSON.stringify(prepared, null, 2));
  return { ...prepared, lockFile: path.join(root, '.local', `${mode}.lock`) };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(JSON.stringify(prepareProbe({ mode: process.argv[2] ?? 'readiness' }), null, 2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
