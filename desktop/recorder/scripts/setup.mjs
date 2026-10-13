import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { copyFile, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import { encoderDependencyProvenance, obsPin, obsRuntimeBinaries, recorderCrtBinaries, recorderManifest, recorderModules,
  verifyEncoderSourceProvenance, verifyRecorderRuntime } from '../runtime/libobs.mjs';
import { writeSourceZip } from './source-zip.mjs';

export const recorderHelperSourceFiles = Object.freeze(['native/CMakeLists.txt', 'native/recorder.cpp',
  'native/encoder-policy.hpp', 'native/encoder-policy-tests.cpp', 'native/README.md', 'scripts/setup.mjs',
  'scripts/probe.mjs', 'scripts/source-zip.mjs', 'runtime/libobs.mjs', 'runtime/source-lock.json']);

const desktopRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sourceLockPath = fileURLToPath(new URL('../runtime/source-lock.json', import.meta.url));
const sourceLock = JSON.parse(await readFile(sourceLockPath, 'utf8'));
const obsRelease = `https://github.com/obsproject/obs-studio/releases/download/${obsPin.version}`;

function run(executable, args, { cwd, capture = false, env = process.env } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { cwd, env, windowsHide: true, stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit' });
    let output = '', error = '';
    child.stdout?.on('data', data => { output += data; });
    child.stderr?.on('data', data => { error += data; });
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolve(output) : reject(new Error(`${path.basename(executable)} failed (${code}): ${error}`)));
  });
}

export async function downloadPinned(url, destination, expectedSha256) {
  async function matches(target) {
    try { return createHash('sha256').update(await readFile(target)).digest('hex') === expectedSha256.toLowerCase(); }
    catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  }
  if (await matches(destination)) return destination;
  await mkdir(path.dirname(destination), { recursive: true });
  const temporary = `${destination}.download`;
  const response = await fetch(url, { signal: AbortSignal.timeout(300_000) });
  if (!response.ok || !response.body) throw new Error(`Recorder dependency download failed (${response.status}): ${url}`);
  await pipeline(response.body, createWriteStream(temporary, { flags: 'w' }));
  if (!await matches(temporary)) throw new Error(`Recorder dependency checksum mismatch: ${path.basename(destination)}`);
  await rename(temporary, destination);
  return destination;
}

export async function locateVisualStudio() {
  const vswhere = path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)', 'Microsoft Visual Studio/Installer/vswhere.exe');
  const installations = JSON.parse(await run(vswhere, ['-latest', '-products', '*', '-requires',
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-format', 'json'], { capture: true }));
  if (!installations[0]?.installationPath) throw new Error('Recorder build requires Visual Studio C++ x64 tools and Windows SDK.');
  const root = installations[0].installationPath;
  const toolVersion = (await readFile(path.join(root, 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt'), 'utf8')).trim();
  const toolRoot = path.join(root, 'VC/Tools/MSVC', toolVersion, 'bin/Hostx64/x64');
  const crtVersions = (await readdir(path.join(root, 'VC/Redist/MSVC'), { withFileTypes: true }))
    .filter(entry => entry.isDirectory() && /^\d+\.\d+\.\d+$/.test(entry.name)).map(entry => entry.name)
    .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
  const crt = path.join(root, 'VC/Redist/MSVC', crtVersions[0], 'x64/Microsoft.VC143.CRT');
  const cmake = path.join(root, 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe');
  const ctest = path.join(path.dirname(cmake), 'ctest.exe');
  for (const file of [cmake, ctest, path.join(toolRoot, 'dumpbin.exe'), path.join(toolRoot, 'lib.exe')]) {
    if (!(await lstat(file)).isFile()) throw new Error(`Recorder build tool is missing: ${file}`);
  }
  return { root, toolRoot, toolVersion, crt, cmake, ctest };
}

async function copyRegular(source, destination) {
  if (!(await lstat(source)).isFile() || await realpath(source) !== path.resolve(source))
    throw new Error(`Recorder setup rejects file aliases: ${source}`);
  await mkdir(path.dirname(destination), { recursive: true });
  await copyFile(source, destination);
}
async function copyTree(source, destination) {
  if (!(await lstat(source)).isDirectory() || await realpath(source) !== path.resolve(source))
    throw new Error(`Recorder setup rejects directory aliases: ${source}`);
  for (const entry of await readdir(source, { withFileTypes: true })) {
    if (entry.isDirectory()) await copyTree(path.join(source, entry.name), path.join(destination, entry.name));
    else await copyRegular(path.join(source, entry.name), path.join(destination, entry.name));
  }
}
async function assertExtractionTree(directory) {
  if (!(await lstat(directory)).isDirectory() || await realpath(directory) !== path.resolve(directory))
    throw new Error(`Recorder extraction rejects directory aliases: ${directory}`);
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) await assertExtractionTree(absolute);
    else if (!(await lstat(absolute)).isFile() || await realpath(absolute) !== path.resolve(absolute))
      throw new Error(`Recorder extraction rejects file aliases: ${absolute}`);
  }
}
async function extractZip(archive, directory) {
  await mkdir(directory, { recursive: true });
  await assertExtractionTree(directory);
  await run('tar.exe', ['-xf', archive, '-C', directory]);
}

export async function setupRecorder({ root = desktopRoot } = {}) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('The bundled recorder requires Windows x64.');
  verifyEncoderSourceProvenance(sourceLock);
  const repository = path.resolve(root, '..'), dependencyRoot = path.join(repository, `deps/obs-${obsPin.version}`);
  const outputRoot = path.join(repository, 'artifacts'), destination = path.join(outputRoot, 'desktop-recorder');
  const tools = await locateVisualStudio();
  await mkdir(dependencyRoot, { recursive: true }); await mkdir(outputRoot, { recursive: true });
  if (await realpath(dependencyRoot) !== dependencyRoot || await realpath(outputRoot) !== outputRoot)
    throw new Error('Recorder dependency and output directories must not be aliases.');
  const runtimeArchive = await downloadPinned(`${obsRelease}/${obsPin.runtimeArchive}`,
    path.join(dependencyRoot, obsPin.runtimeArchive), obsPin.runtimeSha256);
  const sourceArchive = await downloadPinned(`${obsRelease}/${obsPin.sourceArchive}`,
    path.join(dependencyRoot, obsPin.sourceArchive), obsPin.sourceSha256);
  const depsArchive = await downloadPinned(`https://github.com/obsproject/obs-deps/releases/download/${obsPin.dependencyVersion}/${obsPin.dependencyArchive}`,
    path.join(dependencyRoot, obsPin.dependencyArchive), obsPin.dependencySha256);
  const depsSource = await downloadPinned(`https://codeload.github.com/obsproject/obs-deps/tar.gz/${obsPin.dependencySourceRevision}`,
    path.join(dependencyRoot, 'obs-deps-sources.tar.gz'), obsPin.dependencySourceSha256);
  const binary = path.join(dependencyRoot, 'binary'), dependencies = path.join(dependencyRoot, 'dependencies');
  await extractZip(runtimeArchive, binary); await extractZip(depsArchive, dependencies);
  const source = path.join(dependencyRoot, 'source'), sourceDir = path.join(source, `obs-studio-${obsPin.version}-sources`);
  await mkdir(source, { recursive: true });
  await assertExtractionTree(source);
  // Only headers are extracted for compilation. The complete upstream archive ships unchanged as source.
  await run('tar.exe', ['-xzf', sourceArchive, '-C', source, `obs-studio-${obsPin.version}-sources/libobs`]);
  const exports = await run(path.join(tools.toolRoot, 'dumpbin.exe'), ['/exports', path.join(binary, 'bin/64bit/obs.dll')], { capture: true });
  const symbols = [...exports.matchAll(/^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+([A-Za-z_][A-Za-z0-9_?@$]*)/gm)].map(match => match[1]);
  if (symbols.length < 500 || !symbols.includes('obs_startup')) throw new Error('Pinned obs.dll export table is invalid.');
  const definition = path.join(dependencyRoot, 'obs.def'), library = path.join(dependencyRoot, 'obs.lib');
  await writeFile(definition, `LIBRARY obs\nEXPORTS\n${symbols.join('\n')}\n`);
  await run(path.join(tools.toolRoot, 'lib.exe'), [`/def:${definition}`, `/out:${library}`, '/machine:x64']);
  const include = path.join(dependencyRoot, 'include'); await mkdir(include, { recursive: true });
  await writeFile(path.join(include, 'obsconfig.h'), '#pragma once\n#define OBS_DATA_PATH "data"\n#define OBS_PLUGIN_PATH "obs-plugins/64bit"\n#define OBS_PLUGIN_DESTINATION "obs-plugins/64bit"\n#define OBS_RELEASE_CANDIDATE 0\n#define OBS_BETA 0\n');
  const staged = await mkdtemp(path.join(outputRoot, '.recorder-'));
  for (const name of obsRuntimeBinaries) await copyRegular(path.join(binary, 'bin/64bit', name), path.join(staged, name));
  for (const name of recorderCrtBinaries) await copyRegular(path.join(tools.crt, name), path.join(staged, name));
  for (const name of ['ffmpeg', 'ffprobe']) await copyRegular(path.join(dependencies, 'bin', `${name}.exe`), path.join(staged, `revu-media-${name}.exe`));
  for (const name of recorderModules) {
    await copyRegular(path.join(binary, `obs-plugins/64bit/${name}.dll`), path.join(staged, `obs-plugins/64bit/${name}.dll`));
    await copyRegular(path.join(binary, `data/obs-plugins/${name}/locale/en-US.ini`), path.join(staged, `data/obs-plugins/${name}/locale/en-US.ini`));
  }
  for (const entry of await readdir(path.join(binary, 'data/libobs'))) {
    if (/^[a-z0-9_]+\.effect$/.test(entry)) await copyRegular(path.join(binary, 'data/libobs', entry), path.join(staged, 'data/libobs', entry));
  }
  for (const name of [...new Set(sourceLock.map(item => item.licenseDirectory ?? item.name))])
    await copyTree(path.join(dependencies, 'licenses', name), path.join(staged, 'licenses/dependencies', name));
  await copyRegular(path.join(binary, 'data/obs-studio/license/gplv2.txt'), path.join(staged, 'licenses/OBS-GPL-2.0.txt'));
  await copyRegular(sourceArchive, path.join(staged, `sources/${obsPin.sourceArchive}`));
  await copyRegular(depsSource, path.join(staged, 'sources/obs-deps-sources.tar.gz'));
  for (const item of sourceLock) {
    const archive = await downloadPinned(item.url, path.join(dependencyRoot, 'corresponding-source', item.archive), item.sha256);
    if (item.bundleArchive) {
      const subset = await mkdtemp(path.join(outputRoot, '.recorder-source-subset-'));
      await run('tar.exe', ['-xzf', archive, '-C', subset, ...item.bundlePaths]);
      const bundled = path.join(staged, 'sources', item.bundleArchive);
      await writeSourceZip(subset, bundled);
      if (createHash('sha256').update(await readFile(bundled)).digest('hex') !== item.bundleSha256)
        throw new Error(`Recorder source subset checksum mismatch: ${item.bundleArchive}`);
    } else await copyRegular(archive, path.join(staged, 'sources', item.archive));
  }
  const ffmpegSource = sourceLock.find(item => item.name === 'FFmpeg');
  const ffmpegSourceArchive = path.join(staged, 'sources', ffmpegSource.archive);
  const ffmpegFiles = await run('tar.exe', ['-tf', ffmpegSourceArchive], { capture: true });
  const gpl3Path = ffmpegFiles.split(/\r?\n/).find(relative => relative.endsWith('/COPYING.GPLv3'));
  if (!gpl3Path) throw new Error('Matching FFmpeg source is missing its GPLv3 license.');
  await writeFile(path.join(staged, 'licenses/FFmpeg-GPL-3.0.txt'), await run('tar.exe', ['-xOf', ffmpegSourceArchive, gpl3Path], { capture: true }));
  const build = path.join(outputRoot, 'desktop-recorder-build-release');
  await run(tools.cmake, ['-S', path.join(root, 'recorder/native'), '-B', build, '-A', 'x64', '-T', 'host=x64',
    '-DCMAKE_TRY_COMPILE_CONFIGURATION=Release', '-DBUILD_TESTING=ON', `-DOBS_SOURCE_DIR=${sourceDir}`, `-DOBS_LIBRARY=${library}`,
    `-DOBS_CONFIG_DIR=${include}`, `-DOBS_RUNTIME_DIR=${staged}`], { cwd: repository });
  await run(tools.cmake, ['--build', build, '--config', 'Release'], { cwd: repository });
  await run(tools.ctest, ['--test-dir', build, '-C', 'Release', '--output-on-failure', '--no-tests=error'], { cwd: repository });
  // Preserve the actual helper sources/build scripts in every bundle; no personal files or binary dependencies.
  const helperSource = await mkdtemp(path.join(outputRoot, '.recorder-source-'));
  for (const relative of recorderHelperSourceFiles)
    await copyRegular(path.join(root, 'recorder', relative), path.join(helperSource, relative));
  await copyRegular(path.join(root, 'recorder/THIRD-PARTY-NOTICES.txt'), path.join(helperSource, 'THIRD-PARTY-NOTICES.txt'));
  await copyRegular(path.join(staged, 'licenses/OBS-GPL-2.0.txt'), path.join(helperSource, 'COPYING.GPLv2'));
  await copyRegular(path.join(staged, 'licenses/FFmpeg-GPL-3.0.txt'), path.join(helperSource, 'COPYING.GPLv3'));
  await writeFile(path.join(helperSource, 'README.txt'), 'Revu recorder helper source\n\nExtract this archive as desktop/recorder in a Revu source checkout.\nRequires Windows x64, Node 22.12 or newer, Visual Studio C++ x64 tools, Windows SDK and CMake.\nRun: node desktop/recorder/scripts/setup.mjs\nThe setup obtains checksum-pinned upstream archives, creates an import library from obs.dll, builds the helper in Release and stages the runtime.\nThe upstream OBS and OBS-deps build source archives, recipes, patches and corresponding dependency source are supplied beside this ZIP.\nThe AMF subset contains the unmodified interface headers copied by the upstream dependency recipe.\n');
  await copyRegular(path.join(root, 'recorder/THIRD-PARTY-NOTICES.txt'), path.join(staged, 'THIRD-PARTY-NOTICES.txt'));
  await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    'Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory($env:REVU_RECORDER_SOURCE, $env:REVU_RECORDER_SOURCE_ZIP)'],
  { env: { ...process.env, REVU_RECORDER_SOURCE: helperSource, REVU_RECORDER_SOURCE_ZIP: path.join(staged, 'sources/revu-recorder-source.zip') } });
  const ffmpegVersion = await run(path.join(staged, 'revu-media-ffmpeg.exe'), ['-version'], { capture: true, cwd: staged });
  await writeFile(path.join(staged, 'sources/BUILD-INFO.json'), JSON.stringify({ obs: obsPin,
    sourceDependencies: sourceLock, encoderDependencies: encoderDependencyProvenance, compiler: tools.toolVersion, ffmpegVersion,
    helperBuild: 'node desktop/recorder/scripts/setup.mjs; Windows x64 Visual Studio C++ tools and Windows SDK',
    upstreamDependencyBuild: 'sources/obs-deps-sources.tar.gz: Build-Dependencies.ps1, deps.ffmpeg recipes and patches',
  }, null, 2));
  await writeFile(path.join(staged, 'runtime-manifest.json'), JSON.stringify(await recorderManifest(staged, { sources: sourceLock }), null, 2));
  await verifyRecorderRuntime(staged);
  let previous;
  try {
    if (!(await lstat(destination)).isDirectory() || await realpath(destination) !== destination)
      throw new Error('Existing recorder output must be a real directory.');
    previous = path.join(outputRoot, `desktop-recorder-previous-${path.basename(staged)}`);
    await rename(destination, previous);
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  try { await rename(staged, destination); }
  catch (error) { if (previous) await rename(previous, destination); throw error; }
  console.log(`Bundled recorder OBS ${obsPin.version}: ${destination}`);
  return destination;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  setupRecorder().catch(error => { console.error(error.message); process.exitCode = 1; });
}
