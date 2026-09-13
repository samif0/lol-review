import { spawn } from 'node:child_process';
import { copyFile, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rename, unlink, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import asar from '@electron/asar';
import config from '../electron-builder.config.mjs';
import { cleanRuntimeEnvironment, desktopRoot, inspectRuntime } from '../runtime/overwolf.mjs';

const uiExtensions = new Set(['.html', '.js', '.mjs', '.css', '.svg', '.png', '.jpg', '.webp', '.ico', '.woff', '.woff2', '.ttf']);
const requiredAppFiles = ['package.json', 'electron/main.mjs', 'electron/preload.cjs',
  'electron/branding/revu.ico', 'ui/index.html', 'ui/platform/index.mjs', 'runtime/overwolf.mjs'];

/** Explicit source areas; adding a new asset directory requires a deliberate change. */
export function isAppFile(relative) {
  if (relative.includes('\\') || relative.split('/').some(part => !part || part.startsWith('.'))) return false;
  if (/\.(?:test|spec|local)\./i.test(relative)) return false;
  if (relative === 'package.json' || relative === 'runtime/overwolf.mjs'
      || relative === 'electron/branding/revu.ico') return true;
  if (/^electron\/[^/]+\.(mjs|cjs)$/.test(relative)) return true;
  if (!/^ui\/(?:[^/]+|(?:fonts|platform)\/[^/]+)$/.test(relative)) return false;
  return uiExtensions.has(path.posix.extname(relative)) || /^ui\/sample-[^/]+\.json$/.test(relative);
}

async function copyRegular(source, destination) {
  if (!(await lstat(source)).isFile() || await realpath(source) !== path.resolve(source))
    throw new Error(`Packaging requires an unaliased regular file: ${source}`);
  await mkdir(path.dirname(destination), { recursive: true });
  await copyFile(source, destination);
}

export async function stageApp(root, destination) {
  const files = ['package.json', 'runtime/overwolf.mjs', 'electron/branding/revu.ico'];
  for (const directory of ['electron', 'ui', 'ui/fonts', 'ui/platform']) {
    const source = path.join(root, directory);
    if (!(await lstat(source)).isDirectory()) throw new Error(`Packaging requires a real directory: ${source}`);
    for (const entry of await readdir(source, { withFileTypes: true })) {
      const relative = `${directory}/${entry.name}`;
      if (isAppFile(relative)) files.push(relative);
    }
  }
  const unique = [...new Set(files)].sort();
  for (const relative of unique) await copyRegular(path.join(root, relative), path.join(destination, relative));
  for (const relative of requiredAppFiles) {
    if (!unique.includes(relative)) throw new Error(`Missing required app file: ${relative}`);
  }
  const manifest = JSON.parse(await readFile(path.join(destination, 'package.json'), 'utf8'));
  if (manifest.main !== 'electron/main.mjs' || !Array.isArray(manifest.overwolf?.packages)
      || manifest.overwolf.packages.length !== 0) throw new Error('Unexpected desktop entry or gaming package activation');
  return unique;
}

/** Walk only generated output; fail on aliases instead of copying outside the build. */
async function copyTree(source, destination) {
  if (!(await lstat(source)).isDirectory() || await realpath(source) !== path.resolve(source))
    throw new Error(`Expected a real build directory: ${source}`);
  await mkdir(destination, { recursive: true });
  for (const entry of await readdir(source, { withFileTypes: true })) {
    const from = path.join(source, entry.name), to = path.join(destination, entry.name);
    if (entry.isDirectory()) await copyTree(from, to);
    else await copyRegular(from, to);
  }
}

export async function verifyPackage(directory) {
  for (const relative of ['revu-desktop.exe', 'Revu.Sidecar.exe', 'Revu.Sidecar.dll',
    'Revu.Sidecar.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'resources/app.asar', 'resources/branding/revu.ico']) {
    if (!(await lstat(path.join(directory, relative))).isFile()) throw new Error(`Missing packaged file: ${relative}`);
  }
  const archive = path.join(directory, 'resources/app.asar');
  asar.uncache(archive);
  const files = new Set();
  for (const entry of asar.listPackage(archive)) {
    const item = entry.replaceAll('\\', '/').replace(/^\//, '');
    const metadata = asar.statFile(archive, path.normalize(item), false);
    if (metadata.link || metadata.unpacked) throw new Error(`Aliased or unpacked app entry: ${item}`);
    if (metadata.files) {
      if (!['electron', 'electron/branding', 'runtime', 'ui', 'ui/fonts', 'ui/platform'].includes(item))
        throw new Error(`Unexpected packaged app directory: ${item}`);
    } else {
      if (!isAppFile(item)) throw new Error(`Unexpected packaged app file: ${item}`);
      files.add(item);
    }
  }
  for (const item of requiredAppFiles) if (!files.has(item)) throw new Error(`Missing packaged app file: ${item}`);
  const manifest = JSON.parse(asar.extractFile(archive, 'package.json').toString('utf8'));
  if (manifest.main !== 'electron/main.mjs' || manifest.overwolf?.packages?.length !== 0)
    throw new Error('Packaged manifest changed its entry or enabled gaming packages');
}

function run(executable, args, cwd, env) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { cwd, env, stdio: 'inherit', windowsHide: true });
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolve() : reject(new Error(`${path.basename(executable)} failed (${code})`)));
  });
}

/** The only rename destinations are children of this script's canonical output root. */
export async function activatePackage(outputRoot, builtDirectory) {
  const root = await realpath(outputRoot), built = await realpath(builtDirectory);
  if (root !== path.resolve(outputRoot) || built !== path.resolve(builtDirectory)
      || !built.startsWith(root + path.sep) || path.basename(built) !== 'win-unpacked')
    throw new Error('Package output aliases or outside build paths are not supported');
  const destination = path.join(root, 'win-unpacked');
  let previous;
  try {
    const existing = await lstat(destination);
    if (!existing.isDirectory() || await realpath(destination) !== destination)
      throw new Error('Existing package output is not a real directory');
    previous = path.join(root, `previous-${path.basename(path.dirname(path.dirname(built)))}`);
    await rename(destination, previous);
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  try { await rename(built, destination); }
  catch (error) { if (previous) await rename(previous, destination); throw error; }
  return destination;
}

export async function packageDesktop({ root = desktopRoot } = {}) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Packaging requires Windows x64.');
  const runtime = inspectRuntime({ root });
  if (!runtime.installed) throw new Error('Run npm run runtime:install before packaging.');
  const require = createRequire(path.join(root, 'package.json'));
  const builderPackage = require.resolve('@overwolf/ow-electron-builder/package.json');
  const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
  const installedBuilder = JSON.parse(await readFile(builderPackage, 'utf8'));
  if (installedBuilder.version !== manifest.devDependencies?.['@overwolf/ow-electron-builder'])
    throw new Error('Installed Overwolf builder does not match the desktop pin.');
  const repository = path.resolve(root, '..');
  const outputRoot = path.join(repository, 'artifacts/desktop-package');
  await mkdir(outputRoot, { recursive: true });
  if (await realpath(outputRoot) !== outputRoot) throw new Error('Package output aliases are not supported');
  const buildRoot = await mkdtemp(path.join(outputRoot, '.build-'));
  const staged = path.join(buildRoot, 'app'), sidecar = path.join(buildRoot, 'sidecar');
  const files = await stageApp(root, staged);
  const env = cleanRuntimeEnvironment();
  env.CSC_IDENTITY_AUTO_DISCOVERY = 'false';
  await run('dotnet', ['publish', path.join(repository, 'src/Revu.Sidecar/Revu.Sidecar.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:Platform=x64',
    '--artifacts-path', path.join(buildRoot, 'dotnet'), '-o', sidecar], repository, env);
  const builderConfig = { ...config, electronVersion: runtime.pinnedVersion,
    electronDist: path.dirname(runtime.executable), directories: { app: staged, output: path.join(buildRoot, 'output') },
    extraResources: [{ from: path.join(staged, 'electron/branding/revu.ico'), to: 'branding/revu.ico' }],
    win: { ...config.win, icon: path.join(staged, 'electron/branding/revu.ico') } };
  const configPath = path.join(buildRoot, 'builder.json');
  await writeFile(configPath, JSON.stringify(builderConfig, null, 2));
  await run(process.execPath, [path.join(path.dirname(builderPackage), 'cli.js'),
    '--dir', '--win', '--x64', '--publish', 'never', '--config', configPath], root, env);
  const built = path.join(buildRoot, 'output/win-unpacked');
  // A custom unpacked runtime retains Electron's demonstration app by default.
  await unlink(path.join(built, 'resources/default_app.asar')).catch(error => { if (error.code !== 'ENOENT') throw error; });
  const { rcedit } = await import('rcedit');
  await rcedit(path.join(built, 'revu-desktop.exe'), {
    icon: builderConfig.win.icon,
    'file-version': manifest.version.split('-')[0],
    'product-version': manifest.version.split('-')[0],
    'version-string': { ProductName: 'Revu', FileDescription: manifest.description,
      InternalName: 'Revu', OriginalFilename: 'revu-desktop.exe', ProductVersion: manifest.version },
    'requested-execution-level': 'asInvoker',
  });
  await copyTree(sidecar, built);
  const ffmpeg = path.join(repository, 'deps/ffmpeg.exe');
  try { await copyRegular(ffmpeg, path.join(built, 'ffmpeg.exe')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  await verifyPackage(built);
  const directory = await activatePackage(outputRoot, built);
  await writeFile(path.join(outputRoot, 'package-manifest.json'), JSON.stringify({ version: manifest.version,
    runtimeVersion: runtime.pinnedVersion, builderVersion: installedBuilder.version, directory, appFiles: files }, null, 2));
  console.log(`Packaged Revu ${manifest.version}: ${directory}`);
  return directory;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  packageDesktop().catch(error => { console.error(error.message); process.exitCode = 1; });
}
