import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// The launchers and the packager share this package.json, lockfile and installed runtime.
export const desktopRoot = path.resolve(fileURLToPath(new URL('..', import.meta.url)));

/** Child processes never inherit runtime overrides, download mirrors or Node injectors. */
export function cleanRuntimeEnvironment(environment = process.env) {
  const clean = { ...environment };
  for (const name of Object.keys(clean)) {
    if (/^(ELECTRON_|npm_config_electron_|npm_config_platform$|npm_config_arch$)/i.test(name)
        || /^(NODE_OPTIONS|NODE_PATH|force_no_cache)$/i.test(name)) delete clean[name];
  }
  return clean;
}

function resolvePinnedPackage(root) {
  const manifest = path.join(path.resolve(root), 'package.json');
  const project = JSON.parse(readFileSync(manifest, 'utf8'));
  const pinnedVersion = project.devDependencies?.electron ?? project.dependencies?.electron;
  if (!/^\d+\.\d+\.\d+$/.test(pinnedVersion ?? '')) throw new Error('runtime-pin-required');
  // Resolving metadata does not run index.js, whose require can install the runtime.
  const packageFile = createRequire(manifest).resolve('electron/package.json');
  const installed = JSON.parse(readFileSync(packageFile, 'utf8'));
  if (installed.name !== 'electron' || installed.version !== pinnedVersion) throw new Error('package-version-mismatch');
  return { pinnedVersion, installed, packageDirectory: path.dirname(packageFile) };
}

/** Read-only until verifyBinary explicitly executes the installed runtime as Node; never installs. */
export function inspectRuntime({ root = desktopRoot, verifyBinary = true } = {}) {
  const result = { pinnedVersion: null, packageVersion: null, executable: null, versionFile: null,
    binaryVersion: null, binarySha256: null, installed: false, failure: null };
  try {
    const { pinnedVersion, installed, packageDirectory } = resolvePinnedPackage(root);
    result.pinnedVersion = pinnedVersion; result.packageVersion = installed.version;
    const executableName = readFileSync(path.join(packageDirectory, 'path.txt'), 'utf8').trim();
    if (executableName !== 'electron.exe') throw new Error('unexpected-runtime-path');
    const executable = path.join(packageDirectory, 'dist', executableName);
    if (!existsSync(executable) || !statSync(executable).isFile()) throw new Error('binary-missing');
    result.executable = executable;
    result.versionFile = readFileSync(path.join(packageDirectory, 'dist', 'version'), 'utf8').trim().replace(/^v/, '');
    if (result.versionFile !== pinnedVersion) throw new Error('version-file-mismatch');
    if (!verifyBinary) { result.failure = 'binary-not-executed'; return result; }
    const environment = cleanRuntimeEnvironment();
    environment.ELECTRON_RUN_AS_NODE = '1';
    const output = execFileSync(executable, ['-e', 'process.stdout.write(JSON.stringify({electron:process.versions.electron,arch:process.arch}))'],
      { encoding: 'utf8', timeout: 15_000, maxBuffer: 8192, windowsHide: true,
        env: environment, stdio: ['ignore', 'pipe', 'ignore'] });
    const actual = JSON.parse(output);
    result.binaryVersion = actual.electron;
    if (actual.electron !== pinnedVersion || actual.arch !== 'x64') throw new Error('binary-version-or-architecture-mismatch');
    result.binarySha256 = createHash('sha256').update(readFileSync(executable)).digest('hex');
    result.installed = true;
  } catch (error) {
    const allowed = ['runtime-pin-required', 'package-version-mismatch', 'unexpected-runtime-path', 'binary-missing',
      'version-file-mismatch', 'binary-version-or-architecture-mismatch'];
    result.failure = allowed.includes(error.message) ? error.message : 'runtime-unavailable-or-unverifiable';
  }
  return result;
}

export function installPinnedRuntime({ root = desktopRoot } = {}) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('The desktop runtime requires Windows x64.');
  let current = inspectRuntime({ root });
  if (current.installed) return current;
  let packageDirectory;
  try { ({ packageDirectory } = resolvePinnedPackage(root)); }
  catch { throw new Error('Run npm ci --ignore-scripts in desktop for the pinned package first.'); }
  try {
    // The package's own installer downloads the pinned release and checks it against its bundled checksums.
    execFileSync(process.execPath, [path.join(packageDirectory, 'install.js')], {
      cwd: root, env: cleanRuntimeEnvironment(), stdio: 'ignore', windowsHide: true, timeout: 180_000,
    });
  } catch { throw new Error('Pinned runtime installation failed.'); }
  current = inspectRuntime({ root });
  if (!current.installed) throw new Error('Installed runtime failed actual executable/version verification.');
  return current;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const mode = process.argv[2] ?? 'inspect';
    if (!['inspect', 'install'].includes(mode)) throw new Error('Use inspect or install.');
    const result = mode === 'install' ? installPinnedRuntime() : inspectRuntime();
    console.log(JSON.stringify(result, null, 2));
    if (!result.installed) process.exitCode = 1;
  } catch { console.error('Desktop runtime setup failed; install the pinned desktop dependencies and retry.'); process.exitCode = 1; }
}
