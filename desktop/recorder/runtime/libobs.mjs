import { createHash } from 'node:crypto';
import { lstat, readFile, readdir, realpath } from 'node:fs/promises';
import path from 'node:path';

export const recorderSourceLock = Object.freeze(JSON.parse(await readFile(new URL('./source-lock.json', import.meta.url), 'utf8')));

export const obsPin = Object.freeze({ version: '32.2.2', architecture: 'x64',
  runtimeArchive: 'OBS-Studio-32.2.2-Windows-x64.zip',
  runtimeSha256: '4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1',
  sourceArchive: 'OBS-Studio-32.2.2-Sources.tar.gz',
  sourceSha256: 'ec81fb66b03e75ddb3076b576f62679c39262e0e9960cef3e17a40dc5d68e6b4',
  dependencyVersion: '2026-07-15', dependencyArchive: 'windows-deps-2026-07-15-x64.zip',
  dependencySha256: '6f90e9598fa10cff5ad23cdcfae49b87868c07bf896b02cd464582b4ce2f2ba9',
  dependencySourceRevision: '8683107a02300923abe4f293920f4b5edc8cb624',
  dependencySourceSha256: '14a95cbdba57e3cbc0f46a156d045f3574e7e1a90b85afb353ef3eaf44ae7e6e',
});
export const recorderModules = Object.freeze(['win-wasapi', 'obs-ffmpeg', 'obs-nvenc', 'obs-qsv11']);
// These pre-signing file hashes come from the same verified official OBS runtime ZIP.
// oneVPL is statically linked into the QSV files; GPU driver libraries stay on the host.
export const encoderBinaryPins = Object.freeze({
  'obs-plugins/64bit/obs-qsv11.dll': '7182838d8cd59d624665a905879b5d8ab328a41fd8e91664ddb490e9d37cc2ad',
  'obs-qsv-test.exe': '894aeebd244459f700c03f17371ebe1ccd9b8f90533910cfd713a01cfd6a4d39',
  'obs-amf-test.exe': '178369e3c40aad4e2af5c5da9faf66e327e60297a4c4627887f5de2bc904b71d',
});
export const encoderDependencyProvenance = Object.freeze({
  obsVersion: obsPin.version, runtimeArchiveSha256: obsPin.runtimeSha256,
  oneVpl: { version: 'v2.14.0', revision: '025d43d086a3e663184cb49febe86152bf05409f',
    sourceArchive: 'vpl-025d43d086a3e663184cb49febe86152bf05409f.tar.gz',
    sourceSha256: '5314d04c5894fe0b464d1afe1c356d34a4685e9e8e08fa30ddff3392d0e7de41',
    recipe: 'deps.windows/70-vpl.ps1', linkage: 'static dispatcher', license: 'MIT' },
  driverRuntime: 'NVIDIA, AMD and Intel GPU encoding implementations are provided by installed Windows GPU drivers.',
});
export const obsRuntimeBinaries = Object.freeze(['obs.dll', 'libobs-d3d11.dll', 'libobs-winrt.dll',
  'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll', 'avutil-60.dll',
  'swresample-6.dll', 'swscale-9.dll', 'libx264-164.dll', 'w32-pthreads.dll', 'zlib.dll',
  'librist.dll', 'srt.dll', 'obs-ffmpeg-mux.exe', 'obs-nvenc-test.exe', 'obs-amf-test.exe', 'obs-qsv-test.exe']);
export const recorderCrtBinaries = Object.freeze(['concrt140.dll', 'msvcp140.dll', 'msvcp140_1.dll',
  'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll', 'vccorlib140.dll',
  'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll']);
export const requiredRecorderFiles = Object.freeze(['revu-recorder.exe', 'revu-media-ffmpeg.exe', 'revu-media-ffprobe.exe',
  ...obsRuntimeBinaries, ...recorderCrtBinaries, ...recorderModules.map(name => `obs-plugins/64bit/${name}.dll`),
  ...recorderModules.map(name => `data/obs-plugins/${name}/locale/en-US.ini`), 'data/libobs/default.effect',
  'licenses/OBS-GPL-2.0.txt', 'licenses/FFmpeg-GPL-3.0.txt', 'licenses/dependencies/vpl/LICENSE.txt',
  'THIRD-PARTY-NOTICES.txt', 'sources/BUILD-INFO.json',
  `sources/${obsPin.sourceArchive}`, 'sources/obs-deps-sources.tar.gz',
  'sources/revu-recorder-source.zip', ...recorderSourceLock.map(item => `sources/${item.bundleArchive ?? item.archive}`), 'runtime-manifest.json']);

/** A deliberate module and asset policy: no OBS UI, browser, network outputs, plugins or game hooks. */
export function isRecorderFile(relative) {
  if (relative.includes('\\') || relative.split('/').some(part => !part || part.startsWith('.'))) return false;
  if (/graphics-hook|inject-helper|get-graphics-offsets|vulkan|\.pdb$/i.test(relative)) return false;
  if (requiredRecorderFiles.includes(relative)) return true;
  if (/^data\/libobs\/[a-z0-9_]+\.effect$/.test(relative)) return true;
  if (/^licenses\/[A-Za-z0-9_. -]+(?:\/[A-Za-z0-9_. -]+)*$/.test(relative)
      && !/\.(?:dll|exe|pdb|zip|gz|tar)$/i.test(relative)) return true;
  return false;
}

export async function recorderFiles(directory) {
  const canonical = path.resolve(directory);
  async function walk(current, prefix = '') {
    if (!(await lstat(current)).isDirectory() || await realpath(current) !== path.resolve(current))
      throw new Error(`Recorder runtime must use real directories: ${current}`);
    const files = [];
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const relative = prefix + entry.name, absolute = path.join(current, entry.name);
      if (entry.isDirectory()) files.push(...await walk(absolute, `${relative}/`));
      else {
        if (!(await lstat(absolute)).isFile() || await realpath(absolute) !== path.resolve(absolute))
          throw new Error(`Recorder runtime rejects aliases: ${relative}`);
        if (!isRecorderFile(relative)) throw new Error(`Unexpected recorder runtime file: ${relative}`);
        files.push(relative);
      }
    }
    return files;
  }
  return (await walk(canonical)).sort();
}

export async function recorderManifest(directory, { sources } = {}) {
  const files = {};
  for (const relative of await recorderFiles(directory)) {
    if (relative === 'runtime-manifest.json') continue;
    const data = await readFile(path.join(directory, relative));
    files[relative] = { bytes: data.length, sha256: createHash('sha256').update(data).digest('hex') };
  }
  return { schema: 1, hashScope: 'before-release-signing', obsVersion: obsPin.version, architecture: obsPin.architecture,
    modules: [...recorderModules], sourceArchives: sources ?? [], encoderDependencies: encoderDependencyProvenance, files };
}

export function verifyEncoderBinaryPins(files) {
  for (const [relative, expected] of Object.entries(encoderBinaryPins)) {
    if (files[relative]?.sha256 !== expected) throw new Error(`Pinned encoder binary checksum mismatch: ${relative}`);
  }
}

export function verifyEncoderSourceProvenance(sources = recorderSourceLock) {
  const source = sources.find(item => item.name === 'vpl'), expected = encoderDependencyProvenance.oneVpl;
  if (!source || source.version !== expected.version || source.revision !== expected.revision
      || source.archive !== expected.sourceArchive || source.sha256 !== expected.sourceSha256 || source.recipe !== expected.recipe)
    throw new Error('Recorder oneVPL source does not match the pinned encoder binaries');
}

/** Verify the complete allowlist and hashes before the recorder can enter a package.
 * Trusted Signing changes EXE hashes later; this manifest describes pre-signing build provenance.
 */
export async function verifyRecorderRuntime(directory) {
  verifyEncoderSourceProvenance();
  const files = await recorderFiles(directory);
  for (const relative of requiredRecorderFiles) {
    if (!files.includes(relative)) throw new Error(`Missing recorder runtime file: ${relative}`);
  }
  const manifest = JSON.parse(await readFile(path.join(directory, 'runtime-manifest.json'), 'utf8'));
  if (manifest.schema !== 1 || manifest.obsVersion !== obsPin.version || manifest.architecture !== 'x64'
      || JSON.stringify(manifest.modules) !== JSON.stringify(recorderModules)
      || JSON.stringify(manifest.sourceArchives) !== JSON.stringify(recorderSourceLock)
      || JSON.stringify(manifest.encoderDependencies) !== JSON.stringify(encoderDependencyProvenance))
    throw new Error('Recorder runtime manifest does not match the pinned OBS version and modules');
  const actual = await recorderManifest(directory);
  if (JSON.stringify(Object.keys(manifest.files).sort()) !== JSON.stringify(Object.keys(actual.files).sort()))
    throw new Error('Recorder runtime manifest file inventory differs');
  for (const [relative, metadata] of Object.entries(actual.files)) {
    if (manifest.files[relative]?.sha256 !== metadata.sha256 || manifest.files[relative]?.bytes !== metadata.bytes)
      throw new Error(`Recorder runtime integrity mismatch: ${relative}`);
  }
  for (const [relative, expected] of [[`sources/${obsPin.sourceArchive}`, obsPin.sourceSha256],
    ['sources/obs-deps-sources.tar.gz', obsPin.dependencySourceSha256],
    ...recorderSourceLock.map(item => [`sources/${item.bundleArchive ?? item.archive}`, item.bundleSha256 ?? item.sha256])]) {
    if (actual.files[relative]?.sha256 !== expected) throw new Error(`Recorder source checksum mismatch: ${relative}`);
  }
  verifyEncoderBinaryPins(actual.files);
  return manifest;
}
